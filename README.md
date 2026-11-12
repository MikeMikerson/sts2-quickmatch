# QuickMatch

Random co-op matchmaking for Slay the Spire 2. Pick how many players you want, and QuickMatch puts
you in a lobby with other people who are searching for the same size. You land together in the
game's normal co-op character select and play an ordinary co-op run.

Slay the Spire 2 is made by Mega Crit. QuickMatch is an unofficial fan mod, not made or endorsed by
Mega Crit or Valve.

## Install (desktop)

1. Get `QuickMatch.dll` and `QuickMatch.json`, from a release of this repository or by building them
   yourself (see [Building](#building)).
2. Put both files in a folder named `QuickMatch` inside the game's `mods` folder (create it if
   needed):
   - macOS: `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/QuickMatch/`
     (in Finder, right-click the app and choose Show Package Contents)
   - Windows: `<Steam library>\steamapps\common\Slay the Spire 2\mods\QuickMatch\`
3. Start the game. It asks whether to load mods the first time: accept, and restart if asked.

The game keeps a separate save profile while mods are loaded, so unlocks start fresh there. A Steam
update or "Verify integrity of game files" can remove the `mods` folder; put the files back if the
Quick Match button disappears.

## Play

Open **Multiplayer**, choose **Quick Match**, and pick **2, 3 or 4 players**. When the lobby is full
and everyone has pressed Embark, the run starts.

- Everyone needs QuickMatch 0.3.1 (0.3.0 on game v0.111.0), the same game version, and the same gameplay-changing mods
  (QuickMatch itself does not count). Players who differ never see each other: a 0.2.0 player and a
  0.3.0 player are never matched.
- Steam must be running.
- If someone drops, the host can regroup and everyone gets back into the run from its last autosave.
- Voice chat, and the faster connection for matches with a phone, need a relay that every player in
  the match uses. On desktop that means installing the companion `QuickMatchRelay` and setting a
  relay in its `QuickMatchRelay.cfg` (see [its README](mod/QuickMatch.Relay/README.md)); without it
  you play over Steam as usual, with no voice. Two desktop players matched with each other play over
  the game's own Steam connection, which carries no voice.

Phones can play too: the Android StS2 Launcher with the companion in `mod/QuickMatch.Android`, and
the iPhone launcher STS2 Mobile (the sts2-ios-launcher project), which builds a pinned QuickMatch
release in and checks its SHA-256.

## Is it safe?

In plain words, here is what goes where:

- **Matching** uses Steam's own lobby search. Your lobby lists the game version, a hash of the game's
  data, your gameplay mods, the lobby size and how full it is, so that only compatible players find
  it.
- **Game messages** between two desktop players go over the game's own Steam connection, exactly as
  in a co-op run with friends. A phone's game messages (and everyone's, when a phone hosts) go as
  Steam lobby chat messages, which only the lobby's members receive (Steam's servers carry them, as
  for any Steam lobby).
- **The relay is optional, and you choose it.** QuickMatch has no server of its own and no built-in
  relay. If you install the desktop companion `QuickMatchRelay` and point it at a relay (for example
  one you run yourself, see [relay/README.md](relay/README.md)), matches with phones also use that
  relay, because it is faster than lobby chat. Without it, nothing changes.
- **Relay traffic is encrypted between the players.** Each player makes its own random keys for each
  match and announces them to the others over lobby chat, where Steam vouches for who sent them. Game
  messages and voice sent through the relay are encrypted with the sender's key (AES-GCM), so the
  relay cannot read or change them, and no player can pass messages or voice off as another's. The
  relay does see your IP address, your Steam ID, and the size and timing of messages. The keys are
  as private as Steam lobby chat: every member of the lobby receives them and Steam's servers carry
  them, so they protect you from the relay, not from Steam. Players in your match can still play
  badly or leave, as in any co-op run (see "Known limitations" in
  [mod/QuickMatch/README.md](mod/QuickMatch/README.md)).
- **Your mic is off until you turn it on.** It starts muted in every match and records only
  while it is on (or while you hold the push-to-talk key). Voice travels only through the relay,
  encrypted as above. Whom you mute stays on your device.
- **Your Steam sign-in never touches the relay.** On desktop the mod uses the Steam client that is
  already running. The Android companion uses the launcher's saved sign-in to talk to Steam only.
- **No stats, analytics or telemetry.** The mod sends nothing anywhere except to Steam and, if you
  set one up, the relay.

The code is all here, and the build is reproducible, so you can check that a released
`QuickMatch.dll` was built from this source (see below).

## Technical details

### Layout

| Folder | What it is |
|---|---|
| `mod/QuickMatch` | The mod: matchmaking, lobby chat transport, rejoining, map drawing on touchscreens, voice chat (`Voice/`, with the Opus codec port Concentus in `ThirdParty/Concentus`). [README](mod/QuickMatch/README.md) |
| `mod/QuickMatch.Relay` | `QuickMatchRelay`, the desktop (and Android) companion that connects to a relay you choose. [README](mod/QuickMatch.Relay/README.md) |
| `mod/QuickMatch.Android` | `QuickMatchAndroid`, the companion for the Android StS2 Launcher. [README](mod/QuickMatch.Android/README.md) |
| `relay` | The relay server: a Cloudflare Worker with Durable Objects. [README](relay/README.md) |
| `mod/*.Tests` | Tests for each mod. |

### Building

Needs the .NET SDK 10 (`brew install dotnet` on macOS; the mods target .NET 9, the game's runtime, and
the tests .NET 10) and Slay the Spire 2 installed, since the mod compiles against the game's own
assemblies. `global.json` at the root pins the SDK feature band used for the published hashes
(10.0.4xx, at least 10.0.402); `dotnet --version` in this folder shows which one you get.

```sh
cd mod/QuickMatch
dotnet build -c Release              # builds and copies dll, pdb and json into the game's mods folder
dotnet build -c Release -p:ModsPath= # builds only, into bin/Release/net9.0/
./install.sh                         # builds QuickMatch and QuickMatchRelay and copies both into the mods folder
```

The paths in `mod/QuickMatch/Directory.Build.props` are for a macOS Steam install. Elsewhere, pass
your own on the command line: `-p:Sts2DataPath=<game folder>\data_sts2_windows_x86_64` and
`-p:ModsPath=<game folder>\mods` on Windows, or `-p:Sts2AppPath=/path/to/SlayTheSpire2.app` for
another macOS location. The companions (`mod/QuickMatch.Relay`, `mod/QuickMatch.Android`) and the test
projects (`mod/*.Tests`) read the game's assemblies the same way, so on Windows give each `dotnet
build` and `dotnet test` the same `-p:Sts2DataPath=...`. `./install.sh /path/to/mods` installs into
another mods folder.

### Checking the build

`QuickMatch.csproj` builds deterministically, with no local paths or git commit in it, so the same
source and the same game version give a byte-identical `QuickMatch.dll` in any clone.

```sh
cd mod/QuickMatch
dotnet build -c Release -p:ModsPath=
shasum -a 256 bin/Release/net9.0/QuickMatch.dll
```

For QuickMatch 0.3.1 against Slay the Spire 2 v0.113.0 (macOS assemblies, .NET SDK 10.0.402, as
`global.json` asks for) the result is:

```
4c556b610a2be84eda0a81cc0800ee8264ba41ea6f9561514f3c13a51a8a4cfc
```

and `QuickMatch.json` is
`cf15b3eb81d3845906f6094c23308da8448b1be6ba7098c5815ffde9d1948fef`. These are the files the iPhone
launcher pins. If your hash differs, check the game version first, then `dotnet --version`: a
different SDK (and so compiler) can change the output.

### Tests

The C# tests compile against the installed game and need a Debug build of the mod first.

```sh
(cd mod/QuickMatch && dotnet build -p:ModsPath=)
(cd mod/QuickMatch.Tests && dotnet test)
(cd mod/QuickMatch.Relay.Tests && dotnet test)        # its live tests run the relay locally: npm install in relay/ first
(cd mod/QuickMatch.Android && dotnet build -p:ModsPath=)
(cd mod/QuickMatch.Android.Tests && dotnet test)
(cd relay && npm install && node --test test/)
```

The tests include a check that every Harmony patch target still exists in the installed game: run
it first after a game update.

### Running your own relay

The relay is optional. To run one on a free Cloudflare account and point the companion at it, follow
"Run your own" in [relay/README.md](relay/README.md). Everyone in a match must use the same relay
address for it to be used; anyone else simply stays on lobby chat.

## License

MIT, see [LICENSE](LICENSE). The bundled Concentus code in `mod/QuickMatch/ThirdParty/Concentus`
keeps its own BSD-style license (its `LICENSE` file there).
