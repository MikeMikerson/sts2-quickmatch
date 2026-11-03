# Quick Match for Android (QuickMatchAndroid)

A companion to [Quick Match](../QuickMatch/README.md) for the Android StS2 Launcher, which runs Slay
the Spire 2 on Android phones (tested with launcher 0.3.17). Quick Match itself runs unchanged on
Android; this mod supplies the three things it normally gets from Steamworks, which the launcher does
not run:

- **Lobbies**: an `ILobbyBackend` (`Lobby/AndroidLobbyBackend.cs`) on SteamKit2, which the launcher
  already ships (3.4.0). Search, create, join, lobby data and lobby chat go to Steam over the mod's
  own connection, signed in with the launcher's saved Steam sign-in.
- **The player's Steam ID**: the launcher runs the game as platform None with player ID 1, and its
  stub Steam library returns the same ID for everyone. Quick Match runs are Steam runs, so
  `Patches/IdentityPatches.cs` answers `SteamPlatformUtilStrategy.GetLocalPlayerId` with the signed-in
  account's ID (progress save, run history, abandoning a run).
- **Steam names**: `SteamPlatformUtilStrategy.GetPlayerName` answers from the lobby's member list and
  the account's own persona name.

A further patch, on `RunManager.CanonicalizeSave`, keeps the game from renaming a Quick Match save as
broken: when a Steam save does not contain the ID the game asked for, it uses the signed-in ID.
Nothing touches platform None, so normal saves (`default/1`) and the launcher's LAN play are
unchanged.

On desktop (or anywhere the launcher's Steam classes are absent) the mod logs one line and does
nothing.

## Install

Needs the phone signed in to Steam in the launcher, USB debugging on, `adb` (Android platform-tools),
and the .NET SDK and the desktop game on your computer to build the mods (see the root README).

```sh
./install-android.sh              # the only connected phone
./install-android.sh <serial>     # one phone of several (see "adb devices")
```

The script builds Quick Match, this mod and the relay companion `QuickMatchRelay` in Release,
copies `dll`, `pdb` and `json` of each into `/storage/emulated/0/StS2Launcher/Mods/<id>/` with
`adb push`, and checks each file's SHA-256 on the phone. It only adds or overwrites files there and
never deletes anything (a `QuickMatchRelay.cfg` already on the phone is kept). Then start the game
from the launcher, accept the game's mod warning and restart the game. (In launcher 0.3.17 the restart
after the warning can fail; then close the game in the app switcher and start it again.)

`QuickMatchRelay` stays off until it has a relay address: to use a relay (faster card plays, and
hearing voice chat), push a
`QuickMatchRelay.cfg` (see [its README](../QuickMatch.Relay/README.md)) into
`/storage/emulated/0/StS2Launcher/Mods/QuickMatchRelay/`.

The folder names also give the load order: the launcher loads mods in the sorted order of their
folders, and `QuickMatch` sorts before `QuickMatchAndroid` and `QuickMatchRelay`, so the dependency
is met.

## How it reaches the launcher

`Launcher/LauncherSession.cs` is the only file that knows the launcher's internals, and it reaches
them by reflection, by assembly-qualified name:

- `STS2Mobile.Services.AndroidSteamCredentialStore` (`Load`, `HasCredentials`, `AccountName`,
  `RefreshToken`): the launcher's saved sign-in, kept in the Android Keystore.
- `STS2Mobile.Steam.Connection.SteamConnection` (constructor, `Client`, `State`, `Apps`,
  `SuspendIdleTimeout`, `ResumeIdleTimeout`) with `STS2Mobile.Services.AndroidLogger`.

The mod makes its own `SteamConnection`, separate from the launcher's cloud-save connection, so the
launcher's idle disconnect cannot drop a match; the idle timer is held while in a lobby. The refresh
token goes from the credential store straight into that constructor, is used only to connect to
Steam, and is never stored, logged or sent anywhere else by the mod. If any of these members is
missing (another launcher version), the mod logs what it could not find and the Quick Match card
stays unavailable.

## Build and test

```sh
(cd ../QuickMatch && dotnet build -c Release -p:ModsPath=)   # first: this mod references QuickMatch's build
dotnet build -c Release -p:ModsPath=
```

On Windows, add `-p:Sts2DataPath=<install>\data_sts2_windows_x86_64` to each build and to
`dotnet test`, as for Quick Match (the script above is for macOS and Linux shells).

For the tests, build Debug versions of both first:

```sh
(cd ../QuickMatch && dotnet build -p:ModsPath=)
dotnet build -p:ModsPath=
cd ../QuickMatch.Android.Tests && dotnet test
```

The tests drive the bridge against stand-ins with the launcher's shape, parse lobby messages with
SteamKit2's own message classes, and check that every Harmony target names exactly one method in the
installed game. They compile against the desktop game installed on the computer, which must be the
same build as the launcher's (v0.111.0).

## Log lines

In the launcher's `sts2-internal/logs/godot_*.log`:

- `[QuickMatchAndroid] QuickMatchAndroid initialized; lobbies go through the launcher's Steam sign-in`
- `signed in to Steam for lobbies as <steam id>`, or `Steam sign-in for lobbies failed, Quick Match
  waits: <reason>` (retried at most every 30 seconds while the card is shown)
- `created lobby ...`, `entered lobby ...`, `left lobby ...`
- `not running in the Android launcher; nothing to do` (desktop)
- `loading a Steam-platform run save as <id> instead of <given>` (the save fix above)

## Limits

- Only Steam's default branch: the launcher downloads the public branch, and Quick Match matches only
  players on the same game version and data hash.
- Voice chat is listen-only: the launcher does not allow the microphone.
- Without a relay, every message goes over Steam lobby chat, which adds about half a second per card
  played (see Quick Match's README).
- Tested against launcher 0.3.17 only.
