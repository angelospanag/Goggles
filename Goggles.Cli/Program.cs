using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Goggles.Cli;
using Goggles.Core;
using Goggles.Core.Api;
using Goggles.Core.Api.Auth;
using Goggles.Core.Api.ContentSystem;
using Goggles.Core.Diagnostics;
using Goggles.Core.Downloads;
using Goggles.Core.Installs;
using Goggles.Core.Launching;
using Goggles.Core.Library;
using Goggles.Core.Manifests;

var arguments = args.ToList();
var dumpDirectory = TakeOption(arguments, "--dump");
var outputDirectory = TakeOption(arguments, "--out") ?? "fixtures";
var includeHidden = TakeFlag(arguments, "--hidden");
var withDepots = TakeFlag(arguments, "--depots");
var operatingSystem = TakeOption(arguments, "--os") ?? "windows";
var requestedBuild = TakeOption(arguments, "--build");
var requestedBranch = TakeOption(arguments, "--branch");
var requestedPath = TakeOption(arguments, "--path") ?? "/";
var requestedLanguage = TakeOption(arguments, "--lang") ?? "en-US";
var requestedBitness = TakeOption(arguments, "--bitness") ?? "64";
var verifyHashes = TakeFlag(arguments, "--verify");
var dryRun = TakeFlag(arguments, "--dry-run");
var assumeYes = TakeFlag(arguments, "--yes");
var libraryRoot = TakeOption(arguments, "--library");
var parallelChunks = int.TryParse(TakeOption(arguments, "--parallel"), out var parsed) ? parsed : 4;
var command = arguments.FirstOrDefault();
var argument = arguments.Skip(1).FirstOrDefault();

var tokenStore = new FileTokenStore();
var tokenStorePath = tokenStore.Path;

try
{
    switch (command)
    {
        case "login":
            return await LoginAsync();

        case "whoami":
            return await WhoAmIAsync();

        case "library":
            return await LibraryAsync();

        case "stats":
            return await StatsAsync();

        case "logout":
            return await LogoutAsync();

        case "builds":
            return await BuildsAsync();

        case "manifest":
            return await ManifestAsync();

        case "securelink":
            return await SecureLinkAsync();

        case "chunk":
            return await ChunkAsync();

        case "resolve":
            return await ResolveAsync();

        case "download":
            return await DownloadAsync();

        case "play":
            return await PlayAsync();

        case "uninstall":
            return UninstallGame();

        case null or "help" or "--help" or "-h":
            PrintUsage();
            return command is null ? 2 : 0;

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintUsage();
            return 2;
    }
}
catch (GogAuthException exception)
{
    Console.Error.WriteLine($"Authentication failed: {exception.Message}");
    if (exception.ResponseBody is { Length: > 0 } body)
    {
        Console.Error.WriteLine(body);
    }

    return 1;
}
catch (GogApiException exception)
{
    Console.Error.WriteLine($"GOG request failed: {exception.Message}");
    return 1;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"Could not reach GOG: {exception.Message}");
    return 1;
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine($"Bad data from GOG: {exception.Message}");
    return 1;
}

async Task<int> LoginAsync()
{
    using var clients = CreateSession();
    var url = clients.Auth.BuildAuthorizationUrl();

    Console.WriteLine("Opening GOG sign-in. If nothing happens, visit this URL yourself:");
    Console.WriteLine();
    Console.WriteLine($"  {url}");
    Console.WriteLine();
    TryOpenBrowser(url);

    Console.WriteLine("After signing in you will land on a blank-looking embed.gog.com page.");
    Console.WriteLine("Copy the full address from the browser's address bar and paste it here.");
    Console.Write("> ");

    var pasted = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(pasted))
    {
        Console.Error.WriteLine("Nothing pasted; aborting.");
        return 2;
    }

    // Accept either the whole redirect URL or a bare code, since people paste both.
    string code;
    if (Uri.TryCreate(pasted, UriKind.Absolute, out var redirect))
    {
        if (!clients.Auth.TryExtractCode(redirect, out var extracted))
        {
            Console.Error.WriteLine("That URL has no authorization code in it.");
            Console.Error.WriteLine("Expected something like https://embed.gog.com/on_login_success?origin=client&code=...");
            return 2;
        }

        code = extracted!;
    }
    else
    {
        code = pasted;
    }

    var tokens = await clients.Auth.ExchangeCodeAsync(code);
    Console.WriteLine();
    Console.WriteLine($"Signed in. Token expires {tokens.ExpiresAt:u}, stored at {tokenStorePath}");

    var user = await clients.Embed.GetUserDataAsync();
    Console.WriteLine($"Hello, {user.Username} ({user.Email}) — {user.PurchasedItems?.Games ?? 0} games owned.");

    return 0;
}

async Task<int> WhoAmIAsync()
{
    using var clients = CreateSession();
    var user = await clients.Embed.GetUserDataAsync();

    Console.WriteLine($"Username    {user.Username}");
    Console.WriteLine($"Email       {user.Email}");
    Console.WriteLine($"User id     {user.UserId}");
    Console.WriteLine($"Galaxy id   {user.GalaxyUserId}");
    Console.WriteLine($"Country     {user.Country}");
    Console.WriteLine($"Games       {user.PurchasedItems?.Games ?? 0}");
    Console.WriteLine($"Checksum    {user.Checksum?.Games}");

    return 0;
}

/// <summary>
/// Calls the same gameplay.gog.com endpoints <c>GameDetailViewModel</c>'s achievement/activity
/// loading does, but also prints the raw <c>/sessions</c> response body — this host has no
/// official documentation, so this is the only way to confirm
/// <c>GameplayClient.GetSessionSummaryAsync</c>'s decoding actually matches what GOG returns for a
/// real account.
/// </summary>
async Task<int> StatsAsync()
{
    if (argument is null || !long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine("Usage: stats <productId>");
        return 2;
    }

    using var clients = CreateSession();
    var tokens = await clients.Auth.GetValidTokenAsync();
    if (tokens.UserId is not { } rawUserId || !long.TryParse(rawUserId, out var userId))
    {
        Console.Error.WriteLine("Signed-in token has no usable user id.");
        return 1;
    }

    var achievements = await clients.Gameplay.GetAchievementsAsync(productId, userId);
    var unlocked = achievements.Unlocked();
    Console.WriteLine($"Achievements: {unlocked.Count}/{achievements.Total} unlocked");
    foreach (var achievement in unlocked)
    {
        Console.WriteLine($"  - {achievement.Name} (unlocked {achievement.UnlockedAt:O})");
    }

    var summary = await clients.Gameplay.GetSessionSummaryAsync(productId, userId);
    Console.WriteLine(summary.Available
        ? $"Playtime: {summary.Minutes} minutes"
        : "Playtime: not available (unrecognized response shape)");

    using var rawHandler = new GogAuthHandler(clients.Auth) { InnerHandler = new HttpClientHandler() };
    using var rawClient = new HttpClient(rawHandler);

    var achievementsUrl = new Uri(GogHosts.Gameplay, $"clients/{productId}/users/{userId}/achievements");
    Console.WriteLine($"Raw achievements response ({achievementsUrl}):");
    Console.WriteLine(await rawClient.GetStringAsync(achievementsUrl));

    var sessionsUrl = new Uri(GogHosts.Gameplay, $"clients/{productId}/users/{userId}/sessions");
    Console.WriteLine($"Raw sessions response ({sessionsUrl}):");
    Console.WriteLine(await rawClient.GetStringAsync(sessionsUrl));

    return 0;
}

async Task<int> LibraryAsync()
{
    using var clients = CreateSession();
    var sync = clients.Library;

    var stopwatch = Stopwatch.StartNew();
    var result = await sync.SyncAsync(includeHidden: includeHidden);
    stopwatch.Stop();

    foreach (var game in result.Games.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase))
    {
        var platforms = game.Platforms == GamePlatforms.None ? "-" : game.Platforms.ToString().Replace(", ", "/");
        var dlc = game.DlcCount > 0 ? $" +{game.DlcCount} DLC" : string.Empty;
        var hidden = game.IsHidden ? " [hidden]" : string.Empty;

        Console.WriteLine($"{game.Id,-12} {Truncate(game.Title, 48),-48} {platforms,-20}{dlc}{hidden}");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"{result.Games.Count} games over {result.PagesFetched} page(s) in {stopwatch.ElapsedMilliseconds} ms.");
    Console.WriteLine($"Library checksum: {result.Checksum}");

    return 0;
}

async Task<int> LogoutAsync()
{
    using var clients = CreateSession();
    await clients.Auth.SignOutAsync();

    Console.WriteLine($"Cleared {tokenStorePath}");
    return 0;
}

async Task<int> BuildsAsync()
{
    if (!long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine("Usage: builds <productId> [--os windows|osx|linux]");
        return 2;
    }

    using var clients = CreateSession();
    var builds = await clients.Builds.GetBuildsAsync(productId, operatingSystem);

    Console.WriteLine($"{builds.Count} of {builds.TotalCount} builds for {productId} on {operatingSystem}");
    if (builds.HasPrivateBranches)
    {
        Console.WriteLine("(this product has private branches)");
    }

    Console.WriteLine();

    foreach (var build in builds.Items)
    {
        var branch = build.Branch ?? "public";
        var published = build.DatePublished?.ToString("yyyy-MM-dd") ?? "?";
        var tags = build.Tags.Count > 0 ? $" [{string.Join(',', build.Tags)}]" : string.Empty;

        Console.WriteLine(
            $"{build.BuildId,-22} gen{build.Generation}  {published}  {branch,-10} {build.VersionName}{tags}");
    }

    return 0;
}

async Task<int> ManifestAsync()
{
    if (!long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine("Usage: manifest <productId> [--os ...] [--build <id>] [--depots]");
        return 2;
    }

    using var clients = CreateSession();
    var builds = await clients.Builds.GetBuildsAsync(productId, operatingSystem);

    // GOG returns newest first across every branch, so the top entry is often a beta. Without an
    // explicit choice, take the newest build on the default branch (branch: null).
    var build = requestedBuild is not null
        ? builds.Items.FirstOrDefault(candidate => candidate.BuildId == requestedBuild)
        : requestedBranch is not null
            ? builds.Items.FirstOrDefault(candidate => candidate.Branch == requestedBranch)
            : builds.Items.FirstOrDefault(candidate => candidate.Branch is null);

    if (build is null)
    {
        Console.Error.WriteLine(
            $"No matching build for {productId} on {operatingSystem}. Try: builds {productId}");
        return 1;
    }

    if (build.Link is null)
    {
        Console.Error.WriteLine($"Build {build.BuildId} has no metadata link.");
        return 1;
    }

    Console.WriteLine(
        $"Build {build.BuildId} (generation {build.Generation}, branch {build.Branch ?? "default"}) — {build.VersionName}");
    Console.WriteLine($"  {build.Link}");
    Console.WriteLine();

    var json = await clients.Cdn.GetMetaJsonAsync(build.Link);
    var saved = Save($"build-{productId}-{operatingSystem}-{build.BuildId}.json", json);

    using var document = JsonDocument.Parse(json);
    Console.WriteLine($"Build metadata ({json.Length:N0} bytes decompressed) -> {saved}");
    JsonOutline.Print(document.RootElement);

    if (!withDepots)
    {
        Console.WriteLine();
        Console.WriteLine("Pass --depots to follow the depot manifests too.");
        return 0;
    }

    if (!document.RootElement.TryGetProperty("depots", out var depots) ||
        depots.ValueKind != JsonValueKind.Array)
    {
        Console.WriteLine();
        Console.WriteLine("No depots array in this build's metadata.");
        return 0;
    }

    var index = 0;
    foreach (var depot in depots.EnumerateArray())
    {
        if (!depot.TryGetProperty("manifest", out var manifest) ||
            manifest.GetString() is not { Length: > 0 } hash)
        {
            continue;
        }

        var depotJson = await clients.Cdn.GetMetaJsonAsync(hash);
        var depotPath = Save($"depot-{productId}-{operatingSystem}-{hash}.json", depotJson);

        Console.WriteLine();
        Console.WriteLine($"Depot {++index} ({depotJson.Length:N0} bytes decompressed) -> {depotPath}");

        // The first one is enough to read the shape; the rest are saved for later.
        if (index == 1)
        {
            using var depotDocument = JsonDocument.Parse(depotJson);
            JsonOutline.Print(depotDocument.RootElement);
        }
    }

    return 0;
}

async Task<int> SecureLinkAsync()
{
    if (!long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine("Usage: securelink <productId> [--path /]");
        return 2;
    }

    using var clients = CreateSession();
    var json = await clients.SecureLinks.GetSecureLinkJsonAsync(productId, requestedPath);
    var saved = Save($"securelink-{productId}.json", json);

    using var document = JsonDocument.Parse(json);
    Console.WriteLine($"Secure link for {productId}, path {requestedPath} -> {saved}");
    Console.WriteLine();
    JsonOutline.Print(document.RootElement, maxDepth: 5);

    return 0;
}

async Task<int> ChunkAsync()
{
    var compressedMd5 = arguments.Skip(2).FirstOrDefault();

    if (!long.TryParse(argument, out var productId) || compressedMd5 is null)
    {
        Console.Error.WriteLine("Usage: chunk <productId> <compressedMd5>");
        return 2;
    }

    using var clients = CreateSession();
    var link = await clients.SecureLinks.GetSecureLinkAsync(productId);
    var endpoint = link.SelectEndpoint();

    if (endpoint is null)
    {
        Console.Error.WriteLine($"No generation 2 endpoint offered for {productId}.");
        return 1;
    }

    var uri = endpoint.BuildChunkUri(compressedMd5);
    Console.WriteLine($"Endpoint {endpoint.EndpointName} (priority {endpoint.Priority})");
    Console.WriteLine($"  {uri.GetLeftPart(UriPartial.Path)}");
    Console.WriteLine();

    using var http = new HttpClient();
    var compressed = await http.GetByteArrayAsync(uri);

    var compressedHash = Convert.ToHexStringLower(MD5.HashData(compressed));
    Console.WriteLine($"Downloaded    {compressed.Length:N0} bytes");
    Console.WriteLine($"compressedMd5 {compressedHash}  {(compressedHash == compressedMd5 ? "OK" : "MISMATCH")}");

    var plain = CdnClient.DecompressToBytes(compressed);
    Console.WriteLine($"Inflated      {plain.Length:N0} bytes");
    Console.WriteLine($"md5           {Convert.ToHexStringLower(MD5.HashData(plain))}");

    return compressedHash == compressedMd5 ? 0 : 1;
}

async Task<int> ResolveAsync()
{
    if (!long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine("Usage: resolve <productId> [--os ...] [--branch ...] [--lang en-US]");
        return 2;
    }

    using var clients = CreateSession();
    var build = await clients.Manifests.ResolveAsync(productId, await ResolveOptionsAsync(clients));

    Console.WriteLine($"Build {build.BuildId} — {build.Platform} — install dir '{build.InstallDirectory}'");
    Console.WriteLine(
        $"{build.Files.Count:N0} files, {build.TotalChunks:N0} chunks, " +
        $"{Bytes(build.TotalSize)} on disk, {Bytes(build.TotalDownloadSize)} to download");

    if (build.Dependencies.Count > 0)
    {
        Console.WriteLine($"Dependencies: {string.Join(", ", build.Dependencies)}");
    }

    Console.WriteLine();

    foreach (var file in build.Files.OrderByDescending(file => file.Size).Take(15))
    {
        var executable = file.IsExecutable ? " *" : string.Empty;
        Console.WriteLine($"{Bytes(file.Size),12}  {file.Chunks.Count,4} chunks  {file.Path}{executable}");
    }

    if (build.Files.Count > 15)
    {
        Console.WriteLine($"… and {build.Files.Count - 15:N0} more");
    }

    return 0;
}

async Task<int> DownloadAsync()
{
    if (!long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine(
            "Usage: download <productId> [--os ...] [--lang ...] [--parallel N] [--library <dir>]");
        return 2;
    }

    var options = new DownloadOptions
    {
        MaxParallelChunks = parallelChunks,
        VerifyFileHashes = verifyHashes,
    };

    using var clients = CreateSession(options);
    var build = await clients.Manifests.ResolveAsync(productId, await ResolveOptionsAsync(clients));

    var destination = new GameLibrary(LibraryRoot()).InstallPathFor(productId, build.InstallDirectory);
    Console.WriteLine($"Build {build.BuildId} -> {destination}");
    Console.WriteLine(
        $"{build.Files.Count:N0} files, {Bytes(build.TotalDownloadSize)} to fetch " +
        $"({Bytes(build.TotalSize)} installed), {parallelChunks} at a time");
    Console.WriteLine();

    var report = await clients.Downloads.DownloadAsync(build, destination, new Progress<DownloadProgress>(Render));

    Console.WriteLine();
    Console.WriteLine(
        $"Done in {report.Duration.TotalSeconds:N1}s — {report.ChunksDownloaded:N0} chunks downloaded, " +
        $"{report.ChunksSkipped:N0} already present, {Bytes(report.BytesWritten)} written.");

    return 0;

    static void Render(DownloadProgress progress)
    {
        var path = progress.CurrentPath ?? string.Empty;
        Console.Write(
            $"\r{progress.Fraction,7:P1}  {progress.ChunksCompleted:N0}/{progress.TotalChunks:N0} chunks  " +
            $"{Truncate(path, 44),-44}");
    }
}

async Task<int> PlayAsync()
{
    if (!long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine("Usage: play <productId> [--library <dir>] [--dry-run]");
        return 2;
    }

    // Games installed by the desktop app land here; --library points at another install root.
    var root = LibraryRoot();
    var installed = new GameLibrary(root).Find(productId);

    if (installed is null)
    {
        Console.Error.WriteLine($"No install of {productId} found under {root}.");
        return 1;
    }

    var target = await GameLauncher.ResolveTargetAsync(installed.InstallPath, productId);

    if (target is null)
    {
        Console.Error.WriteLine($"'{installed.InstallPath}' has no runnable play task.");
        return 1;
    }

    Console.WriteLine($"Task         {target.Name}");
    Console.WriteLine($"Executable   {target.Executable}");
    Console.WriteLine($"Working dir  {target.WorkingDirectory}");
    Console.WriteLine($"Bundle       {target.Bundle ?? "-"}");
    Console.WriteLine($"Exists       {File.Exists(target.Executable)}");

    if (dryRun)
    {
        return 0;
    }

    new GameLauncher().Launch(target);
    Console.WriteLine("Launched.");

    return 0;
}

int UninstallGame()
{
    if (!long.TryParse(argument, out var productId))
    {
        Console.Error.WriteLine("Usage: uninstall <productId> [--library <dir>] [--yes]");
        return 2;
    }

    var root = LibraryRoot();
    var install = new GameLibrary(root).Find(productId);

    if (install is null)
    {
        Console.Error.WriteLine($"No install of {productId} found under {root}.");
        return 1;
    }

    var size = GameUninstaller.Measure(install.InstallPath);

    if (!assumeYes)
    {
        Console.WriteLine($"Would delete {install.InstallPath} ({Bytes(size)}).");
        Console.WriteLine("Re-run with --yes to actually remove it.");
        return 0;
    }

    try
    {
        var result = new GameUninstaller().Uninstall(root, install.InstallPath, productId);
        Console.WriteLine(result.Removed
            ? $"Removed {result.Path} — freed {Bytes(result.BytesFreed)}."
            : "Nothing to remove.");

        return 0;
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

string LibraryRoot() => libraryRoot ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Games",
    "Goggles");

/// <summary>
/// Builds can bundle DLC the account does not own, and GOG refuses to sign downloads for those, so
/// the owned set is fetched and handed to the resolver.
/// </summary>
async Task<ResolveOptions> ResolveOptionsAsync(GogSession session) => new()
{
    Os = operatingSystem,
    Branch = requestedBranch,
    BuildId = requestedBuild,
    Language = requestedLanguage,
    Bitness = requestedBitness,
    OwnedProductIds = (await session.Embed.GetOwnedProductIdsAsync()).ToHashSet(),
};

string Save(string fileName, string json)
{
    Directory.CreateDirectory(outputDirectory);
    var path = Path.Combine(outputDirectory, fileName);

    // Reformatted on the way out: these are read by humans trying to work out the format.
    using var document = JsonDocument.Parse(json);
    File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));

    return path;
}

void PrintUsage()
{
    Console.WriteLine(
        """
        goggles — development harness for Goggles.Core

        Commands:
          login              Sign in to GOG and store the tokens
          whoami             Show the signed-in account
          library            Sync and list the owned library
          logout             Forget the stored tokens
          builds <id>        List content-system builds for a product
          manifest <id>      Fetch a build's metadata, decompress it and outline its shape
          securelink <id>    Fetch the signed CDN endpoints for a product's depot store
          chunk <id> <md5>   Download one depot chunk by compressedMd5 and verify it
          resolve <id>       Resolve a build into the file list that would be downloaded
          download <id>      Download a build into <library>/<installDirectory>
          play <id>          Launch an installed game (--dry-run resolves without starting it)
          uninstall <id>     Delete an installed game (dry run unless --yes)
          stats <id>         Print achievement/playtime stats, plus the raw sessions response body
                              (gameplay.gog.com is undocumented — this is how to verify decoding
                              against a real account)

        Options:
          --hidden           Include hidden products (library)
          --os <name>        windows | osx | linux (default: windows)
          --build <id>       Which build to fetch (default: newest on the default branch)
          --branch <name>    Pick the newest build on a named branch instead
          --depots           Also follow the depot manifests (manifest)
          --lang <tag>       Depot language to install (default: en-US)
          --bitness <bits>   32 or 64 (default: 64)
          --parallel <n>     Concurrent chunk downloads (default: 4)
          --verify           Re-check every finished file's SHA-256
          --path <path>      Content store path for securelink (default: /)
          --library <dir>    Where games are installed (default: ~/Games/Goggles)
          --out <dir>        Where to save fetched manifests (default: fixtures)
          --dump <dir>       Write every raw response to <dir>, for building test fixtures
        """);
}

static void TryOpenBrowser(Uri url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true })?.Dispose();
    }
    catch (Exception exception)
    {
        // Headless machines and locked-down desktops both land here; the printed URL still works.
        Console.Error.WriteLine($"  (could not open a browser automatically: {exception.Message})");
    }
}

GogSession CreateSession(DownloadOptions? downloadOptions = null) => GogSession.Create(
    tokenStore,
    downloadOptions,
    dumpDirectory is null
        ? null
        : inner => new ResponseDumpHandler(dumpDirectory, Console.Error) { InnerHandler = inner });

static string Bytes(long value) => value switch
{
    >= 1L << 30 => $"{value / (double)(1L << 30):N2} GiB",
    >= 1L << 20 => $"{value / (double)(1L << 20):N1} MiB",
    >= 1L << 10 => $"{value / (double)(1L << 10):N0} KiB",
    _ => $"{value} B",
};

static string Truncate(string value, int length) =>
    value.Length <= length ? value : string.Concat(value.AsSpan(0, length - 1), "…");

static string? TakeOption(List<string> arguments, string name)
{
    var index = arguments.IndexOf(name);
    if (index < 0)
    {
        return null;
    }

    if (index + 1 >= arguments.Count)
    {
        throw new ArgumentException($"{name} needs a value.");
    }

    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);

    return value;
}

static bool TakeFlag(List<string> arguments, string name) => arguments.Remove(name);
