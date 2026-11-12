# Quick Match

Random co-op matchmaking for Slay the Spire 2. In the Multiplayer menu, pick **Quick Match**, choose
**2, 3 or 4 players**, and you are grouped with other players searching for the same size. The group
lands together in the normal co-op character select: everyone picks a character and presses Embark,
and the run starts once the lobby is full and everyone is ready. Emotes, map pings and map drawing
work as usual; phones get touch drawing, and matches that use a relay get voice chat (see below).

Matching uses Steam lobby search inside the game, so only players who have this mod can find each
other. The mod itself opens no network connections of its own: it talks to Steam, and to a relay
only through a companion that supplies one (see [Relay](#relay-optional)).

## Requirements

- Every player: the same game version (v0.113.0 for this build) and Quick Match 0.3.1 (0.3.0 was the
  build for v0.111.0). Lobbies carry
  a protocol number, so players on different releases (0.2.0 and 0.3.0, for example) never see each
  other's lobbies, and a host turns away a player of another release who joins anyway.
- Every player must load the **same gameplay-affecting mods**. Quick Match itself does not count (it
  sets `affects_gameplay: false`), so the simplest setup is Quick Match and nothing else. Lobbies are
  filtered by game version, the game's data hash and the gameplay mod list.
- Steam must be running (the card is greyed out without it, and when the game is launched with
  `-fastmp`). On Android the companion mod stands in for it (see `../QuickMatch.Android`).

## Install

The mod is two files, `QuickMatch.dll` and `QuickMatch.json`, in a folder named `QuickMatch` inside
the game's `mods` folder (create it if needed).

- **macOS**: `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/QuickMatch/`
  (the game looks for `mods` next to its executable, which is inside the app bundle; in Finder use
  right-click > Show Package Contents). Building this project copies the files there automatically.
- **Windows**: `<Steam library>\steamapps\common\Slay the Spire 2\mods\QuickMatch\`

Then start the game. The first time any mod is present the game asks whether to load mods: accept,
and restart if asked. On the Mods screen, disable any other gameplay mods. The game keeps a separate
save profile while mods are loaded, so unlocks start fresh there.

Steam updates or "Verify integrity of game files" may remove files inside the game folder. If the
Quick Match card disappears, run `./install.sh` in this folder: it builds Quick Match and the relay
companion (`../QuickMatch.Relay`) in Release and copies both back, keeping a
`QuickMatchRelay.cfg` that is already there. Pass another mods folder as an argument if your game is
elsewhere.

For Android, see `../QuickMatch.Android`: the same `QuickMatch.dll` runs there with a companion mod.

## Build

Needs the .NET SDK 10 (`brew install dotnet` on macOS). The mod targets .NET 9, the game's runtime,
and compiles against the installed game's assemblies.

```sh
cd mod/QuickMatch
dotnet build -c Release              # builds and copies dll, pdb and json into the game's mods folder
dotnet build -c Release -p:ModsPath= # builds only (bin/Release/net9.0/)
./install.sh                         # builds Release and copies it into the mods folder
```

`Directory.Build.props` holds the macOS Steam paths. Override `Sts2AppPath`, `Sts2DataPath` or
`ModsPath` on the command line for other setups; on Windows, point `Sts2DataPath` at
`<install>\data_sts2_windows_x86_64` and `ModsPath` at `<install>\mods`. An empty `ModsPath` builds
without copying. The companions and every test project read the game's assemblies the same way, so
pass the same `-p:Sts2DataPath=...` to their `dotnet build` and `dotnet test` too.

The build is deterministic with local paths mapped away (`Deterministic`, `ContinuousIntegrationBuild`,
`PathMap`), so the same source, game version and .NET SDK give the same `QuickMatch.dll`;
`global.json` at the repository root pins the SDK. The root README lists the expected SHA-256 and how
to check it.

### Tests

```sh
dotnet build -p:ModsPath=                 # the tests need a Debug build of the mod
cd ../QuickMatch.Tests && dotnet test
```

The tests cover matching rules, the lobby chat link, the relay route and its encryption, voice
(codec, gate, jitter buffer, encryption, mutes) and the touch gestures. They also check that every
Harmony patch target still exists in the installed game, which is the first thing to run after a
game update.

## How matching works

1. **Search**: Steam lobby search for Quick Match lobbies of your size with a free slot and matching
   version, data hash and mods. Best lobby first: most players, then oldest. Up to five are tried.
2. **Join**: the game's own join flow, without its error popups; a full or stale lobby is skipped.
3. **Host**: if two searches a couple of seconds apart find nothing, you open a lobby (the game's
   normal host lobby, made searchable) and wait in character select. A panel at the top shows
   "Waiting for players (k/N)".
4. **Merge**: while a lobby is not full, its host keeps looking for a better lobby of the same size
   with room for its whole group (fuller first, then older). If one exists, the group moves there
   within about 10 seconds: two lone hosts who searched at the same moment become one lobby, and for
   3 or 4 players a 2 + 2 or 2 + 1 split joins up. Everyone who moves keeps the character they had
   picked (they press Embark again).
5. **Start gate**: the run cannot start until the lobby has N players, even if everyone present is
   ready. Pressing Embark early shows the usual "Waiting for other players" with an Unready button.

Back cancels at any point. When the run starts the lobby is closed and removed from search.

## How game data travels

Each player's connection to the host is one of two kinds, chosen when they join:

- **Desktop to desktop: the game's own Steam sockets.** A desktop host runs the match on the game's
  own Steam host, the same one a run with friends uses, and marks its lobby (`qm_link=sockets`). A
  desktop player who finds that lobby joins through the game's own Steam connection.
- **Anyone on a phone: Steam lobby chat.** The phone launchers cannot run those sockets, so their game
  messages go as Steam lobby chat messages inside the match's lobby (`Transport/`). Lobby chat reaches
  every platform that can sign in to Steam.

A desktop host takes both kinds in the same match: desktop players on sockets, phones on lobby chat.
When a phone hosts, everyone uses lobby chat, desktop players included. Runs hosted with friends the
normal way are not affected.

What lobby chat means in play:

- A message and its answer take about half a second there and back. The game runs a joining player's
  card only once the host has confirmed it, so the cards of a player on lobby chat wait about that
  long; the host's own cards and a sockets player's cards do not. Ending a turn waits for every
  player, so it is slower by the same whenever anyone in the match is on lobby chat.
- Steam lets one player send about 17 lobby messages a second and silently drops the rest. The mod
  sends 10 a second on average and packs the game's messages into them; after a quiet moment it may
  send up to 3 at once, so a card played between other messages goes out straight away. Large
  messages are split and put back together.
- Each lobby message is numbered. A missing one is asked for again and the messages after it wait,
  so the game sees every message once and in order, as it would over a socket. A player not heard
  from for 20 seconds is dropped, as with the game's own connections.
- Messages the game marks as unreliable are dropped when they have waited a second to be sent.
- With the environment variable `QUICKMATCH_DEBUG=1` set for the game, each of the game's action
  messages (card plays, end turn, choices) gets a log line with a millisecond UTC time as it is sent
  and received (`[QuickMatch] DEBUG timing: ...`), so two players' logs show where a card play's wait
  goes. It is off by default.

### Relay (optional)

A lobby chat match can also travel over a relay, a small WebSocket server (`../../relay`), at a few
hundredths of a second each way instead of about 0.3 s. Quick Match itself opens no connection: a
platform supplies one through `Transport/IRelayTransport.cs`. On desktop and Android that is the
companion `QuickMatchRelay` (`../QuickMatch.Relay`), which has no built-in relay and stays off until
you give it a relay address. Without one, everything stays on lobby chat. The log calls a player's
link that the relay carries a "fast link".

- The host joins the relay when its first lobby chat player connects, then sends each player the
  relay region and a random 32-byte room key for the match, over lobby chat. Each player connects to
  the relay address in its own settings, so everyone in the match must use the same relay; a player
  on another relay finds an empty room and stays on lobby chat.
- The relay room is an HMAC-SHA256 of the room key, so only players who were sent it can find it.
- Every player makes its own random keys for the match, one for lobby messages and one for voice,
  and a random relay secret. It announces the keys over lobby chat with a tag of its secret (the
  start of its SHA-256). Steam delivers lobby chat with the real sender, so the others tie those keys
  to that player's Steam ID; the host passes each player's voice key on to the other players. A
  relay message is taken only from a player of the match, only from a relay connection that carries
  the tag that player announced, and only if it opens with that player's AES-GCM key
  (`Transport/RelayRoute.cs`, `Transport/RelayCrypto.cs`). So no player can pass messages or voice
  off as another's. The relay sees only each message's readable header (its session and sequence
  number), which is authenticated with the rest.
- In the relay room, the first connection under a Steam ID holds it: the relay refuses a later
  connection under that ID unless it brings the same secret, which only the same player reconnecting
  has. A player refused this way carries on over lobby chat.
- Each lobby message keeps its number, so a player who gets it both ways takes the first and drops
  the second. Each side counts which copy arrives first and tells the sender: lobby chat copies stop
  only when the relay is first at least 6 of 8 times, relay copies stop when lobby chat is first
  (`Transport/RelayRoute.cs`). While no lobby chat copies are sent, the 10 a second pace is lifted to
  50 a second.
- A relay connection that closes, a player leaving the relay, or 5 seconds without a relay message
  put both sides back on lobby chat at once; anything lost comes back from the resend buffer. The
  relay connection is tried again after 3 seconds, at most 4 times a match. The relay never ends a
  match.
- Log lines: `relay: in the room (region ...)`, `fast link up via relay for <id>`,
  `relay slower than lobby chat for <id>`, `relay connection closed: <reason>`, and per match
  `relay: N lobby messages sent and M received over the relay; ...`.

## Voice chat

Players in a match that uses a relay can talk. Voice travels only through the relay room, never over
lobby chat, so it needs a relay that every player in the match uses: on desktop that means the
`QuickMatchRelay` companion with a relay set in its settings file, and every player must run Quick
Match 0.3.0. Two desktop players matched with each other play over the game's Steam sockets, which
carry no voice.

- **When the mic is used**: voice chat is on by default, but your mic starts muted in every match.
  It records only while you have turned it on with the mic button, or while you hold the push-to-talk
  key (V by default, desktop only). In voice activated mode only speech is sent, nothing during
  silence (`Voice/VoiceGate.cs`).
- **Devices**: on desktop the mic is recorded through Steam's voice API, so Steam's chosen microphone
  and its noise handling apply (`Voice/SteamVoiceDevice.cs`). The iPhone launcher supplies its own
  device. The Android launcher does not allow the microphone, so Android players can listen only.
- **Format**: 16 kHz mono Opus at 18 kbit/s, 20 ms frames, three frames to a packet. The codec is
  Concentus, a pure C# port of the Opus reference library, compiled into the mod
  (`ThirdParty/Concentus`, under its own BSD-style license; its README records the source commit and
  file hashes).
- **Encryption**: each voice packet is sealed with AES-GCM under the speaker's own voice key for the
  match (`Voice/VoiceCrypto.cs`), and taken only from a player of the match on the relay connection
  that player announced (see Relay above). The relay sees a small readable header (stream and frame
  number) and the timing, so it can tell when someone is talking, but not what is said.
- **Controls**: a mic button in the lobby and during the run, a speaking ring on each player's
  portrait, and a Voice button in the pause menu with voice on or off, hear voice, talk mode, mic
  sensitivity, volume, per-player mute and volume, "Mute everyone else" and "Strangers start muted"
  (players who are not your Steam friends start muted). A player who stays far too loud is turned
  down automatically.
- **Privacy of choices**: settings and mutes are kept in `quickmatch_voice.json` next to the modded
  saves of the signed-in account and are never sent anywhere. A muted player's voice is dropped
  before it is decoded.
- Log lines: `voice: ready for this match, microphone <state>`, and at the end of a match
  `voice: N packets sent; ...`.

## Drawing on the map from a phone

On desktop you draw on the map by holding the right mouse button (the middle button erases). A
touchscreen has neither, so on a phone or tablet the mod adds touch gestures on top of the game's own
drawing. Lines are the game's, in your character's colour, synced, saved with the run and hidden by
the game's "Show multiplayer drawings" setting.

- **Hold to draw:** hold a finger still on the map, or on a map point, for about half a second. The
  phone ticks, and you draw until you lift your finger. Moving sooner is the normal map scroll, and a
  tap is still a vote or a ping.
- **Pen tool:** the game's quill, eraser and clear buttons at the bottom left, made larger on touch.
  With the quill or the eraser on, one finger draws and **two fingers move the map**. A tap leaves a
  dot. Tap the quill again to stop. Clear removes only your own lines.

Nothing changes on a computer without a touchscreen, and no new network messages are involved. The
code is `MapPen/`. The log gets one line per map visit, for example
`map pen: 3 strokes (1 by holding), 1 two-finger scrolls`.

## Rejoining a run

The game cannot put a player back into a run that is still going, but it can reload the host's save.
So when someone drops, the group regroups: the host reopens the saved run, and everyone else gets back
in. The run continues from the last autosave (taken when entering a room and after a fight), so a
fight in progress starts over.

When a Quick Match run starts, every player stores a small record of it (seed and players) on their
own device. Only the host has the save file.

- **A player drops**: the host gets a "Player Disconnected" popup. **Regroup** saves, quits and
  reopens the run at once. The other players are taken to the rejoin search automatically. The player
  who dropped restarts the game, opens Multiplayer and presses **Rejoin Run**. Once everyone is in,
  all press Embark. **Keep Playing** continues without them (the game may then wait for the missing
  player at the end of the turn).
- **The host drops**: the others are taken to the rejoin search ("Waiting for the host to reopen your
  run"). The host restarts the game, opens Multiplayer and presses **Load** as usual.
- **Rejoin Run** shows in the Multiplayer menu while you have a Quick Match run to return to and are
  not its host. It keeps looking for 3 minutes, then offers **Keep Waiting** or **Forget Run**.

The reopened lobby is listed only under that run's key, and the game's load lobby turns away anyone
who is not in the save, so strangers cannot get in. The record is cleared when the run ends (win,
loss or abandon), when the host abandons it from the menu, when you choose Forget Run, or after 7
days.

## Testing with a friend

Both: same game version, same Quick Match build, no other gameplay mods, mods accepted.

1. You pick 2 players and land in character select alone (1/2). Your friend picks 2 players and should
   join you within a few seconds. Both press Embark; the run starts.
2. Both pick 2 players at the same moment: you may briefly get two lobbies, then one of you moves
   into the other's lobby within about 10 seconds.
3. One picks 2 and the other 3: you never match.
4. Host presses Back: the friend gets the usual "host left" popup and returns to the size picker.
   Client presses Back: host shows 1/2 again and the friend can search again and rejoin.
5. Size 3 with two players: both Embark, the run does not start (log: `start gate`).
6. Host drops mid-fight (force-quit): the friend lands on "Waiting for the host to reopen your run".
   The host restarts and presses Load; the friend joins automatically and the run continues from the
   last room.
7. Friend drops: the host gets "Player Disconnected" and presses Regroup. The friend restarts,
   presses Rejoin Run and is back in the same run.

Logs: `~/Library/Application Support/SlayTheSpire2/logs/godot.log` (macOS) or
`%appdata%\SlayTheSpire2\logs\godot.log` (Windows). All lines start with `[QuickMatch]`:

- `start size=N ver=... db=... mods=...` (the compatibility key; must be identical for both players)
- Desktop host: `starting host for N over Steam sockets and lobby chat ...`; a phone host:
  `starting lobby chat host ...`
- `joining lobby <id> over Steam sockets` or `joining lobby <id> over lobby chat ...`
- `state X -> Y (reason)` for every step, and `lobby search ... -> k result(s)` with each candidate
- `published lobby ...`, `merge check: own lobby is visible in search`
- `join <id> failed: <reason>`, `start gate: ...`, `run started with ...`
- `merge: moving group of k ...`, `run record saved: key=...` (must match on every player)

## Known limitations

- Only players with this mod, on the same game version and gameplay mods, can match.
- In a match with a phone and no relay, the phone's cards (and every player's cards when the phone
  hosts) take about half a second to play. Matching does not yet prefer desktop-hosted lobbies, so a
  desktop player can land in a phone's lobby.
- The room key and every player's relay keys travel as Steam lobby chat, which every member of the
  lobby receives and Steam's servers carry. The encryption protects a match from the relay and from
  anyone outside the lobby, not from Steam, and not between the players of one match: each of them
  can read the others' relay traffic, as they see the game anyway.
- No player can pass messages or voice off as another's, but Quick Match does not change what the
  game itself trusts: a griefer can still play badly, leave, or use a modified game as in any co-op
  run. A player of the match can also take another player's Steam ID in the relay room before that
  player gets there, which keeps that player on lobby chat (and without voice) for the match.
- Rejoining reloads the last autosave, so a fight in progress starts over. There is no live rejoin
  into a running game (the game itself does not support it).
- Only the host has the save. If the host never comes back, the run cannot continue.
- Groups only merge when the better lobby has room for the whole group (for 3 players, 2 + 2 stays
  as two lobbies).
- Standard mode only; the host sets the ascension level in character select, as in vanilla.
- The host of a Quick Match lobby and players on lobby chat have no Invite button. A desktop player
  on Steam sockets has it, as in vanilla.
