using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChatApp.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shared.Models;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// One attachment on a rendered message. Images load their bitmap asynchronously with
/// the auth header; everything else shows as a file chip that opens in the browser.
/// </summary>
public partial class AttachmentItemViewModel : ObservableObject
{
    private readonly ApiClientService _api;

    public AttachmentInfo Attachment { get; }

    public string FileName => Attachment.FileName;

    public bool IsImage => Attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public string SizeLabel => Attachment.SizeBytes switch
    {
        <= 0 => string.Empty,
        < 1024 => $"{Attachment.SizeBytes} B",
        < 1024 * 1024 => $"{Attachment.SizeBytes / 1024.0:0.#} KB",
        _ => $"{Attachment.SizeBytes / (1024.0 * 1024.0):0.#} MB"
    };

    /// <summary>Loaded lazily; null shows the placeholder chip instead.</summary>
    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    public AttachmentItemViewModel(AttachmentInfo attachment, ApiClientService api)
    {
        Attachment = attachment;
        _api = api;

        if (IsImage)
            _ = LoadImageAsync();
    }

    private async Task LoadImageAsync()
    {
        var bytes = await _api.DownloadBytesAsync(Attachment.Url);
        if (bytes is null || bytes.Length == 0)
            return;

        try
        {
            // Decoded off the UI thread; only the property assignment marshals over.
            var bitmap = new Bitmap(new MemoryStream(bytes));
            await Dispatcher.UIThread.InvokeAsync(() => Image = bitmap);
        }
        catch
        {
            // Not decodable as an image after all — the file chip stays.
        }
    }

    /// <summary>Hands the URL to the OS: browser for pages, download for files.</summary>
    [RelayCommand]
    private void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Attachment.Url) { UseShellExecute = true });
        }
        catch
        {
            // No handler registered; nothing sensible to do.
        }
    }
}

/// <summary>An already-uploaded file waiting in the composer.</summary>
public sealed record PendingAttachment(AttachmentInfo Attachment)
{
    public string FileName => Attachment.FileName;
}

/// <summary>
/// A link-preview card under a message: the first http(s) URL's OpenGraph metadata,
/// fetched through the server's preview endpoint (which also special-cases YouTube).
/// </summary>
public partial class LinkPreviewCardViewModel : ObservableObject
{
    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        @"https?://\S+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public string Url { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial string? SiteName { get; set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    /// <summary>
    /// Builds a card for the first URL in <paramref name="content"/>, or null when there
    /// is none. The fetch happens in the background; the card stays hidden until it
    /// lands, so failed previews cost nothing visually.
    /// </summary>
    public static LinkPreviewCardViewModel? TryCreate(string content, ApiClientService api)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var match = UrlPattern.Match(content);
        if (!match.Success)
            return null;

        var card = new LinkPreviewCardViewModel { Url = match.Value.TrimEnd('.', ',', ')', ']') };
        _ = card.LoadAsync(api);
        return card;
    }

    private async Task LoadAsync(ApiClientService api)
    {
        try
        {
            var preview = await api.GetLinkPreviewAsync(Url);
            if (preview is null || (string.IsNullOrWhiteSpace(preview.Title) && string.IsNullOrWhiteSpace(preview.Description)))
                return;

            byte[]? imageBytes = null;
            if (!string.IsNullOrWhiteSpace(preview.ImageUrl))
                imageBytes = await api.DownloadBytesAsync(preview.ImageUrl);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Title = preview.Title;
                Description = preview.Description;
                SiteName = preview.SiteName;

                if (imageBytes is { Length: > 0 })
                {
                    try
                    {
                        Image = new Bitmap(new MemoryStream(imageBytes));
                    }
                    catch
                    {
                        // Metadata without a thumbnail is still a useful card.
                    }
                }

                HasPreview = true;
            });
        }
        catch
        {
            // No preview is a fine outcome; the message already shows the raw URL.
        }
    }

    [RelayCommand]
    private void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        }
        catch
        {
            // No handler registered; nothing sensible to do.
        }
    }
}

/// <summary>
/// File picking + upload shared by the room and DM composers.
/// </summary>
public static class AttachmentUpload
{
    /// <summary>Opens the platform picker and uploads every chosen file.</summary>
    public static async Task<List<PendingAttachment>> PickAndUploadAsync(ApiClientService api)
    {
        var top = ResolveTopLevel();
        if (top is null)
            return [];

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true
        });

        var uploaded = new List<PendingAttachment>();

        foreach (var file in files)
        {
            var properties = await file.GetBasicPropertiesAsync();
            var size = (long)(properties.Size ?? 0);

            await using var stream = await file.OpenReadAsync();

            var info = await api.UploadFileAsync(
                stream, file.Name, GuessContentType(file.Name), size);

            uploaded.Add(new PendingAttachment(info));
        }

        return uploaded;
    }

    private static TopLevel? ResolveTopLevel() => Application.Current?.ApplicationLifetime switch
    {
        IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
        ISingleViewApplicationLifetime single when single.MainView is { } view => TopLevel.GetTopLevel(view),
        _ => null
    };

    /// <summary>
    /// Extension-based sniffing. The server stores whatever we claim, and the only
    /// consumer decision hanging on it is inline-image rendering.
    /// </summary>
    private static string GuessContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
}
