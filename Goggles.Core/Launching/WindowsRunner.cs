using System.Diagnostics;
using System.Text;

namespace Goggles.Core.Launching;

/// <summary>
/// Runs a Windows build on macOS or Linux through a compatibility layer — what Heroic calls the
/// game's "Wine version". Goggles does not ship or manage the layer itself; it drives one the user
/// already has.
/// </summary>
/// <remarks>
/// Only the command line differs between runners: the play task, its working directory and its
/// arguments come from GOG's <c>goggame-{id}.info</c> exactly as they do for a native build, so a
/// runner is nothing more than a way of turning a <see cref="LaunchTarget"/> into a process to start.
/// </remarks>
public abstract record WindowsRunner
{
    /// <summary>
    /// Identifies the runner across restarts, so a game's choice can be remembered — e.g.
    /// <c>crossover:Games</c> or <c>wine:/opt/homebrew/bin/wine</c>.
    /// </summary>
    public abstract string Id { get; }

    /// <summary>A short label for settings and error messages, e.g. "CrossOver (Games)".</summary>
    public abstract string DisplayName { get; }

    /// <summary>The program that actually gets started — wine, or CrossOver's wrapper around it.</summary>
    public abstract string Executable { get; }

    /// <summary>Whether the runner is still installed where it was found.</summary>
    public bool IsAvailable => File.Exists(Executable);

    /// <summary>Builds the command line for <paramref name="target"/>, without starting anything.</summary>
    public abstract ProcessStartInfo CreateStartInfo(LaunchTarget target);

    /// <summary>
    /// The play task's arguments, split the way Windows would split them. GOG ships them as one
    /// Windows command-line string; passing them through <see cref="ProcessStartInfo.ArgumentList"/>
    /// keeps them intact rather than relying on a second round of shell quoting.
    /// </summary>
    protected static IEnumerable<string> GameArguments(LaunchTarget target) =>
        target.Arguments is { Length: > 0 } arguments ? WindowsCommandLine.Split(arguments) : [];
}

/// <summary>
/// CodeWeavers' CrossOver, running the game in one of the user's bottles.
/// </summary>
/// <remarks>
/// CrossOver's own <c>wine</c> wrapper takes <c>--bottle</c> and converts a native path for
/// <c>--workdir</c>, so the bottle's prefix never has to be located by hand. The game is passed as
/// a plain argument, which wine resolves as a native path — <em>not</em> through <c>--cx-app</c>,
/// which despite its help text looks the path up inside the bottle's <c>C:</c> drive and fails
/// with "could not find … Is it installed?" (confirmed against CrossOver 26.2). Bottles are where CrossOver keeps installed redistributables and per-game settings, which is
/// most of what makes it the easier runner to start with.
/// </remarks>
public sealed record CrossOverRunner : WindowsRunner
{
    /// <summary>The standard install location.</summary>
    public const string DefaultAppPath = "/Applications/CrossOver.app";

    public required string AppPath { get; init; }

    public required string Bottle { get; init; }

    public override string Id => $"crossover:{Bottle}";

    public override string DisplayName => $"CrossOver ({Bottle})";

    public override string Executable =>
        Path.Combine(AppPath, "Contents", "SharedSupport", "CrossOver", "bin", "wine");

    public override ProcessStartInfo CreateStartInfo(LaunchTarget target)
    {
        var startInfo = new ProcessStartInfo(Executable) { WorkingDirectory = target.WorkingDirectory };

        startInfo.ArgumentList.Add("--bottle");
        startInfo.ArgumentList.Add(Bottle);
        startInfo.ArgumentList.Add("--workdir");
        startInfo.ArgumentList.Add(target.WorkingDirectory);
        startInfo.ArgumentList.Add(target.Executable);

        foreach (var argument in GameArguments(target))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

/// <summary>
/// A plain Wine build — Homebrew's, Gcenx's, Apple's Game Porting Toolkit, or a distribution's —
/// running the game in a given prefix.
/// </summary>
public sealed record WineRunner : WindowsRunner
{
    /// <summary>Path to the <c>wine</c> binary.</summary>
    public required string WineBinary { get; init; }

    /// <summary>The prefix (<c>WINEPREFIX</c>) to run in; Wine creates it on first use.</summary>
    public required string Prefix { get; init; }

    public override string Id => $"wine:{WineBinary}";

    public override string DisplayName => $"Wine ({WineBinary})";

    public override string Executable => WineBinary;

    public override ProcessStartInfo CreateStartInfo(LaunchTarget target)
    {
        var startInfo = new ProcessStartInfo(WineBinary) { WorkingDirectory = target.WorkingDirectory };

        startInfo.Environment["WINEPREFIX"] = Prefix;

        // Wine's debug channels are chatty, and the output is discarded anyway (see
        // GameLauncher.StartDetached) — silencing them saves the game the cost of producing it.
        startInfo.Environment["WINEDEBUG"] = "-all";

        startInfo.ArgumentList.Add(target.Executable);

        foreach (var argument in GameArguments(target))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

/// <summary>
/// Splits a Windows command line the way <c>CommandLineToArgvW</c> and the Microsoft C runtime do.
/// </summary>
/// <remarks>
/// The rules are odd but fixed: whitespace separates arguments outside quotes; <c>2n</c>
/// backslashes before a quote become <c>n</c> backslashes and the quote toggles quoting;
/// <c>2n+1</c> backslashes before a quote become <c>n</c> backslashes and a literal quote;
/// backslashes anywhere else are literal; and inside quotes, <c>""</c> is a literal quote.
/// </remarks>
public static class WindowsCommandLine
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArgument = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);

                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--;
                }

                hasArgument = true;
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                hasArgument = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArgument)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    hasArgument = false;
                }

                continue;
            }

            current.Append(c);
            hasArgument = true;
        }

        if (hasArgument)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
