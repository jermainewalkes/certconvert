using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CertConvert.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    CheckFailed,
}

public enum ChecksumResult
{
    /// <summary>The release publishes no SHA256SUMS.txt; integrity rests on TLS.</summary>
    NoChecksumFile,
    /// <summary>The download matches the published checksum.</summary>
    Verified,
    /// <summary>Mismatch, or the checksum file does not list this asset — do not apply.</summary>
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? AssetName = null,
    string? AssetUrl = null,
    string? ChecksumsUrl = null,
    string? Message = null);

public sealed record ApplyResult(
    bool Ok,
    string Message,
    string? RestartPath = null,
    string? FallbackSavedTo = null);

/// <summary>
/// Checks GitHub Releases for a newer version, downloads the platform build and
/// swaps it in place. Lives in the app layer on purpose: CertConvert.Core stays
/// network-free. Network access only ever happens from explicit user action or
/// the opt-in on-launch check.
/// </summary>
public sealed class UpdateService
{
    public const string LatestReleaseApi =
        "https://api.github.com/repos/jermainewalkes/certconvert/releases/latest";
    public const string TagsApi =
        "https://api.github.com/repos/jermainewalkes/certconvert/tags";
    public const string ReleasesPageUrl =
        "https://github.com/jermainewalkes/certconvert/releases";

    private readonly HttpClient _http;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"CertConvert/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Release version of the running build, without the +sha suffix.</summary>
    public static string CurrentVersion { get; } =
        (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>Which release asset suits this machine, e.g. "osx-arm64".</summary>
    internal static string RuntimeRid =>
        OperatingSystem.IsWindows()
            ? "win-x64"
            : RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";

    /// <summary>Pads to three components so 1.0 == 1.0.0 in comparisons.</summary>
    internal static Version? ParseVersion(string text)
    {
        if (!Version.TryParse(text.TrimStart('v', 'V').Trim(), out var v))
            return null;
        return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseApi, ct);
            // GitHub returns 404 from releases/latest when a repo publishes no binary
            // releases. Builds are distributed through the app stores now, so fall
            // back to the tag list: a from-source build can still tell the user a
            // newer version exists and point them at GitHub to rebuild.
            if ((int)response.StatusCode == 404)
                return await CheckViaTagsAsync(ct);
            if (!response.IsSuccessStatusCode)
                return Failed($"GitHub responded with {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string releaseUrl = root.TryGetProperty("html_url", out var hu)
                ? hu.GetString() ?? "" : "";

            var latest = ParseVersion(tag);
            var current = ParseVersion(CurrentVersion);
            if (latest is null || current is null)
                return Failed($"Could not compare versions (tag \"{tag}\").");
            if (latest <= current)
                return new UpdateCheckResult(
                    UpdateStatus.UpToDate, CurrentVersion, latest.ToString(), releaseUrl);

            string? assetName = null, assetUrl = null, checksumsUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    if (name.Contains(RuntimeRid, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        assetUrl = url;
                    }
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        checksumsUrl = url;
                    }
                }
            }

            return new UpdateCheckResult(
                UpdateStatus.UpdateAvailable, CurrentVersion, latest.ToString(),
                releaseUrl, assetName, assetUrl, checksumsUrl,
                assetUrl is null ? SourceOnlyMessage() : null);
        }
        catch (Exception e) when (
            e is HttpRequestException or TaskCanceledException or JsonException
              or KeyNotFoundException or InvalidOperationException)
        {
            return Failed(e is TaskCanceledException
                ? "Timed out contacting GitHub."
                : $"Could not reach GitHub: {e.Message}");
        }
    }

    private static UpdateCheckResult Failed(string message) =>
        new(UpdateStatus.CheckFailed, CurrentVersion, Message: message);

    /// <summary>
    /// Version check for when GitHub hosts no binary releases (the store-only
    /// model): read the tag list, take the highest version and, if newer, tell the
    /// user where to get it. Tags survive release deletion, so this keeps working.
    /// Tags are not returned in semver order, so we ask for the maximum page size
    /// and take the semver-max; correct until the repo exceeds 100 tags.
    /// </summary>
    private async Task<UpdateCheckResult> CheckViaTagsAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(TagsApi + "?per_page=100", ct);
            if (!response.IsSuccessStatusCode)
                return Failed($"GitHub responded with {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var current = ParseVersion(CurrentVersion);
            if (current is null)
                return Failed($"Could not parse the running version \"{CurrentVersion}\".");

            Version? latest = null;
            foreach (var tag in doc.RootElement.EnumerateArray())
            {
                var v = ParseVersion(tag.GetProperty("name").GetString() ?? "");
                if (v is not null && (latest is null || v > latest))
                    latest = v;
            }
            if (latest is null)
                return Failed("No release tags were found on GitHub.");
            if (latest <= current)
                return new UpdateCheckResult(
                    UpdateStatus.UpToDate, CurrentVersion, latest.ToString(), ReleasesPageUrl);

            return new UpdateCheckResult(
                UpdateStatus.UpdateAvailable, CurrentVersion, latest.ToString(),
                ReleasesPageUrl, Message: SourceOnlyMessage());
        }
        catch (Exception e) when (
            e is HttpRequestException or TaskCanceledException or JsonException
              or KeyNotFoundException or InvalidOperationException)
        {
            return Failed(e is TaskCanceledException
                ? "Timed out contacting GitHub."
                : $"Could not reach GitHub: {e.Message}");
        }
    }

    /// <summary>Shown when a newer version exists but no downloadable build does.</summary>
    private static string SourceOnlyMessage() =>
        "Install it from the Mac App Store or Microsoft Store, or build it from " +
        "source on GitHub.";

    /// <summary>
    /// Asset and checksum URLs come from GitHub API JSON — only ever fetch them
    /// over https, never a downgraded/other scheme, before downloading or hashing.
    /// </summary>
    private static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Refusing a non-https update URL: {url}");
    }

    public async Task<string> DownloadAsync(
        string url, string fileName, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        RequireHttps(url);
        string dir = Path.Combine(Path.GetTempPath(), "CertConvert-update");
        Directory.CreateDirectory(dir);
        string destination = Path.Combine(dir, fileName);

        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destination);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
                progress?.Report((double)done / total.Value);
        }
        return destination;
    }

    /// <summary>
    /// Verifies the download against the release's SHA256SUMS.txt.
    /// NoChecksumFile when the release carries no SHA256SUMS.txt (integrity then
    /// rests on TLS alone); Verified/Failed when it does. A checksum file that is
    /// present but does not list this asset is treated as Failed — failing closed,
    /// since our releases always checksum every zip, so a missing entry is anomalous.
    /// </summary>
    public async Task<ChecksumResult> VerifyChecksumAsync(
        string zipPath, string? checksumsUrl, string assetName, CancellationToken ct = default)
    {
        if (checksumsUrl is null)
            return ChecksumResult.NoChecksumFile;
        RequireHttps(checksumsUrl);
        string sums = await _http.GetStringAsync(checksumsUrl, ct);
        // Parse each "<hex-hash>  <filename>" (or "…*<filename>") line and match the
        // filename FIELD exactly — not EndsWith, so "evil-<asset>" can't satisfy it.
        string? expected = sums
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l =>
            {
                int sp = l.IndexOf(' ');
                return sp <= 0
                    ? (Hash: "", File: "")
                    : (Hash: l[..sp], File: l[(sp + 1)..].TrimStart(' ', '*'));
            })
            .Where(e => e.File.Equals(assetName, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Hash)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(expected))
            return ChecksumResult.Failed; // file present but asset unlisted → fail closed

        await using var stream = File.OpenRead(zipPath);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? ChecksumResult.Verified
            : ChecksumResult.Failed;
    }

    // ---------- apply ----------

    public async Task<ApplyResult> ApplyAsync(string zipPath)
    {
        try
        {
            string extractDir = Path.Combine(
                Path.GetTempPath(), "CertConvert-update", "extracted");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));

            return OperatingSystem.IsMacOS()
                ? ApplyMacOs(extractDir, zipPath)
                : OperatingSystem.IsWindows()
                    ? ApplyWindows(extractDir, zipPath)
                    : Fallback(zipPath, "Self-update is not supported on this platform.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Fallback(zipPath, $"Could not apply the update: {e.Message}");
        }
    }

    private ApplyResult ApplyMacOs(string extractDir, string zipPath)
    {
        string? newBundle = LocateMacBundle(extractDir);
        if (newBundle is null)
            return Fallback(zipPath, "The downloaded zip does not contain CertConvert.app.");

        // ZipFile drops unix permissions; restore the executable bit.
        string newBinary = Path.Combine(newBundle, "Contents", "MacOS", "CertConvert");
        if (!File.Exists(newBinary))
            return Fallback(zipPath, "The downloaded app bundle is incomplete.");
        File.SetUnixFileMode(newBinary,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        string? currentBundle = RunningMacBundle();
        if (currentBundle is null)
            return Fallback(zipPath,
                "This copy is not running from a CertConvert.app bundle, so it cannot replace itself.");

        string backup = currentBundle + ".bak";
        if (Directory.Exists(backup))
            Directory.Delete(backup, true);
        Directory.Move(currentBundle, backup);
        try
        {
            Directory.Move(newBundle, currentBundle);
        }
        catch (Exception)
        {
            Directory.Move(backup, currentBundle); // roll back
            throw;
        }
        return new ApplyResult(true,
            "Update applied. Restart to run the new version " +
            "(the previous version is kept beside it as CertConvert.app.bak until the next launch).",
            RestartPath: currentBundle);
    }

    private ApplyResult ApplyWindows(string extractDir, string zipPath)
    {
        string? newExe = LocateWindowsExe(extractDir);
        if (newExe is null)
            return Fallback(zipPath, "The downloaded zip does not contain CertConvert.exe.");

        string? currentExe = Environment.ProcessPath;
        if (currentExe is null || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Fallback(zipPath, "Could not locate the running executable.");

        string old = currentExe + ".old";
        if (File.Exists(old))
            File.Delete(old);
        // Renaming a running executable is allowed on Windows; overwriting is not.
        File.Move(currentExe, old);
        try
        {
            File.Move(newExe, currentExe);
        }
        catch (Exception)
        {
            File.Move(old, currentExe); // roll back
            throw;
        }
        return new ApplyResult(true,
            "Update applied. Restart to run the new version " +
            "(the previous version is kept as CertConvert.exe.old until the next launch).",
            RestartPath: currentExe);
    }

    /// <summary>Couldn't self-update — save the zip somewhere obvious instead.</summary>
    private static ApplyResult Fallback(string zipPath, string reason)
    {
        try
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            string dest = Path.Combine(downloads, Path.GetFileName(zipPath));
            File.Copy(zipPath, dest, overwrite: true);
            return new ApplyResult(false,
                $"{reason} The update has been saved to {dest} — unzip it and replace the app manually.",
                FallbackSavedTo: dest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ApplyResult(false,
                $"{reason} Download the update manually from the releases page.");
        }
    }

    public void RestartNow(string restartPath)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start("open", ["-n", restartPath]);
        else
            Process.Start(new ProcessStartInfo(restartPath) { UseShellExecute = true });
        Environment.Exit(0);
    }

    // ---------- locate helpers (internal for tests) ----------

    internal static string? LocateMacBundle(string extractDir) =>
        Directory
            .EnumerateDirectories(extractDir, "CertConvert.app", SearchOption.AllDirectories)
            .FirstOrDefault();

    internal static string? LocateWindowsExe(string extractDir) =>
        Directory
            .EnumerateFiles(extractDir, "CertConvert.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

    /// <summary>The .app bundle this process runs from, or null for a bare binary.</summary>
    internal static string? RunningMacBundle()
    {
        // AppContext.BaseDirectory ends .../CertConvert.app/Contents/MacOS/
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
        if (dir.Name == "MacOS" &&
            dir.Parent?.Name == "Contents" &&
            dir.Parent.Parent?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true)
            return dir.Parent.Parent.FullName;
        return null;
    }

    /// <summary>Best-effort removal of leftovers from a previous update (called at startup).</summary>
    public static void CleanUpLeftovers()
    {
        try
        {
            if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe)
            {
                string old = exe + ".old";
                if (File.Exists(old))
                    File.Delete(old);
            }
            else if (OperatingSystem.IsMacOS() && RunningMacBundle() is { } bundle)
            {
                string backup = bundle + ".bak";
                if (Directory.Exists(backup))
                    Directory.Delete(backup, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked leftover is harmless; try again next launch.
        }
    }
}
