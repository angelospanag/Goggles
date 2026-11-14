using System.Text.Json;

namespace Goggles.Core.Launching;

/// <summary>
/// Remembers which runner each Windows game uses, and the last one picked as the default for games
/// that have no choice yet. Stored as a small JSON file beside the tokens.
/// </summary>
public sealed class RunnerPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Lock _gate = new();

    public RunnerPreferences(string? path = null) => Path = path ?? DefaultPath();

    public string Path { get; }

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Goggles",
        "runners.json");

    /// <summary>
    /// The runner to preselect for <paramref name="productId"/>: its own choice if it still exists,
    /// otherwise the last one picked anywhere, otherwise the first available.
    /// </summary>
    public WindowsRunner? Choose(long productId, IReadOnlyList<WindowsRunner> available)
    {
        var stored = Read();

        return Match(stored.Games.GetValueOrDefault(productId))
               ?? Match(stored.Default)
               ?? available.FirstOrDefault();

        WindowsRunner? Match(string? id) =>
            id is null ? null : available.FirstOrDefault(runner => runner.Id == id);
    }

    /// <summary>Records <paramref name="runner"/> for this game, and as the default for new ones.</summary>
    public void Remember(long productId, WindowsRunner runner)
    {
        lock (_gate)
        {
            var stored = Read();
            stored.Games[productId] = runner.Id;
            stored.Default = runner.Id;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(temporary, Path, overwrite: true);
        }
    }

    private Stored Read()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path), JsonOptions) ?? new Stored()
                : new Stored();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A preference is a convenience: losing it means picking the runner again, not an error.
            return new Stored();
        }
    }

    private sealed class Stored
    {
        public string? Default { get; set; }

        public Dictionary<long, string> Games { get; set; } = [];
    }
}
