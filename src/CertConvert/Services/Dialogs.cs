using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace CertConvert.Services;

/// <summary>File pickers, clipboard and browser launching for the view models.</summary>
public static class Dialogs
{
    private static readonly FilePickerFileType CertTypes = new("Certificates And Keys")
    {
        Patterns =
        [
            "*.pem", "*.crt", "*.cer", "*.der", "*.p7b", "*.p7c",
            "*.pfx", "*.p12", "*.key", "*.csr",
        ],
    };

    private static TopLevel? Top =>
        Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    public static async Task<IReadOnlyList<string>> OpenFilesAsync(
        string title, bool allowMultiple = true)
    {
        if (Top?.StorageProvider is not { } storage) return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = [CertTypes, FilePickerFileTypes.All],
        });
        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
    }

    public static async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        if (Top?.StorageProvider is not { } storage) return null;
        // Derive the default extension from the suggested name so the platform
        // dialog prefills and appends it when the user doesn't type one.
        var ext = System.IO.Path.GetExtension(suggestedName).TrimStart('.');
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = ext.Length > 0 ? ext : null,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public static async Task CopyTextAsync(string text)
    {
        if (Top?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public static async Task OpenUrlAsync(string url)
    {
        if (Top is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(url));
    }
}
