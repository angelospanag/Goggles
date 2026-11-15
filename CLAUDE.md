# Goggles

A cross-platform C# client for GOG.com. A from-scratch reimplementation — including the
download/manifest engine — not a wrapper around `gogdl` or similar.

## Commands

```bash
dotnet build -c Release                         # whole solution (Release); SDK pinned in global.json
dotnet test                                     # Core tests; needs no display server
dotnet format                                   # format the code
dotnet run --project Goggles.Desktop            # desktop app
dotnet publish Goggles.Desktop -c Release -r osx-arm64 -p:SelfContained=true -o publish/osx-arm64   # + Goggles.app
dotnet run --project Goggles.Cli -- help        # dev harness
dotnet run --project Goggles.Cli -- login       # OAuth: paste back the redirect URL
dotnet run --project Goggles.Cli -- library --dump ./fixtures
dotnet run --project Goggles.Cli -- builds <productId> [--os windows|osx|linux]
dotnet run --project Goggles.Cli -- manifest <productId> --depots   # fetch + outline v2 manifests
dotnet run --project Goggles.Cli -- securelink <productId>
dotnet run --project Goggles.Cli -- chunk <productId> <compressedMd5>   # download + verify one chunk
dotnet run --project Goggles.Cli -- details <productId> [--extra <n> --out <dir>]   # gameDetails; fetch one goodie
dotnet run --project Goggles.Cli -- galaxy-service --capture ./galaxy   # sign a game in and log what it asks Galaxy for
```

**CI** (`.github/workflows/ci.yml`) runs on every PR and push to `main`: build and test on Linux, macOS
and Windows with `-warnaserror` (so any new warning fails, and so does a package with a known
vulnerability, via the NuGet audit settings in `Directory.Build.props`); `dotnet format
--verify-no-changes`; and `dotnet list package --vulnerable --include-transitive`, which also runs
weekly because advisories appear without code changes. A package job publishes self-contained builds for
osx-arm64 (as `Goggles.app`), win-x64 and linux-x64, checks each, and keeps the archives as run
artifacts for a week. Dependabot opens PRs for NuGet packages and for
the workflow's actions, which are pinned to commit hashes. To reproduce locally:
`dotnet build -c Release -warnaserror && dotnet test -c Release --no-build && dotnet format --verify-no-changes`.

**`Goggles.Cli` is a development harness, not a product.** It exists for the things the desktop app
cannot do — capturing raw responses with `--dump`, decoding undocumented blobs with
`manifest --depots`, isolating failures (`play --dry-run` proved launch resolution was correct while
the launch itself was not), and running headless. It is also the second consumer of Core that keeps
the no-Avalonia rule honest and forced `GogSession` to exist. It carries no obligation to match the
app feature for feature, and needs no polish budget; the duplicated commands are conveniences.

Games go to `--library` (default `~/Games/Goggles`, the same root the desktop app uses) so
`download`, `play` and `uninstall` agree. `--out` is only for manifest and API dumps.

`--dump <dir>` writes every response to disk. Access tokens, refresh tokens, session ids, the client
secret and the authorization code are redacted from both bodies and URLs by `ResponseDumpHandler`,
but **dumps still contain the user's email, account id and owned library — never commit them.**

## Layout

```
Goggles.Desktop/  Avalonia UI — sign-in, library grid/list, game detail pages
  Services/         CoverImageCache (disk-cached _196.jpg tiles)
  Packaging/macOS/  Info.plist and goggles.icns for the Goggles.app a macOS publish produces
Goggles.Core/     all logic; no UI dependency
  Api/Auth/         OAuth2, token storage, the auth DelegatingHandler
  Api/Embed/        embed.gog.com — owned library, user data
  Api/ContentSystem/ builds, manifests, secure links, CDN
  Library/          domain models + LibrarySyncService, LibraryCache (library.json)
  Manifests/        ManifestResolver → ResolvedBuild (files, chunks, offsets)
  Downloads/        DownloadEngine — parallel, resumable, verified
  Launching/        GameLauncher — reads goggame-{id}.info play tasks; WindowsRunner (CrossOver/
                    Wine), WindowsRunnerDiscovery, RunnerPreferences (runners.json)
  Installs/         GameUninstaller — guarded directory removal
  Diagnostics/      ResponseDumpHandler — credential-redacting response dumps
  Galaxy/           CommunicationService — stands in for Galaxy's local service (achievements)
Goggles.Cli/      dev harness for exercising Core against live GOG
Goggles.Core.Tests/
```

**The one hard rule: `Goggles.Core` never references Avalonia.** That is what keeps the tests
headless and the download engine off the UI thread. Everything else is negotiable.

Core is "everything that isn't UI", not "domain only" — the API client belongs here. There is no
`Infrastructure` project and no Clean-architecture layering, because GOG's API *is* the domain;
there is no business logic that would survive swapping it out. Revisit only if a second consumer
appears (a daemon, a published NuGet package) or platform-specific token storage starts dragging
native dependencies around.

## Conventions

- **Build settings are shared.** `Directory.Build.props` sets the target framework, nullable and
  implicit usings for every project; `Directory.Packages.props` holds every NuGet version. A new
  package gets a `<PackageVersion>` there and a version-less `<PackageReference>` in its project.
  The desktop project is `Goggles.Desktop` but builds an assembly named `Goggles`, so the executable
  is `Goggles`. What macOS shows in Activity Monitor, the Dock and the menu bar is a separate thing:
  Avalonia's `Application.Name`, set in `App.axaml`. Left unset it is "Avalonia Application".
- **Wire DTOs stay inside `Api/<host>/Models/` and never leave the service layer.** `embed.gog.com`
  and `api.gog.com` speak different JSON dialects; they are reconciled in `Library/`, not merged
  into shared DTOs.
- **ViewModels depend on services, never on `HttpClient`.**
- **No interface until there are two implementations** (or a real need to fake one). `ITokenStore`
  earns its place; a `ILibraryCache` with one in-memory implementation would not.
- Tests use `StubHttpMessageHandler` and `FakeTimeProvider` from `Goggles.Core.Tests/TestDoubles/`.
  No mocking framework.
- Comments explain *why*, particularly where the code works around a GOG quirk. Skip the ones that
  restate the code.

## GOG API notes

Reference: https://gogapidocs.readthedocs.io/en/latest/ — community-maintained, incomplete, and
occasionally wrong about details. Verify against a real response before trusting it.

Hosts in `GogHosts`: `auth` (OAuth2), `embed` (owned library — legacy), `api` (product metadata,
`downlink`), `content-system` (build lists), `cdn` (manifests, depot chunks).

The client id/secret in `GogAuthOptions` are Galaxy's own, published in the docs above. GOG issues
no third-party client registrations, so every community client uses them.

**`redirect_uri` is validated, so the redirect cannot be moved.** Tested 2026-08-12: `auth.gog.com`
happily forwards any `redirect_uri` to `login.gog.com`, which makes it look permissive, but after a
real sign-in GOG answers

```json
{"error":"redirect_uri_mismatch","error_description":"The redirect URI provided does not match registered URI(s)."}
```

for `http://localhost:<port>/callback` and for custom schemes alike. Only Galaxy's registered
`https://embed.gog.com/on_login_success?origin=client` works. That rules out the RFC 8252 loopback
listener and any `goggles://` handler; a `LoopbackAuthListener` was written, tested and deleted.

The consequence: capturing the code automatically requires *observing the browser*, which means an
embedded WebView (what Heroic does, free with Electron) or asking the user to paste the redirect URL
(what Goggles does). Logins are rare because refresh tokens persist, so pasting is the cheaper trade
until a WebView earns its dependency.

**The docs only cover generation 1 manifests.** Generation 2 — what Galaxy actually uses for modern
builds — is undocumented. The shape below was captured from live responses by the `manifest` spike
command; `ManifestResolver` will still need to handle generation 1 behind the same abstraction.

### Generation 2 manifests (captured, not documented)

Metadata blobs are zlib-compressed JSON, sharded by hash: `/content-system/v2/meta/{h0..2}/{h2..4}/{hash}`.
Build links point at `gog-cdn-fastly.gog.com`; follow `build.link` rather than rebuilding the URL.

A build blob (`version: 2`) holds `baseProductId`, `platform`, `buildId`, `installDirectory`,
`clientId`/`clientSecret` (per-game Galaxy credentials, used for achievements and cloud saves),
`products[]`, `tags[]`, `scriptInterpreter`, an `offlineDepot`, and `depots[]`. Each depot entry has
`manifest` (the hash of a second blob), `size`, `compressedSize`, `productId`, `languages[]`
(`"*"` or e.g. `"en-US"`) and sometimes `osBitness[]` — so language and bitness selection happen at
depot granularity, before any file is fetched.

A depot blob is `{depot: {items[], smallFilesContainer}, version: 2}`. Each item is
`{path, type: "DepotFile", sha256, flags[] (e.g. "executable"), chunks[]}`, and each chunk is
`{md5, size, compressedMd5, compressedSize}` — 10 MiB uncompressed per chunk. That gives the
download engine everything: per-chunk addressing, per-chunk integrity, and a whole-file sha256.

**A build can span several products.** Baldur's Gate II: Enhanced Edition (`1207666373`) ships four
depots of its own and three belonging to `2119788107`, "Reflections Of Myth & Valor" — a DLC listed
in the same manifest's `products[]`. GOG signs download URLs per product and answers **403 Forbidden**
on `secure_link` for anything the account has not bought, so `ResolveOptions.OwnedProductIds` filters
those depots out before the download engine can ask. The base product is always kept, since resolving
a game implies owning it.

**Builds span branches, newest first.** `branch: null` is the default/public branch; named branches
(`"beta"`) are interleaved, so "the first item" is frequently a beta build. Always filter by branch.

### Downloading chunks (captured, not documented)

`GET content-system.gog.com/products/{id}/secure_link?generation=2&path=/&_version=2` (authenticated)
returns `{product_id, type: "depot", urls[]}`. Each entry is a CDN endpoint with a `url_format`
template, a `parameters` map, a `priority`, `supports_generation[]` and `fallback_only`.

Two templates were observed, and they place the path differently:

```
fastly  {base_url}/token=nva={expires_at}~dirs={dirs}~token={token}{path}
gcore   {base_url}/{path}?wsSecret={token}&wsTime={time}&prefix={prefix}
```

**The chunk hash extends the `path` parameter, not the end of the URL** — appending to the finished
string breaks gcore, whose query parameters come after `{path}`. `SecureLinkEndpoint.BuildChunkUri`
substitutes `{path}` with `path + "/" + GalaxyPath.Shard(compressedMd5)`.

Chunks are addressed by **`compressedMd5`**, zlib-compressed, and served raw. Verified end to end:
downloaded bytes match `compressedSize` and `compressedMd5`; inflated bytes match `size` and `md5`.
Tokens are short-lived — fetch a secure link per download session, not once at startup.

Still unknown: what `smallFilesContainer` means for extraction, and whether `priority` sorts
ascending or descending (highest-first is assumed, and fastly at priority 10 does serve).

### Quirks confirmed against live responses

- `userData.checksum` mixes cases: `games` and `wishlist` are camelCase, but `games_rating` and
  `reviews_votes` are snake_case. Hence the explicit `[JsonPropertyName]` attributes.
- `worksOn` keys are PascalCase (`Windows`/`Mac`/`Linux`); the deserializer is case-insensitive.
- Product images are protocol-relative, extension-less, and served from `gog-statics.com`:
  `//images-3.gog-statics.com/<hash>`. A size suffix must be appended before use.
- Dates use PHP's `DateTime` shape (`{date, timezone_type, timezone}`) — see `PhpDateTime`.
- A signed-out embed session returns an **HTML login page with HTTP 200**, so unparseable bodies are
  raised as `GogApiException`. `GogAuthHandler` treats that page like a 401 — refresh, retry once —
  and if it survives the refresh, raises `AuthService.SignInRequired`.
- **Session expiry has one path.** `AuthService.SignInRequired` fires when the token endpoint refuses
  the credentials (400/401/403 only — 429 and 5xx are transient) or none are stored; the shell
  subscribes once and returns to sign-in. Screens just show the error; they never sign out themselves.
  A 401 that survives a refresh deliberately does *not* fire it: endpoints can refuse single products.
- `gameDetails/{id}.json` returns a literal `null` body for products the account does not own.
- Product-level `tags` have been observed as both `["12"]` and `[{"id":"12"}]`; `TagIdListConverter`
  accepts either.
- `getFilteredProducts` reports `hasHiddenProducts`, so the hidden pass can usually be skipped.
- A product's `rating` in `getFilteredProducts` is GOG's **community average in whole stars (0–5)**,
  not the user's own rating — a 254-game library came back all 0–4. The gogapidocs sample shows
  tenths (`41`), so the scale has changed at least once. The user's own ratings are a separate
  endpoint, `user/games_rating.json`, which does use stars×10.
- `content-system.gog.com` speaks snake_case, unlike embed, and publishes dates as
  `2023-09-19T18:36:09+0000` — an offset with no colon, which `System.Text.Json` rejects. See
  `LenientDateTimeOffsetConverter`.

## State

Working end to end against live GOG: OAuth2 login/refresh/storage, library sync, build listing,
manifest resolution, and downloading. A full 348 MiB game has been fetched, resumed (250/250 chunks
skipped), and SHA-256 verified.

The desktop app signs in, shows the owned library as a grid with cover art and live search, and its
detail panel installs and launches games. Absolute Drift has been fetched as a native macOS `.app`
bundle and resolved to a runnable play task end to end.

Windows-only titles run on macOS/Linux through CrossOver or Wine — A.D. 2044 has been launched
through a CrossOver 26.2 bottle end to end. The runner is picked per game in the detail panel; the
CLI's `play` still launches natively only.

Not built yet: install execution (dependencies/redistributables are listed but not
run), cloud saves, creating bottles or managing Wine builds, Proton.

### Launching (captured, not documented)

An installed build contains `goggame-{productId}.info` — at the install root on Windows/Linux, and
inside `Contents/Resources/` on macOS. It holds `playTasks[]`, each
`{category, isPrimary, name, path, type}`; `type: "FileTask"` is runnable and `"URLTask"` is a
manual or support link. `path` is relative to the install directory and backslash-separated on
Windows builds, so it goes through `ManifestResolver.ToRelativePath` — it is third-party input like
any manifest path.

macOS builds install *as* the bundle: `installDirectory` is `absolutedrift.app`, and the play task
path is `Contents/MacOS/absolutedrift`. `GameLauncher` detects the enclosing bundle and hands it to
`open` rather than exec'ing the inner binary, so LaunchServices gives it a dock icon and activation.

### Windows builds on macOS/Linux (runners)

A `WindowsRunner` turns a `LaunchTarget` into a `ProcessStartInfo`; nothing else about launching
changes. Goggles drives a runner the user already has and never installs one.

- **CrossOver:** `…/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine --bottle <name>
  --workdir <dir> <native exe path> <args>`. **Do not use `--cx-app`**: despite its help text it
  looks the path up inside the bottle's `C:` drive and fails with "could not find … Is it
  installed?" (confirmed against CrossOver 26.2). A plain argument is resolved as a native path.
- A bottle is a directory under `~/Library/Application Support/CrossOver/Bottles` containing
  `cxbottle.conf`; one runner is offered per bottle.
- **Wine:** `wine <exe> <args>` with `WINEPREFIX` set (Goggles' own, under app data). Discovery
  checks `PATH` *and* Homebrew / Wine.app locations explicitly, because an app started from the Dock
  does not inherit the shell's `PATH`.
- Play-task `arguments` are a single Windows command line; `WindowsCommandLine.Split` applies the C
  runtime's quoting rules so each argument reaches the game intact through `ArgumentList`.
- The runner's output is discarded like a native game's, so a runner that fails immediately is
  currently silent in the UI — run the command by hand to see why.

### Achievements: the Galaxy Communication Service (captured, not documented)

Games do not report achievements themselves. Their Galaxy SDK connects to a service Galaxy runs on
`127.0.0.1:9977`, and that service talks to GOG. Launched outside Galaxy, a game has nobody to report
to, so **Goggles only reads achievements and playtime today** — `GameplayClient` never writes. Heroic
solves this by bundling Comet (Rust, imLinguin/comet) as a separate process; Goggles is building the
service into Core instead, in phases; phases 1 (observe) and 2 (answer auth) are done.

- **Framing:** big-endian `u16` header length, a protobuf `gog.protocols.pb.Header` (`sort`, `type`,
  `size`, `oseq`), then `size` bytes of body. A reply quotes the request's `oseq` as `rseq`, header
  extension 100; extension 101 is a status code numbered like HTTP. `sort` 1 is the communication
  service. Definitions are vendored, CC0, in `Galaxy/Protocols` — see its README.
- **Auth is per game.** `AUTH_INFO_REQUEST` carries the *game's* `client_id`/`client_secret` (the
  ones in the build manifest); the service exchanges the user's refresh token for that client at
  `auth.gog.com/token?grant_type=refresh_token&without_new_session=1`, and returns the user id with
  `2 << 56` OR-ed in. Unlocks then go to `gameplay.gog.com/clients/{gameClientId}/users/{userId}/…`.
- **The SDK is a shim.** `LIBRARY_INFO_REQUEST` asks where GOG's "peer" library is; the service must
  download it (`cfg.gog.com/desktop-galaxy-peer/7/master/files-{windows,osx}.json`) and say where.
- **Playtime is the launcher's job, not the service's.** Comet never records sessions; Heroic does it
  itself after a game exits: `POST gameplay.gog.com/games/{productId}/users/{userId}/sessions` with
  `{session_date (unix seconds), time (minutes)}`, expecting `201`, skipping sessions under a minute
  and queueing them while offline (`updateGOGPlaytime` in Heroic's `gog/games.ts`). Unverified by
  Goggles so far.
- **Product id or client id?** `GameplayClient` reads achievements at `clients/{productId}/…` with
  Galaxy's own token, confirmed against a live account; Comet reads and writes at
  `clients/{gameClientId}/…` with the game's token. Which form the *write* accepts is unverified.
- Under Wine, some SDK versions also want a registered Windows service (Comet ships a dummy one).

**Confirmed against a real game (2026-11-14, Absolute Drift, native macOS, `libGalaxy64.dylib`):**

- The framing is exactly as above; Goggles re-encodes the captured frame byte for byte (test
  `A_frame_captured_from_a_real_game_decodes_as_expected`).
- The **first** message is `AUTH_INFO_REQUEST` — `client_id`, `client_secret`, `game_pid`, no
  `platform_type` or `openid`. This SDK never sent `LIBRARY_INFO_REQUEST`, so the peer download is
  not needed for every game; it is presumably a newer-SDK thing.
- The game's `client_id` (`52472569771423029`) is not its product id (`1136126792`); it is the
  `clientId` in the build manifest and in the installed `goggame-{id}.info`, so Goggles knows it
  before launch.
- Unanswered, it re-sent the identical request (same `oseq` 1, same connection) four times over
  about nine seconds, then went quiet; the game kept running.

**Answering auth (phase 2, same game, same day):** `CommunicationHandler` answers
`AUTH_INFO_REQUEST` with a refresh token issued to the *game's* client
(`AuthService.GetTokenForClientAsync` — `without_new_session=1`, not stored, and a 403 there means
"not owned", so it never raises `SignInRequired`), the `galaxyUserId` from `userData.json` with
`2 << 56` OR-ed in, and the username. Like Comet it first pushes `OVERLAY_STATE_CHANGE_NOTIFICATION`
(initialized) — Goggles has no overlay; it is sent because SDKs are known to work after it. The game
accepted the reply and immediately asked for, in order:

- three `sort 2 type 3` messages (Comet's "webbroker": topic subscriptions for push notifications —
  not decoded, not needed for achievements);
- `GET_USER_STATS_REQUEST` and `GET_USER_ACHIEVEMENTS_REQUEST`, both with the tagged user id;
- `GET_LEADERBOARDS_REQUEST`.

Unanswered, each is re-sent with the same `oseq` every few seconds. Comet never sends an error reply:
unimplemented requests get silence, an unowned game gets its connection closed
(`GalaxyConnectionRefusedException`), and a failed exchange still gets a reply without a token so the
game can run offline.

The rest still comes from Comet's source and the protocol definitions. `galaxy-service --capture
<dir>` is how each piece gets checked: it logs every message, answers nothing yet, and writes each
frame with credentials redacted (it answers what `CommunicationHandler` handles). Bodies it cannot decode
(other subprotocols) are written as received, so inspect captures before committing them.

Next phases: achievements and stats with an on-disk queue for
offline unlocks, then start the service from the desktop app around a launch, then CrossOver.

### Extras and DLC on the detail page (captured, not documented)

`account/gameDetails/{id}.json` — already fetched for the hero image — also carries the goodies and
owned DLC shown on the Extras tab. Sampled across 26 games on 2026-11-15:

- `extras[]` is a plain array of `{manualUrl, name, type, info, size}`; 20 of 26 games had some.
  `size` is display text (`"5 MB"`), and `type` is not reliable — one `"avatars"` entry was a 3 GB
  installer. Only `downloads` has the awkward `[["English", {…}]]` shape.
- `manualUrl` (`/downloads/{slug}/{id}`) is fetched from embed.gog.com with the bearer token and
  redirects to a signed CDN URL whose last segment is the real file name. The transport follows the
  redirect and drops `Authorization` on the way. `EmbedClient.DownloadExtraAsync` refuses anything
  that is not an embed `/downloads/` path, since the token goes wherever that URL points.
- `dlcs[]` lists only DLC the account owns, each shaped like its parent (so it has its own `extras`).
  Unverified against a real entry: the account used so far owns no DLC.
- `textInformation` was empty for all 26; `features` lists Galaxy features (achievements, cloud
  saves, overlay), which Goggles mostly does not provide, so neither is shown. `changelog` is HTML
  up to ~70 KB and is not shown yet.

Goodies are saved under `~/Downloads/Goggles/<title>/`, not the library root, which holds only what
GOG's builds produce.

### Library cache

`LibraryCache` keeps the last sync in `library.json` beside the tokens. On launch the library screen
shows it at once and calls `SyncAsync(knownChecksum)`, which costs one `userData.json` request when
`checksum.games` is unchanged (measured ~0.4 s, against ~2–3 s for a full three-page sync). The
Refresh button always syncs in full. The checksum is only trusted for `LibraryCache.MaxAge` (a day),
because it tracks what is owned, not every field shown — a community rating can change without it.
The file is tied to the account id, ignored if it belongs to anyone else or is an older format, and
deleted on sign-out. Heroic, by comparison, caches its GOG library forever and re-fetches all of it
on every online refresh.

Pages after the first are fetched four at a time; the empty-page guard still stops in page order, so
a `totalPages` GOG over-reports costs at most one wasted batch.

### Install layout

`GameLibrary` owns it. A build goes into the library root under the directory name the manifest
gives it — nothing is wrapped, renamed or added:

```
~/Games/Goggles/Tyranny/…
~/Games/Goggles/absolutedrift.app/…
```

A game counts as installed when GOG's own `goggame-{productId}.info` marker is present, at the
install root or in `Contents/Resources` for a macOS bundle. That keeps the installed filter to a
single directory listing rather than a manifest fetch per game.

**Known gap:** `PrepareFiles` sizes every file before fetching a chunk, and that marker is small
enough to arrive early, so an interrupted download leaves a full-size but hollow tree that still
reads as installed — Baldur's Gate II was left with an 18 MB executable of pure zeros this way, and
Play launched it. A completion record written by Goggles would fix it; one was built and then
removed deliberately, to keep the install directory exactly as GOG produces it.

### Uninstalling

`GameUninstaller` deletes a directory tree, so it refuses unless all three hold: the target is
strictly inside the library root, it is a real directory rather than a symlink, and it contains
`goggame-{productId}.info` for the product being removed. The guards live in Core, not the UI, so
the CLI is protected too. The CLI's `uninstall` is a dry run unless given `--yes`.

UI notes: `GogSession` is the single composition root — both the app and the CLI build the client
stack through it, so they cannot drift. The library grid is a `ListBox` over `Controls/TileWrapPanel`:
as many columns as a 208 px minimum allows, each stretched to fill the row (a `WrapPanel` left a gap
of up to a whole tile on the right), with covers held at GOG's 196x110 shape by
`AspectRatioDecorator`. Neither virtualises; fine for a few hundred tiles, worth revisiting for
larger libraries. Covers load for the whole library at once, throttled to six concurrent fetches.
Sign-in happens in the window on macOS: `Goggles.Desktop/Platform/MacWebView.cs` embeds a real `WKWebView`
through `NativeControlHost` and the Objective-C runtime, and the authorization code is taken from
the address once GOG redirects. No package and no bundled Chromium — the CEF-based Avalonia web
views would ship a browser, which is what avoiding Electron was meant to prevent. Elsewhere, and
behind a "sign in with your browser instead" link, the paste flow remains.

Three things that cost an evening and are easy to hit again:

- **Avalonia leaves the container view it wraps a native control in at a zero frame**, and AppKit
  views do not clip to their superview, so the page renders against an empty box at the window
  origin. `MacWebView.SyncFrame` positions the container itself, converting Avalonia's top-left
  coordinates to AppKit's bottom-left ones.
- **⌘V does nothing without an Edit menu.** macOS dispatches editing shortcuts through the main
  menu's key equivalents before the focused view sees them, and Avalonia provides no Edit menu.
  `MacEditMenu` builds one with AppKit directly — Avalonia's own `NativeMenu` did not work — and
  appends it to the existing menu bar, with nil targets so actions walk the responder chain.
- The web view claims Safari's user agent, because GOG serves a CAPTCHA more readily to one it does
  not recognise, and a CAPTCHA is the thing an embedded browser handles worst.

Download engine notes: chunks are verified on both sides of decompression before touching disk;
resume works by re-hashing chunk ranges of pre-existing files rather than keeping a state file, so
there is nothing to corrupt or clean up. Files are pre-sized and written at offset via
`RandomAccess`, so parallel workers never contend. Retries rotate CDN endpoints and re-fetch the
secure link on 403, since signing tokens expire mid-download.

Tokens are stored as plaintext JSON at `~/.config/Goggles/tokens.json` (mode 600). OS-backed secure
storage slots in behind `ITokenStore` later.

Roughly in order: library cache (done, as JSON) → manifest resolution (spike first) → download engine → delta
patching (deferred; v1 can do full downloads only) → install → launch + cloud saves. Windows titles
run through user-installed CrossOver or Wine; managing runners themselves (bottles, Wine builds,
Proton) is not planned for v1.
