using System.Diagnostics;
using System.Text.Json;
using Goggles.Core.Manifests;

namespace Goggles.Core.Launching;

/// <summary>
/// Starts an installed game, using the play tasks GOG ships with the build.
/// </summary>
public sealed class GameLauncher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Finds the <c>goggame-{productId}.info</c> file for an install, or null if it has none.
    /// </summary>
    /// <remarks>
    /// macOS builds put it inside the application bundle rather than at the install root.
    /// </remarks>
    public static string? FindInfoFile(string installDirectory, long productId)
    {
        var name = $"goggame-{productId}.info";

        string[] candidates =
        [
            Path.Combine(installDirectory, name),
            Path.Combine(installDirectory, "Contents", "Resources", name),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<GameInfo?> ReadInfoAsync(
        string installDirectory,
        long productId,
        CancellationToken cancellationToken = default)
    {
        if (FindInfoFile(installDirectory, productId) is not { } path)
        {
            return null;
        }

        await using var stream = File.OpenRead(path);

        try
        {
            return await JsonSerializer
                .DeserializeAsync<GameInfo>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Could not read '{path}': {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Works out what "Play" should start: the primary runnable task, or the first runnable one if
    /// the build never marks a primary.
    /// </summary>
    public static LaunchTarget? ResolveTarget(GameInfo info, string installDirectory)
    {
        var runnable = info.PlayTasks.Where(task => task.IsRunnable).ToList();
        var task = runnable.FirstOrDefault(candidate => candidate.IsPrimary) ?? runnable.FirstOrDefault();

        if (task?.Path is not { Length: > 0 } path)
        {
            return null;
        }

        // The info file is content shipped by a third party; a path escaping the install directory
        // is treated the same way as one in a manifest.
        var executable = Path.Combine(installDirectory, ManifestResolver.ToRelativePath(path));

        var workingDirectory = task.WorkingDir is { Length: > 0 } relative
            ? Path.Combine(installDirectory, ManifestResolver.ToRelativePath(relative))
            : Path.GetDirectoryName(executable) ?? installDirectory;

        return new LaunchTarget
        {
            Name = task.Name ?? info.Name ?? "Game",
            Executable = executable,
            Arguments = task.Arguments,
            WorkingDirectory = workingDirectory,
            // A macOS build is an application bundle: handing the bundle to LaunchServices gets the
            // dock icon, activation and entitlements that exec'ing the inner binary does not.
            Bundle = FindEnclosingBundle(installDirectory, executable),
        };
    }

    /// <summary>Convenience: read the info file and resolve in one step.</summary>
    public static async Task<LaunchTarget?> ResolveTargetAsync(
        string installDirectory,
        long productId,
        CancellationToken cancellationToken = default)
    {
        var info = await ReadInfoAsync(installDirectory, productId, cancellationToken).ConfigureAwait(false);

        return info is null ? null : ResolveTarget(info, installDirectory);
    }

    /// <summary>Starts the game and returns the process, or null if the platform detached it.</summary>
    /// <param name="target">What to start, from <see cref="ResolveTarget"/>.</param>
    /// <param name="runner">
    /// Runs a Windows build through Wine or CrossOver; null to start the build natively.
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// The executable named by the play task is missing, or the runner is no longer installed.
    /// </exception>
    public Process? Launch(LaunchTarget target, WindowsRunner? runner = null)
    {
        if (!File.Exists(target.Executable))
        {
            throw new FileNotFoundException(
                $"'{target.Name}' points at {target.Executable}, which is not there.",
                target.Executable);
        }

        if (runner is not null)
        {
            if (!runner.IsAvailable)
            {
                throw new FileNotFoundException(
                    $"{runner.DisplayName} is not installed at {runner.Executable} any more.",
                    runner.Executable);
            }

            // A Windows .exe needs no executable bit and no bundle handling; the runner owns how
            // it starts.
            return StartDetached(runner.CreateStartInfo(target));
        }

        EnsureExecutable(target.Executable);

        if (target.Bundle is { Length: > 0 } bundle && OperatingSystem.IsMacOS())
        {
            // Deliberately without -n: if the game is already running, bring it to the front rather
            // than starting a second copy of it.
            var open = new ProcessStartInfo("open");
            open.ArgumentList.Add(bundle);

            return StartDetached(open);
        }

        var startInfo = new ProcessStartInfo(target.Executable)
        {
            WorkingDirectory = target.WorkingDirectory,
        };

        if (target.Arguments is { Length: > 0 } arguments)
        {
            startInfo.Arguments = arguments;
        }

        return StartDetached(startInfo);
    }

    /// <summary>
    /// Starts a child without letting it share our console.
    /// </summary>
    /// <remarks>
    /// An inherited pipe that nobody drains fills after a few dozen kilobytes and then blocks the
    /// writer. Games log freely, so a launched game can sit there doing nothing until the parent
    /// exits and the pipe breaks — which looks exactly like "it only starts when I close Goggles".
    /// Both streams are therefore redirected and discarded, and stdin is closed so nothing can
    /// block waiting to be typed at.
    /// </remarks>
    private static Process? StartDetached(ProcessStartInfo startInfo)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        if (Process.Start(startInfo) is not { } process)
        {
            return null;
        }

        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        return process;
    }

    /// <summary>
    /// The download engine sets the executable bit from manifest flags, but a build that neglects
    /// to flag its own entry point would otherwise be unlaunchable.
    /// </summary>
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);

        if (!mode.HasFlag(UnixFileMode.UserExecute))
        {
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Walks up from the executable to the outermost <c>.app</c> within the install.</summary>
    private static string? FindEnclosingBundle(string installDirectory, string executable)
    {
        var root = Path.GetFullPath(installDirectory);
        var current = Path.GetFullPath(executable);
        string? bundle = null;

        while (current.StartsWith(root, StringComparison.Ordinal))
        {
            if (current.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                bundle = current;
            }

            if (Path.GetDirectoryName(current) is not { } parent || parent == current)
            {
                break;
            }

            current = parent;
        }

        return bundle;
    }
}

/// <summary>Everything needed to start a game.</summary>
public sealed record LaunchTarget
{
    public required string Name { get; init; }

    public required string Executable { get; init; }

    public required string WorkingDirectory { get; init; }

    public string? Arguments { get; init; }

    /// <summary>The enclosing macOS application bundle, when the executable lives inside one.</summary>
    public string? Bundle { get; init; }
}
