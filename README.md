# Goggles

[![CI](https://github.com/angelospanag/Goggles/actions/workflows/ci.yml/badge.svg)](https://github.com/angelospanag/Goggles/actions/workflows/ci.yml)

A fast, native, cross-platform client for [GOG.com](https://www.gog.com) — written in C# with
Avalonia UI, and built from scratch down to the download and manifest engine.

> **Status: early.** Sign-in, library browsing, downloading and launching native builds all work
> against the real GOG API, from the desktop app as well as the CLI. On macOS and Linux, Windows-only
> titles run through CrossOver or Wine. Not ready for daily use.

## Why

[Heroic Games Launcher](https://heroicgameslauncher.com) already does "GOG launcher with a GUI"
well, on an Electron front end over the Python `gogdl` backend. Goggles isn't trying to beat it at
that. The point is to own the whole stack — including the parts most clients delegate — as a
genuinely custom C# implementation: one native binary, no Electron, no Python.

## Requirements

- macOS, Linux, or Windows
- .NET 11 SDK — pinned in [`global.json`](global.json) to 11.0.100 or any later 11.0.x

## Getting started

### 1. Install the .NET SDK

The simplest way is [dotnetup](https://learn.microsoft.com/dotnet/core/tools/dotnetup/getting-started),
Microsoft's per-user .NET toolchain manager (currently in public preview). Download it:

```bash
# macOS / Linux
curl -fsSL https://aka.ms/dotnetup/get-dotnetup.sh | bash
```

```powershell
# Windows (PowerShell)
irm https://aka.ms/dotnetup/get-dotnetup.ps1 | iex
```

Follow the script's instructions to add `dotnetup` to your `PATH`, open a new terminal, then run
the one-time setup — it wires `dotnet` into your shell and installs the latest stable SDK:

```bash
dotnetup init
dotnetup install 11      # only needed if init picked something other than .NET 11
```

Open a new terminal once more and check that `dotnet --version` prints 11.0.something.

Prefer a regular installer? Any .NET 11 SDK from [dot.net](https://dot.net) works just as well.

### 2. Build and run

Clone the repository and run everything from its root:

```bash
git clone https://github.com/angelospanag/Goggles.git
cd Goggles

dotnet run --project Goggles.Desktop # launch the desktop app
dotnet build -c Release              # build the whole solution
dotnet test                          # run the tests (headless, no display server needed)
dotnet format                        # format the code
```

### 3. The command-line harness (optional)

You can also sign in and pull your library from the terminal:

```bash
dotnet run --project Goggles.Cli -- login
dotnet run --project Goggles.Cli -- library
```

`login` opens GOG's sign-in page in your browser. When it finishes you land on a blank-looking
`embed.gog.com` page — copy the whole address from the address bar and paste it back. Goggles reads
the authorization code out of it and exchanges it for tokens.

The desktop app does better on macOS: sign-in happens inside the window, in the system's own web
view, and finishes on its own. Elsewhere — and whenever GOG serves a CAPTCHA the embedded view can't
handle — it falls back to the same paste-the-address flow.

Other commands:

```
whoami                  show the signed-in account
library --hidden        include products you have hidden
logout                  forget the stored tokens
builds <productId>      list the builds GOG publishes for a game
manifest <productId>    fetch a build's manifest, decompress it and outline its structure
securelink <productId>  fetch the signed CDN endpoints for a game's depot store
chunk <id> <md5>        download a single depot chunk and verify its checksums
resolve <id>            list the files a build would install
download <id>           download a build (resumable; --parallel N, --verify, --lang, --out)
play <id>               launch an installed game (--dry-run to just resolve it)
uninstall <id>          delete an installed game (dry run unless --yes)
galaxy-service          stand in for Galaxy's local service: sign games in, log what they send (--capture <dir>)
--dump <dir>            write raw API responses to <dir>
```

**On credentials:** tokens are written as plaintext JSON to `tokens.json` in the app's data folder
— `~/Library/Application Support/Goggles` on macOS, `~/.config/Goggles` on Linux — with owner-only
permissions. Secure OS-backed storage is planned. The same folder holds `library.json`, the last
library sync, which Goggles shows at launch and deletes when you sign out. Response dumps contain
your email, account id, and owned library — keep them out of version control.

## Playing Windows games on macOS and Linux

Many GOG games only ship for Windows. Goggles can run them through a compatibility layer you already
have installed — much like Heroic's per-game "Wine version":

- **[CrossOver](https://www.codeweavers.com/crossover)** — every bottle you have is offered
  separately, so different games can live in different bottles.
- **Wine** — any `wine` on your `PATH`, from Homebrew, or from the Wine Stable / Devel / Staging
  app bundles. Goggles keeps its own prefix for it, in its app data folder.

Install the Windows build as usual, then pick a runner under **Run with** on the game's page and
press Play. Each game remembers its own choice, and new games start with the last one you picked.
Bottles you add in CrossOver show up the next time you open a game.

Goggles drives these tools; it doesn't install them, create bottles, or set up a game's
redistributables (DirectX, Visual C++ runtimes). If a game starts and then complains about a missing
DLL, install the runtime into its bottle from CrossOver.

## Achievements

Goggles shows each game's GOG achievements and your play time, but **playing a game through
Goggles does not unlock anything yet.** The numbers you see are whatever GOG has already recorded,
from Galaxy or another client.

That's because games don't report achievements themselves. Each one ships with GOG's Galaxy SDK,
which hands unlocks to a background service that the GOG Galaxy app runs on your machine
(`127.0.0.1:9977`). That service signs in with the game's own credentials and forwards the unlock to
GOG. Started by anything other than Galaxy, a game finds nobody listening, and its unlocks go
nowhere.

Goggles is building its own version of that service, inside the app:

| | How unlocks reach GOG |
|---|---|
| GOG Galaxy | Galaxy's own background service |
| Heroic | [Comet](https://github.com/imLinguin/comet), a separate Rust program Heroic downloads and starts alongside the game |
| Goggles | Built into `Goggles.Core`: no extra program, and it uses the sign-in Goggles already has |

It's being built in phases. **Phases 1 and 2 are done:** the service listens, logs what a game
sends, and signs the game in. Absolute Drift on macOS accepts the sign-in and then asks for its
achievements and stats, which aren't answered yet. Next it records achievements and stats
(including unlocks made offline), then starts automatically when you press Play, and finally works
for Windows games in CrossOver.

To watch what a game asks for, run `galaxy-service` from the command-line harness, then launch the
game. Quit GOG Galaxy (or Comet) first, since only one program can use the port. The message
formats come from the community's reverse-engineered
[gog_protocols](https://github.com/Yepoleb/gog_protocols) (public domain), checked against real
game traffic.

## Layout

| Project | What it is |
|---|---|
| `Goggles.Desktop` | The Avalonia desktop app — sign-in, library, game pages, installing and playing |
| `Goggles.Core` | Everything else: API clients, auth, library sync, downloads, launching, the Galaxy service. No UI dependency. |
| `Goggles.Cli` | Development harness for driving Core against live GOG |
| `Goggles.Core.Tests` | Unit tests; run headless |

`Goggles.Core` never references Avalonia, so the tests need no display server and the eventual
download engine can't accidentally touch the UI thread.

## Roadmap

- [x] OAuth2 login, token storage and refresh
- [x] Library sync (owned games)
- [x] Library cache — the last sync is shown instantly at launch, then checked against GOG behind it
- [x] Manifest resolution — build/branch/language/bitness selection against GOG's undocumented
      generation 2 manifests
- [x] Download engine — multi-threaded, resumable, checksum-verified
- [ ] Install execution — running bundled dependencies and redistributables
- [x] Launch — native builds, via the play tasks GOG ships with each install
- [ ] Cloud save sync
- [ ] Achievement and stats sync — Goggles' own stand-in for Galaxy's local service *(in progress)*
- [ ] Delta patching *(deferred — v1 does full downloads only)*
- [x] Windows games on macOS and Linux through CrossOver (any bottle) or Wine, chosen per game
- [ ] Creating CrossOver bottles and managing Wine builds from Goggles
- [ ] Proton (via umu) on Linux

## Notes

Goggles uses GOG's unofficial API, documented by the community at
[gogapidocs](https://gogapidocs.readthedocs.io/en/latest/). It is not affiliated with, endorsed by,
or supported by GOG.com or CD PROJEKT.

## License

[MIT](LICENSE)
