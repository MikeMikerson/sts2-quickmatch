# Quick Match Relay (QuickMatchRelay)

A companion to Quick Match that connects it to a relay you choose. When you play a Quick Match game
with someone on a phone, the game's messages normally travel as Steam lobby chat, about 0.3 seconds
each way, so every card a non-host player plays waits about 0.6 seconds for the host to confirm it.
With this mod and a relay, the same messages also go through the relay, a few hundredths of a second
each way, and the faster path is used. The relay also carries Quick Match's voice chat: on desktop,
voice needs this mod with a relay set. The log calls a player's link that the relay carries a "fast
link".

There is no built-in relay. Until you set one in `QuickMatchRelay.cfg` (below), the relay is off and
Quick Match uses Steam lobby chat only, exactly as without this mod. Every player in the match must
run Quick Match 0.3.0.

Matches between two desktop players do not use it: they play over the game's own Steam connection.

## Install

Put the `QuickMatchRelay` folder (`QuickMatchRelay.dll`, `.pdb`, `.json`) next to the `QuickMatch`
folder in the game's mods folder, then add a `QuickMatchRelay.cfg` (below). On macOS,
`../QuickMatch/install.sh` builds and installs both mods; for the Android StS2 Launcher,
`../QuickMatch.Android/install-android.sh` installs all three. Both scripts keep a
`QuickMatchRelay.cfg` that is already there. The iPhone launcher supplies its own relay connection and
does not use this mod.

Every player in the match needs a relay connection to the **same relay** for the relay to be used.
With one of them missing, or on a different relay, the match plays over lobby chat as before.

## Settings

The file `QuickMatchRelay.cfg` in the `QuickMatchRelay` folder (not `.json`: the game would read that
as a mod):

```
# The relay to use (see ../../relay/README.md, "Run your own").
relay = https://quickmatch-relay.<your name>.workers.dev

# Optional: turn the relay off without deleting the address.
enabled = false
```

The address must be a host name on HTTPS (port 443) without a path; `https://` or `wss://` both work.
IP addresses, other ports, paths and user names are refused. Any mistake in the file turns the relay
off and the game's log says why, so a typo never sends your match somewhere you did not choose.
The connection never goes through a proxy and does not follow redirects. If more than 512 messages
are waiting to be sent (a stalled network), the connection is closed rather than queueing stale
messages, and Quick Match carries on over lobby chat and connects again.

## How it decides

- Every player makes its own random keys for every match and announces them to the others inside
  the match's lobby chat, where Steam vouches for the sender. Every message sent through the relay
  is encrypted and authenticated with the sender's key (AES-GCM), so the relay can neither read nor
  change game messages or voice, and no player can pass messages off as another's. The relay keeps a
  player's place in the room for that player's own connection: another connection under the same
  Steam ID is refused (see Quick Match's README, "Relay").
- Every message still goes over lobby chat too, at first. Each player counts which copy arrives
  first. Only when the relay copy wins at least 6 of 8 times does the sender stop the lobby chat
  copies; if lobby chat wins, the relay copies stop instead.
- If the relay goes quiet for 5 seconds, closes the connection, or a player leaves it, both sides go
  back to lobby chat at once, and anything lost meanwhile is sent again over lobby chat. The match
  never ends because of the relay.

## What the relay sees

- Your IP address (as any server you connect to does) and when you connect.
- A room token derived from the host's room key, your Steam ID as you announce it to the others in
  the room, a random secret your game makes for the match (so only you can reconnect under your
  Steam ID), the size and timing of messages, and each message's sequence number (or, for voice, its
  frame number).
- Not the content of any message or voice packet, not your Steam sign-in (this mod never touches
  it), nothing else on your computer.

The relay in `../../relay` keeps no logs and no storage. If you use someone else's relay, you are
trusting them to run that code.

## Build

```sh
cd ../QuickMatch && dotnet build -c Release -p:ModsPath=   # first: this mod references QuickMatch's build
cd ../QuickMatch.Relay && dotnet build -c Release -p:ModsPath=
```

The output is in `bin/Release/net9.0/`. Like Quick Match, it compiles against the installed game
(`Sts2AppPath`, `Sts2DataPath`) and builds deterministically. On Windows pass
`-p:Sts2DataPath=<install>\data_sts2_windows_x86_64` to both builds.

Tests: `cd ../QuickMatch.Relay.Tests && dotnet test`. They check the settings parser and run the
WebSocket transport against the relay's own source with `wrangler dev` on your machine, which needs
Node.js and `npm install` in `../../relay`.

## Log lines (godot.log)

- `[QuickMatchRelay] fast link through <host> (<settings file>)`: loaded, using that relay.
- `[QuickMatchRelay] fast link off (<settings file>); Quick Match uses Steam lobby chat only`: no
  file, no `relay` line, `enabled = false`, or a mistake in the file (a `WARN` line before it names
  it).
- `[QuickMatch] chat host: relay: in the room (region ...)`, then `fast link up via relay for <steam
  id>` when the other player's game confirmed the relay is faster.
- `relay slower than lobby chat for <steam id>; staying on lobby chat`
- `relay connection closed: <reason>; lobby chat carries the match`
- At the end of a match: how many messages went over the relay and over lobby chat.

## Limits

- The keys travel as Steam lobby chat, which every member of the lobby receives and Steam's servers
  carry: they protect the match from the relay, not from Steam, and the players of one match can
  read each other's relay traffic. See Quick Match's README, "Known limitations".
- A relay on Cloudflare's free plan has a daily allowance. If it runs out, matches simply use lobby
  chat until it resets.
