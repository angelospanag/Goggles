using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Goggles.Core.Api;
using Goggles.Core.Api.Embed;
using Goggles.Core.Api.Embed.Models;

namespace Goggles.Desktop.ViewModels;

/// <summary>One goodie on the detail page's Extras tab, downloadable on its own.</summary>
public sealed partial class GameExtraItem : ObservableObject
{
    private readonly EmbedClient _embed;
    private readonly string _manualUrl;
    private readonly string _directory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string? SavedPath { get; set; }

    public GameExtraItem(EmbedClient embed, GameExtra extra, string? from, string directory)
    {
        _embed = embed;
        _manualUrl = extra.ManualUrl!;
        _directory = directory;

        Name = extra.Name is { Length: > 0 } name ? Capitalise(name) : "Untitled";
        Detail = string.Join(" · ", new[] { KindLabel(extra.Type), extra.Size, from }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public string Name { get; }

    /// <summary>Kind, size and — for a DLC's goodie — which DLC it came with.</summary>
    public string Detail { get; }

    private bool CanDownload => !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        IsDownloading = true;
        Status = "Starting…";
        try
        {
            SavedPath = await _embed.DownloadExtraAsync(
                _manualUrl,
                _directory,
                new Progress<long>(bytes => Status = $"{bytes / (double)(1L << 20):N1} MiB"));
            Status = null;
        }
        catch (Exception exception) when (exception is GogApiException or HttpRequestException or IOException
                                              or UnauthorizedAccessException)
        {
            Status = $"Failed: {exception.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Selects the downloaded file in the system's file manager.</summary>
    [RelayCommand]
    private void Reveal()
    {
        if (SavedPath is not { } path)
        {
            return;
        }

        var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", ["-R", path])
            : OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            : new ProcessStartInfo("xdg-open", [Path.GetDirectoryName(path)!]);

        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Status = $"Could not open the folder: {exception.Message}";
        }
    }

    /// <summary>GOG's <c>type</c> values are lower-case plurals; these read better as labels.</summary>
    private static string? KindLabel(string? type) => type switch
    {
        null or "" => null,
        "manuals" => "Manual",
        "audio" => "Audio",
        "video" => "Video",
        "artworks" => "Artwork",
        "wallpapers" => "Wallpapers",
        "avatars" => "Avatars",
        "game add-ons" => "Add-on",
        "guides & reference" => "Guide",
        _ => Capitalise(type),
    };

    private static string Capitalise(string value) =>
        char.IsLower(value[0]) ? char.ToUpperInvariant(value[0]) + value[1..] : value;
}
