// QuickMatch relay: forwards game messages and voice between the members of one match. They are
// encrypted end to end by the players (each player's keys travel over Steam lobby chat), so the
// relay only sees opaque bytes, their sizes and timings, and the connecting addresses. Nothing is
// stored. README.md describes the protocol, the limits and how to run your own copy.
import { DurableObject } from "cloudflare:workers";
import { addressGroup } from "./address.js";

const PROTOCOL_VERSION = 2;

// One Durable Object per region serves every match whose host connected there; members of a
// match all join the host's region. The hint only matters when the object is first created.
const REGIONS = { apac: "apac", na: "enam", eu: "weur" };
const CONTINENT_REGION = { AS: "apac", OC: "apac", NA: "na", SA: "na", EU: "eu", AF: "eu" };
const DEFAULT_REGION = "na";

const LIMITS = {
  membersPerRoom: 4,
  maxPayloadBytes: 64 * 1024,
  roomsPerRegion: 150,
  socketsPerRegion: 400,
  socketsPerAddress: 8,
  messagesPerSecond: 100,
  messageBurst: 200,
  // All connections of one address (an IPv4 address, or an IPv6 /64) together, per hour.
  messagesPerAddressPerHour: 250_000,
  joinWithinMs: 15_000,
  idleCloseMs: 120_000,
  sweepEveryMs: 10_000,
  // How often a connection's last message time is written to its attachment, which outlives hibernation.
  lastAtStepMs: 10_000,
};
const HOUR_MS = 3_600_000;

// Frames. All integers are little endian; member IDs are the players' Steam IDs. A member's tag is
// the first 8 bytes of the SHA-256 of the secret it joined with.
const FRAME_JOIN = 0x4a; //           client: 'J' | version u8 | room token [32] | member u64 | secret [16]
const FRAME_WELCOME = 0x57; //        relay:  'W' | version u8 | region length u8 | region | count u8 | (member u64 | tag [8]) * count
const FRAME_DATA = 0x44; //           client: 'D' | target u64 (0 = every other member) | payload
//                                    relay:  'D' | sender u64 | payload
const FRAME_MEMBER_JOINED = 0x2b; //  relay:  '+' | member u64 | tag [8]
const FRAME_MEMBER_LEFT = 0x2d; //    relay:  '-' | member u64
const SECRET_BYTES = 16;
const TAG_BYTES = 8;
const JOIN_BYTES = 42 + SECRET_BYTES;
const DATA_HEADER_BYTES = 9;

// Close codes; 4000 to 4999 are free for applications.
const CLOSE = {
  replaced: [4000, "replaced by a newer connection of the same member"],
  roomFull: [4001, "room full"],
  badFrame: [4002, "malformed frame"],
  rate: [4003, "too many messages"],
  tooBig: [4004, "frame too large"],
  busy: [4005, "relay busy"],
  notJoined: [4006, "no join in time"],
  idle: [4007, "idle"],
  version: [4008, "unsupported protocol version"],
  taken: [4009, "member ID in use"],
  budget: [4010, "address over its hourly message budget"],
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const relay = /^\/v1\/relay\/([a-z]+)$/.exec(url.pathname);
    if (relay) {
      if ((request.headers.get("Upgrade") ?? "").toLowerCase() !== "websocket") {
        return new Response("Expected a WebSocket upgrade.\n", { status: 426 });
      }
      const region = relay[1] === "auto" ? regionFor(request.cf?.continent) : relay[1];
      if (!Object.hasOwn(REGIONS, region)) {
        return new Response("Unknown region.\n", { status: 404 });
      }
      const stub = env.RELAY.get(env.RELAY.idFromName(region), { locationHint: REGIONS[region] });
      const headers = new Headers(request.headers);
      // The hub gets a tag of the address instead of the address, to apply the per-address limits.
      headers.set("X-Relay-Address", await addressTag(request.headers.get("CF-Connecting-IP") ?? ""));
      headers.set("X-Relay-Region", region);
      return stub.fetch(new Request(request, { headers }));
    }
    if (url.pathname === "/") {
      return new Response("QuickMatch relay. It forwards encrypted game messages and voice between the players of a Slay the Spire 2 Quick Match game.\n", {
        headers: { "content-type": "text/plain; charset=utf-8" },
      });
    }
    return new Response("Not found.\n", { status: 404 });
  },
};

function regionFor(continent) {
  return CONTINENT_REGION[continent] ?? DEFAULT_REGION;
}

// The first 8 bytes of an unsalted SHA-256 of the address group. It keeps the raw address out of the
// hub's memory and socket tags, nothing more: it is not anonymous, since anyone holding a tag could
// find an IPv4 address by trying them all. Tags live only in memory, like everything else here.
async function addressTag(address) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(addressGroup(address)));
  return "a:" + hex(new Uint8Array(digest).subarray(0, 8));
}


export class RelayHub extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    // Answers keep-alive pings without waking the object, so idle members cost nothing.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
    // Everything a connection needs to survive hibernation is in its attachment:
    // { region, address, at (opened, 0 once out of every room), lastAt (last message, roughly),
    //   room, member, secret (hex), tag (hex), joining }.
    this.rooms = null; // room -> Map(member -> WebSocket); rebuilt from attachments after hibernation
    this.buckets = new Map(); // WebSocket -> rate state; empty again after hibernation
    this.addressUse = new Map(); // address tag -> { since, count } for the hourly budget; empty again after hibernation
    this.lastSweep = 0;
  }

  async fetch(request) {
    const address = request.headers.get("X-Relay-Address") ?? "a:";
    const region = request.headers.get("X-Relay-Region") ?? "";
    const now = Date.now();
    this.sweep(now);
    if (this.ctx.getWebSockets().length >= LIMITS.socketsPerRegion ||
        this.ctx.getWebSockets(address).length >= LIMITS.socketsPerAddress ||
        this.overBudget(address, now)) {
      return new Response("Relay busy.\n", { status: 503 });
    }
    const [client, server] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(server, [address]);
    server.serializeAttachment({ region, address, at: now, lastAt: now, room: null, member: null, secret: null, tag: null, joining: false });
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, message) {
    const now = Date.now();
    this.sweep(now);
    if (typeof message === "string") return this.closeWith(ws, CLOSE.badFrame);
    const bytes = new Uint8Array(message);
    if (bytes.length > DATA_HEADER_BYTES + LIMITS.maxPayloadBytes) return this.closeWith(ws, CLOSE.tooBig);
    if (!this.take(ws, now)) return this.closeWith(ws, CLOSE.rate);
    const state = ws.deserializeAttachment();
    if (!this.spend(state.address, now)) return this.closeWith(ws, CLOSE.budget);
    if (state.joining) return this.closeWith(ws, CLOSE.badFrame);
    if (state.room === null) return this.join(ws, state, bytes, now);
    if (bytes.length < DATA_HEADER_BYTES || bytes[0] !== FRAME_DATA) return this.closeWith(ws, CLOSE.badFrame);
    if (now - state.lastAt >= LIMITS.lastAtStepMs) {
      state.lastAt = now;
      ws.serializeAttachment(state);
    }
    const members = this.roomMap().get(state.room);
    if (!members) return;
    const target = readU64(bytes, 1);
    // Forward the same bytes with the target replaced by the sender.
    bytes.set(u64Bytes(BigInt(state.member)), 1);
    if (target === 0n) {
      for (const other of members.values()) {
        if (other !== ws) this.sendSafe(other, bytes);
      }
    } else {
      const other = members.get(target.toString());
      if (other && other !== ws) this.sendSafe(other, bytes);
    }
  }

  async webSocketClose(ws) {
    this.leave(ws);
    try { ws.close(1000, "closed"); } catch { /* already closed */ }
  }

  async webSocketError(ws) {
    this.leave(ws);
  }

  // The first join of a member ID in a room holds it with the joiner's secret. A later join for that
  // ID replaces the earlier connection only if it brings the same secret (the same player
  // reconnecting); anyone else is refused, so one member cannot push another out of the room.
  async join(ws, state, bytes, now) {
    if (bytes.length < 2 || bytes[0] !== FRAME_JOIN) return this.closeWith(ws, CLOSE.badFrame);
    if (bytes[1] !== PROTOCOL_VERSION) return this.closeWith(ws, CLOSE.version);
    if (bytes.length !== JOIN_BYTES) return this.closeWith(ws, CLOSE.badFrame);
    const room = hex(bytes.subarray(2, 34));
    const memberId = readU64(bytes, 34);
    if (memberId === 0n) return this.closeWith(ws, CLOSE.badFrame);
    const secretBytes = bytes.slice(42, 42 + SECRET_BYTES);
    // Anything else this connection sends before the join is done closes it.
    state.joining = true;
    ws.serializeAttachment(state);
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", secretBytes));
    if (ws.readyState !== 1) return; // closed while hashing
    // From here on nothing awaits, so no other event of this object runs in between.
    state.joining = false;
    const member = memberId.toString();
    const secret = hex(secretBytes);
    const tag = digest.subarray(0, TAG_BYTES);
    const rooms = this.roomMap();
    let members = rooms.get(room);
    const previous = members?.get(member);
    if (previous && !sameText(previous.deserializeAttachment()?.secret ?? "", secret)) {
      ws.serializeAttachment(state);
      return this.closeWith(ws, CLOSE.taken);
    }
    if (!members) {
      if (rooms.size >= LIMITS.roomsPerRegion) return this.closeWith(ws, CLOSE.busy);
      members = new Map();
      rooms.set(room, members);
    } else if (!previous && members.size >= LIMITS.membersPerRoom) {
      return this.closeWith(ws, CLOSE.roomFull);
    }
    if (previous) {
      members.delete(member);
      this.forget(previous);
      try { previous.close(...CLOSE.replaced); } catch { /* already closed */ }
    }
    Object.assign(state, { room, member, secret, tag: hex(tag), lastAt: now });
    ws.serializeAttachment(state);
    members.set(member, ws);

    const others = [...members.entries()].filter(([id]) => id !== member);
    const region = new TextEncoder().encode(state.region);
    const entryBytes = 8 + TAG_BYTES;
    const welcome = new Uint8Array(4 + region.length + entryBytes * others.length);
    welcome[0] = FRAME_WELCOME;
    welcome[1] = PROTOCOL_VERSION;
    welcome[2] = region.length;
    welcome.set(region, 3);
    welcome[3 + region.length] = others.length;
    others.forEach(([id, other], i) => {
      const at = 4 + region.length + entryBytes * i;
      welcome.set(u64Bytes(BigInt(id)), at);
      welcome.set(unhex(other.deserializeAttachment()?.tag ?? ""), at + 8);
    });
    this.sendSafe(ws, welcome);
    const joined = new Uint8Array(1 + entryBytes);
    joined[0] = FRAME_MEMBER_JOINED;
    joined.set(u64Bytes(memberId), 1);
    joined.set(tag, 9);
    for (const [, other] of others) this.sendSafe(other, joined);
  }

  /** Removes a socket from its room and tells the others. Safe to call more than once. */
  leave(ws) {
    this.buckets.delete(ws);
    let state;
    try { state = ws.deserializeAttachment(); } catch { return; }
    if (!state?.room) return;
    const members = this.roomMap().get(state.room);
    if (members?.get(state.member) === ws) {
      members.delete(state.member);
      if (members.size === 0) {
        this.roomMap().delete(state.room);
      } else {
        const left = memberFrame(FRAME_MEMBER_LEFT, BigInt(state.member));
        for (const other of members.values()) this.sendSafe(other, left);
      }
    }
    this.forget(ws);
  }

  /** Marks a socket as in no room, so its later close event changes nothing. */
  forget(ws) {
    this.buckets.delete(ws);
    try {
      const state = ws.deserializeAttachment();
      if (state) ws.serializeAttachment({ ...state, room: null, member: null, secret: null, tag: null, joining: false, at: 0 });
    } catch { /* closed */ }
  }

  closeWith(ws, [code, reason]) {
    this.leave(ws);
    try { ws.close(code, reason); } catch { /* already closed */ }
  }

  sendSafe(ws, bytes) {
    try { ws.send(bytes); } catch { this.leave(ws); }
  }

  roomMap() {
    if (this.rooms) return this.rooms;
    this.rooms = new Map();
    for (const ws of this.ctx.getWebSockets()) {
      const state = ws.deserializeAttachment();
      if (!state?.room) continue;
      let members = this.rooms.get(state.room);
      if (!members) this.rooms.set(state.room, (members = new Map()));
      members.set(state.member, ws);
    }
    return this.rooms;
  }

  take(ws, now) {
    let bucket = this.buckets.get(ws);
    if (!bucket) {
      bucket = { tokens: LIMITS.messageBurst, at: now };
      this.buckets.set(ws, bucket);
    }
    bucket.tokens = Math.min(LIMITS.messageBurst, bucket.tokens + ((now - bucket.at) * LIMITS.messagesPerSecond) / 1000);
    bucket.at = now;
    if (bucket.tokens < 1) return false;
    bucket.tokens -= 1;
    return true;
  }

  /** Counts one message against its address's hourly budget; false once the budget is spent. */
  spend(address, now) {
    let use = this.addressUse.get(address);
    if (!use || now - use.since >= HOUR_MS) {
      use = { since: now, count: 0 };
      this.addressUse.set(address, use);
    }
    use.count += 1;
    return use.count <= LIMITS.messagesPerAddressPerHour;
  }

  overBudget(address, now) {
    const use = this.addressUse.get(address);
    return use !== undefined && now - use.since < HOUR_MS && use.count >= LIMITS.messagesPerAddressPerHour;
  }

  /**
   * Closes sockets that never joined and members silent for too long, and forgets spent budget
   * windows. Runs on traffic (messages and new connections) only.
   */
  sweep(now) {
    if (now - this.lastSweep < LIMITS.sweepEveryMs) return;
    this.lastSweep = now;
    for (const ws of this.ctx.getWebSockets()) {
      let state;
      try { state = ws.deserializeAttachment(); } catch { continue; }
      if (!state) continue;
      if (state.room === null) {
        if (state.at !== 0 && now - state.at > LIMITS.joinWithinMs) this.closeWith(ws, CLOSE.notJoined);
      } else if (now - state.lastAt > LIMITS.idleCloseMs) {
        this.closeWith(ws, CLOSE.idle);
      }
    }
    for (const [address, use] of this.addressUse) {
      if (now - use.since >= HOUR_MS) this.addressUse.delete(address);
    }
  }
}

/** Compares two strings in time that does not depend on where they differ. */
function sameText(a, b) {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function unhex(text) {
  const bytes = new Uint8Array(text.length / 2);
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(text.substr(2 * i, 2), 16);
  return bytes;
}

function memberFrame(kind, member) {
  const frame = new Uint8Array(9);
  frame[0] = kind;
  frame.set(u64Bytes(member), 1);
  return frame;
}

function readU64(bytes, offset) {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getBigUint64(offset, true);
}

function u64Bytes(value) {
  const bytes = new Uint8Array(8);
  new DataView(bytes.buffer).setBigUint64(0, value, true);
  return bytes;
}

function hex(bytes) {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}
