# QuickMatch relay

A small Cloudflare Worker (with Durable Objects) that forwards QuickMatch game messages and voice
between the players of a match. It is optional: QuickMatch has no built-in relay, and you run your
own or use one you trust.

QuickMatch normally carries a phone player's game messages as Steam lobby chat, about 0.3 s each way.
In co-op the host confirms every card a player plays, so each card waits for a round trip. Through a
relay the same round trip takes tens of milliseconds. Matching still happens through Steam lobbies,
and if the relay is slow, full or unreachable, QuickMatch stays on lobby chat. Voice chat travels
only through the relay.

Desktop players matched with each other never use it: they play over the game's own Steam
connection. The relay is for matches with a phone: the iPhone launcher, or desktop and Android
players with the `QuickMatchRelay` companion mod (`../mod/QuickMatch.Relay`).

## What the relay can and cannot see

- It cannot read or change game messages or voice, nor pass anything off as coming from a player.
  Each player encrypts with AES-GCM under its own random keys for the match, which it announces to
  the other players over Steam lobby chat, which only the lobby's members receive.
- It never sees anyone's Steam sign-in. Only encrypted game messages and voice go through it.
- It sees the connecting IP addresses, each member's Steam ID (sent when joining a room), a random
  secret each member chose for the match, the size and timing of messages, a small readable header on
  each (a sequence or frame number), and which connections are in the same match.
- It keeps nothing. Rooms exist only in memory while their members are connected. For the
  per-address limits it uses a tag of each address (the first 8 bytes of an unsalted SHA-256 of the
  IPv4 address or IPv6 /64), which keeps the raw address out of its memory but is not anonymous:
  anyone holding a tag could find an IPv4 address by trying them all. There is no database, no file
  and no log of messages, and Cloudflare's request logging is turned off in `wrangler.toml`
  (`[observability] enabled = false`). Cloudflare itself still handles the connections as the host.

## Run your own

1. Create a free Cloudflare account and choose your `workers.dev` name when Cloudflare asks (Workers
   and Pages). No domain and no payment details are needed.
2. With Node.js installed, in this folder: `npm install`, then `npx wrangler login` (approve in the
   browser).
3. `npx wrangler deploy`. Your relay is at `https://quickmatch-relay.<your name>.workers.dev`.
4. Point the companion at it: in the `QuickMatchRelay` mod folder, create `QuickMatchRelay.cfg` with
   the line

   ```
   relay = https://quickmatch-relay.<your name>.workers.dev
   ```

   (see `../mod/QuickMatch.Relay/README.md`). Give the same address to everyone you want to play with:
   every player in a match must use the same relay for it to be used.

Opening the address in a browser shows a one-line description of the relay.

## Cost

None on Cloudflare's free plan, which allows 100,000 requests a day (20 incoming WebSocket messages
count as one) and 13,000 GB-seconds of Durable Object compute a day. Each region is one Durable
Object, so even awake all day it stays within the compute allowance; the request allowance is the
real limit. Past a free limit Cloudflare returns errors until the daily reset at 00:00 UTC rather than
charging, and QuickMatch then simply uses lobby chat. Check Cloudflare's current plan terms for your
account.

## Protocol

WebSocket at `wss://<relay address>/v1/relay/<region>`, binary frames, integers little endian:

| Frame | Direction | Layout |
|---|---|---|
| Join | client | `'J'`, version 2, room token (32 bytes), member ID (u64, the Steam ID), secret (16 bytes) |
| Welcome | relay | `'W'`, version, region length, region, member count, then member ID and tag (8 bytes) of each |
| Data | client | `'D'`, target member (u64, 0 for every other member), payload |
| Data | relay | `'D'`, sender member (u64), payload |
| Member joined | relay | `'+'`, member ID, tag (8 bytes) |
| Member left | relay | `'-'`, member ID |

`<region>` is `auto` (the relay picks Asia-Pacific, North America or Europe from where the connection
comes from), or `apac`, `na`, `eu`. The host connects with `auto` and tells the others which region it
got. A text frame `ping` is answered with `pong` without waking the relay. The room token is derived
from a room key the host sends over lobby chat, so only the match's players can find the room.

The first join of a member ID in a room holds it. A later join for that ID replaces the earlier
connection only if it brings the same secret (the same player reconnecting); otherwise it is refused
with close code 4009 and the holder stays. A member's tag is the first 8 bytes of the SHA-256 of its
secret. Players announce their own tags over lobby chat, so a connection under someone else's ID
shows up with the wrong tag and its messages are ignored. Version 1 joins (Quick Match 0.2.0) are
refused with close code 4008.

## Limits

4 members a room, 64 KB a frame, 100 messages a second per connection (bursts up to 200), 8
connections per address, 250,000 messages an hour per address (all its connections together, so one
address cannot use up a free plan's allowance alone), 150 rooms and 400 connections per region, 15
seconds to join after connecting, and members who send nothing for 2 minutes are closed. An IPv6
address counts by its /64. A connection over a limit is closed with a close code from 4000 to 4010
(see `src/index.js`); an address over its hourly budget cannot connect until the hour is over.

The checks for silent members and late joins run when the relay handles traffic (a message or a new
connection). Each connection's last message time is kept with the connection, so it survives the
Durable Object's hibernation; the hourly budgets and the rate counters are kept in memory and start
again after it.

## Development

- `npm install` once, then `npm test` (or `node --test test/`) runs the relay locally with
  `wrangler dev` and checks the protocol and limits. Nothing is deployed and no Cloudflare account is
  needed.
- `npm run dev` serves it at `ws://127.0.0.1:8787` for local testing.
