// Runs the relay locally with `wrangler dev` (no Cloudflare account needed, nothing is deployed) and
// checks the protocol and the limits from real WebSocket clients. `npm test`.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { addressGroup } from "../src/address.js";

const PORT = 18787;
const BASE = `http://127.0.0.1:${PORT}`;
let dev;

before(async () => {
  dev = spawn("npx", ["wrangler", "dev", "--port", String(PORT), "--ip", "127.0.0.1", "--show-interactive-dev-session=false"], {
    cwd: new URL("..", import.meta.url),
    stdio: ["ignore", "pipe", "pipe"],
  });
  let output = "";
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("wrangler dev did not start:\n" + output)), 60_000);
    const look = (chunk) => {
      output += chunk;
      if (output.includes("Ready on")) { clearTimeout(timer); resolve(); }
    };
    dev.stdout.on("data", look);
    dev.stderr.on("data", look);
    dev.on("exit", (code) => reject(new Error(`wrangler dev exited with ${code}:\n${output}`)));
  });
});

after(() => dev?.kill("SIGINT"));

function u64(value) {
  const bytes = new Uint8Array(8);
  new DataView(bytes.buffer).setBigUint64(0, BigInt(value), true);
  return bytes;
}

function readU64(bytes, offset) {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getBigUint64(offset, true);
}

function joinFrame(token, member, secret = randomBytes(16), version = 2) {
  const frame = new Uint8Array(58);
  frame[0] = 0x4a;
  frame[1] = version;
  frame.set(token, 2);
  frame.set(u64(member), 34);
  frame.set(secret, 42);
  return frame;
}

function tagOf(secret) {
  return createHash("sha256").update(secret).digest().subarray(0, 8);
}

function dataFrame(target, payload) {
  const frame = new Uint8Array(9 + payload.length);
  frame[0] = 0x44;
  frame.set(u64(target), 1);
  frame.set(payload, 9);
  return frame;
}

/** A client that queues what it receives, so tests can wait for the next frame or the close. */
async function connect(region = "apac") {
  const ws = new WebSocket(`ws://127.0.0.1:${PORT}/v1/relay/${region}`);
  ws.binaryType = "arraybuffer";
  const frames = [];
  const waiters = [];
  let closed = null;
  const closeWaiters = [];
  ws.onmessage = (e) => {
    const bytes = new Uint8Array(e.data);
    const waiter = waiters.shift();
    if (waiter) waiter(bytes); else frames.push(bytes);
  };
  ws.onclose = (e) => {
    closed = { code: e.code, reason: e.reason };
    closeWaiters.splice(0).forEach((w) => w(closed));
  };
  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = () => reject(new Error("could not connect"));
  });
  return {
    ws,
    send: (bytes) => ws.send(bytes),
    next: (ms = 3000) => frames.length ? Promise.resolve(frames.shift()) : new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("no frame")), ms);
      waiters.push((bytes) => { clearTimeout(timer); resolve(bytes); });
    }),
    nothingWithin: async (ms) => {
      await new Promise((r) => setTimeout(r, ms));
      return frames.length === 0;
    },
    closed: (ms = 3000) => closed ? Promise.resolve(closed) : new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("not closed")), ms);
      closeWaiters.push((c) => { clearTimeout(timer); resolve(c); });
    }),
    close: () => ws.close(),
  };
}

async function joined(token, member, region, secret = randomBytes(16)) {
  const client = await connect(region);
  client.send(joinFrame(token, member, secret));
  const welcome = await client.next();
  assert.equal(welcome[0], 0x57);
  return { client, welcome, secret };
}

function welcomeEntries(welcome) {
  const regionLength = welcome[2];
  const count = welcome[3 + regionLength];
  return Array.from({ length: count }, (_, i) => {
    const at = 4 + regionLength + 16 * i;
    return { member: readU64(welcome, at), tag: Buffer.from(welcome.subarray(at + 8, at + 16)) };
  });
}

function welcomeMembers(welcome) {
  return welcomeEntries(welcome).map((e) => e.member);
}

test("members see each other and data reaches the target or every other member with the sender's ID", async () => {
  const token = randomBytes(32);
  const a = await joined(token, 101);
  assert.equal(new TextDecoder().decode(a.welcome.subarray(3, 3 + a.welcome[2])), "apac");
  assert.deepEqual(welcomeMembers(a.welcome), []);
  const b = await joined(token, 102);
  assert.deepEqual(welcomeEntries(b.welcome), [{ member: 101n, tag: tagOf(a.secret) }]);
  const joinedNote = await a.client.next();
  assert.equal(joinedNote[0], 0x2b);
  assert.equal(readU64(joinedNote, 1), 102n);
  assert.deepEqual(Buffer.from(joinedNote.subarray(9, 17)), tagOf(b.secret));
  const c = await joined(token, 103);
  await a.client.next();
  await b.client.next();

  a.client.send(dataFrame(102, new Uint8Array([1, 2, 3])));
  const toB = await b.client.next();
  assert.equal(toB[0], 0x44);
  assert.equal(readU64(toB, 1), 101n);
  assert.deepEqual([...toB.subarray(9)], [1, 2, 3]);
  assert.ok(await c.client.nothingWithin(200), "a targeted frame reached another member");

  b.client.send(dataFrame(0, new Uint8Array([9])));
  for (const other of [a, c]) {
    const got = await other.client.next();
    assert.equal(readU64(got, 1), 102n);
    assert.deepEqual([...got.subarray(9)], [9]);
  }
  assert.ok(await b.client.nothingWithin(200), "a broadcast came back to its sender");

  c.client.close();
  for (const other of [a, b]) {
    const left = await other.client.next();
    assert.equal(left[0], 0x2d);
    assert.equal(readU64(left, 1), 103n);
  }
  a.client.close();
  b.client.close();
});

test("rooms are separate", async () => {
  const a = await joined(randomBytes(32), 201);
  const b = await joined(randomBytes(32), 202);
  assert.deepEqual(welcomeMembers(b.welcome), []);
  a.client.send(dataFrame(0, new Uint8Array([7])));
  assert.ok(await b.client.nothingWithin(200));
  a.client.close();
  b.client.close();
});

test("a fifth member is refused", async () => {
  const token = randomBytes(32);
  const members = [];
  for (let i = 0; i < 4; i++) members.push((await joined(token, 300 + i)).client);
  const fifth = await connect();
  fifth.send(joinFrame(token, 399));
  assert.equal((await fifth.closed()).code, 4001);
  members.forEach((m) => m.close());
});

test("a member reconnecting with its secret replaces its old connection without a leave notice", async () => {
  const token = randomBytes(32);
  const a = await joined(token, 401);
  const b1 = await joined(token, 402);
  await a.client.next();
  const b2 = await joined(token, 402, undefined, b1.secret);
  assert.deepEqual(welcomeMembers(b2.welcome), [401n]);
  assert.equal((await b1.client.closed()).code, 4000);
  const note = await a.client.next();
  assert.equal(note[0], 0x2b, "the others are told the member is back, not that it left");
  assert.deepEqual(Buffer.from(note.subarray(9, 17)), tagOf(b1.secret), "the same secret gives the same tag");
  a.client.send(dataFrame(402, new Uint8Array([5])));
  assert.deepEqual([...(await b2.client.next()).subarray(9)], [5]);
  a.client.close();
  b2.client.close();
});

test("a join for a member ID held under another secret is refused and the holder stays", async () => {
  const token = randomBytes(32);
  const host = await joined(token, 451);
  const client = await joined(token, 452);
  await host.client.next();
  const hijack = await connect();
  hijack.send(joinFrame(token, 451));
  assert.equal((await hijack.closed()).code, 4009);
  assert.ok(await client.client.nothingWithin(200), "the room heard about the refused join");
  host.client.send(dataFrame(0, new Uint8Array([6])));
  const got = await client.client.next();
  assert.equal(readU64(got, 1), 451n);
  assert.deepEqual([...got.subarray(9)], [6]);

  // Once the holder is gone, its ID is free; the others see the new holder's tag.
  host.client.close();
  assert.equal((await client.client.next())[0], 0x2d);
  const other = await joined(token, 451);
  const note = await client.client.next();
  assert.equal(note[0], 0x2b);
  assert.deepEqual(Buffer.from(note.subarray(9, 17)), tagOf(other.secret));
  assert.notDeepEqual(tagOf(other.secret), tagOf(host.secret));
  client.client.close();
  other.client.close();
});

test("malformed input closes the connection", async () => {
  const text = await connect();
  text.ws.send("hello");
  assert.equal((await text.closed()).code, 4002);

  const short = await connect();
  short.send(new Uint8Array([0x4a, 2, 2]));
  assert.equal((await short.closed()).code, 4002);

  const zero = await connect();
  zero.send(joinFrame(randomBytes(32), 0));
  assert.equal((await zero.closed()).code, 4002);

  const version = await connect();
  version.send(joinFrame(randomBytes(32), 5, randomBytes(16), 9));
  assert.equal((await version.closed()).code, 4008);

  // The join of protocol 1, without a secret.
  const old = await connect();
  old.send(joinFrame(randomBytes(32), 5, randomBytes(16), 1).subarray(0, 42));
  assert.equal((await old.closed()).code, 4008);

  const noSecret = await connect();
  noSecret.send(joinFrame(randomBytes(32), 5).subarray(0, 42));
  assert.equal((await noSecret.closed()).code, 4002);

  const a = await joined(randomBytes(32), 501);
  a.client.send(new Uint8Array([0x58, 0, 0, 0, 0, 0, 0, 0, 0]));
  assert.equal((await a.client.closed()).code, 4002);
});

test("a frame over 64 KB is refused", async () => {
  const a = await joined(randomBytes(32), 601);
  a.client.send(dataFrame(0, new Uint8Array(64 * 1024 + 1)));
  assert.equal((await a.client.closed()).code, 4004);
});

test("a flood is cut off", async () => {
  const a = await joined(randomBytes(32), 701);
  for (let i = 0; i < 400; i++) a.client.send(dataFrame(0, new Uint8Array([1])));
  assert.equal((await a.client.closed()).code, 4003);
});

test("one address can hold at most 8 connections", async () => {
  await new Promise((r) => setTimeout(r, 500)); // let the earlier tests' closes reach the relay
  const open = [];
  for (let i = 0; i < 8; i++) open.push(await connect());
  await assert.rejects(connect());
  open.forEach((c) => c.close());
});

test("plain requests", async () => {
  assert.equal((await fetch(`${BASE}/v1/relay/apac`)).status, 426);
  await assert.rejects(connect("mars"), "an unknown region was accepted");
  assert.equal((await fetch(`${BASE}/nothing`)).status, 404);
  assert.equal((await fetch(`${BASE}/probe`)).status, 404);
});

test("IPv6 addresses count by their /64", () => {
  assert.equal(addressGroup("203.0.113.7"), "203.0.113.7");
  assert.equal(addressGroup("::ffff:203.0.113.7"), "203.0.113.7");
  assert.equal(addressGroup("2001:db8:1:2:aaaa:bbbb:cccc:dddd"), "2001:db8:1:2::/64");
  assert.equal(addressGroup("2001:0db8:0001:0002::1"), "2001:db8:1:2::/64");
  assert.equal(addressGroup("2001:db8::"), "2001:db8:0:0::/64");
  assert.notEqual(addressGroup("2001:db8:1:3::1"), addressGroup("2001:db8:1:2::1"));
  assert.equal(addressGroup("not:an::address:x"), "not:an::address:x");
});
