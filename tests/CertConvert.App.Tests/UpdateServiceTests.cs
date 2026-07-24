using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CertConvert.Services;

namespace CertConvert.Gui.Tests;

/// <summary>Serves canned HTTP responses so the updater is tested without a network.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(responder(request));
}

public class UpdateServiceTests
{
    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string ReleaseJson(string tag, params string[] assetNames)
    {
        var assets = string.Join(",", Array.ConvertAll(assetNames, n =>
            $$"""{"name":"{{n}}","browser_download_url":"https://example.test/{{n}}"}"""));
        return $$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/x/y/releases/tag/{{tag}}",
             "assets":[{{assets}}]}
            """;
    }

    [Fact]
    public async Task NewerTag_ReportsUpdateAvailable_WithMatchingAsset()
    {
        var svc = new UpdateService(new FakeHandler(_ => Json(ReleaseJson("v9.9.9",
            "CertConvert-9.9.9-osx-x64.zip",
            "CertConvert-9.9.9-osx-arm64.zip",
            "CertConvert-9.9.9-win-x64.zip",
            "SHA256SUMS.txt"))));

        var result = await svc.CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("9.9.9", result.LatestVersion);
        Assert.NotNull(result.AssetUrl);
        Assert.Contains(UpdateService.RuntimeRid, result.AssetName!);
        Assert.NotNull(result.ChecksumsUrl); // SHA256SUMS.txt was present
    }

    [Fact]
    public async Task EqualVersion_ReportsUpToDate()
    {
        var svc = new UpdateService(new FakeHandler(_ =>
            Json(ReleaseJson("v" + UpdateService.CurrentVersion))));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task OlderTag_ReportsUpToDate()
    {
        var svc = new UpdateService(new FakeHandler(_ => Json(ReleaseJson("v0.0.1"))));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task MalformedJson_ReportsCheckFailed()
    {
        var svc = new UpdateService(new FakeHandler(_ => Json("{ not json")));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.CheckFailed, result.Status);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task NetworkError_ReportsCheckFailed()
    {
        var svc = new UpdateService(new FakeHandler(_ => throw new HttpRequestException("offline")));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.CheckFailed, result.Status);
    }

    [Fact]
    public async Task Non200_ReportsCheckFailed()
    {
        var svc = new UpdateService(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("rate limited") }));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.CheckFailed, result.Status);
    }

    [Fact]
    public async Task UpdateWithNoPlatformAsset_IsAvailableButNotInstallable()
    {
        var svc = new UpdateService(new FakeHandler(_ =>
            Json(ReleaseJson("v9.9.9", "some-unrelated-file.txt"))));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Null(result.AssetUrl);
        Assert.NotNull(result.Message);
    }

    // ---------- store-only fallback: no binary releases, check tags instead ----------

    /// <summary>404 from releases/latest, a tag list from the tags endpoint.</summary>
    private static FakeHandler NoReleasesButTags(string tagsJson) => new(req =>
        req.RequestUri!.AbsoluteUri == UpdateService.LatestReleaseApi
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") }
            : Json(tagsJson));

    [Fact]
    public async Task NoReleases_FallsBackToTags_ReportsSourceOnlyUpdate()
    {
        var svc = new UpdateService(NoReleasesButTags(
            """[{"name":"v9.9.9"},{"name":"v0.0.1"}]"""));

        var result = await svc.CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("9.9.9", result.LatestVersion);
        Assert.Null(result.AssetUrl); // nothing to download — store or source only
        Assert.Equal(UpdateService.ReleasesPageUrl, result.ReleaseUrl);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task NoReleases_TagsNotNewer_ReportsUpToDate()
    {
        var svc = new UpdateService(NoReleasesButTags(
            $$"""[{"name":"v{{UpdateService.CurrentVersion}}"},{"name":"v0.0.1"}]"""));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task NoReleases_NoTags_ReportsCheckFailed()
    {
        var svc = new UpdateService(NoReleasesButTags("[]"));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.CheckFailed, result.Status);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task NoReleases_TagsEndpointRateLimited_ReportsCheckFailed()
    {
        var svc = new UpdateService(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri == UpdateService.LatestReleaseApi
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") }
                : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("rate limited") }));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.CheckFailed, result.Status);
    }

    [Fact]
    public async Task NoReleases_MixedTags_HighestParseableStableWins()
    {
        // Prereleases and non-version tags are skipped (Version.TryParse rejects
        // them); the semver-max is taken across the list, not the first entry.
        var svc = new UpdateService(NoReleasesButTags(
            """[{"name":"v2.0.0-beta"},{"name":"docs-freeze"},{"name":"v1.5.0"},{"name":"v9.9.9"},{"name":"v3.0.0"}]"""));
        var result = await svc.CheckAsync();
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("9.9.9", result.LatestVersion);
    }

    [Theory]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2.3.4", "2.3.4")]
    public void ParseVersion_NormalisesTags(string tag, string expected)
    {
        Assert.Equal(expected, UpdateService.ParseVersion(tag)!.ToString());
    }

    [Fact]
    public void ParseVersion_RejectsGarbage() =>
        Assert.Null(UpdateService.ParseVersion("not-a-version"));

    // ---------- checksum verification ----------

    private static string WriteTempZip(byte[] content, out string sha256Hex)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
        File.WriteAllBytes(path, content);
        sha256Hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        return path;
    }

    [Fact]
    public async Task Checksum_NoFile_ReturnsNoChecksumFile()
    {
        var svc = new UpdateService(new FakeHandler(_ => Json("")));
        string zip = WriteTempZip([1, 2, 3], out _);
        try
        {
            var result = await svc.VerifyChecksumAsync(zip, null, "x.zip");
            Assert.Equal(ChecksumResult.NoChecksumFile, result);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task Checksum_MatchingEntry_ReturnsVerified()
    {
        string zip = WriteTempZip([9, 8, 7, 6], out string hex);
        // shasum -a 256 format: "<hex>  <filename>" (two spaces).
        var svc = new UpdateService(new FakeHandler(_ => Json($"{hex}  build.zip\n")));
        try
        {
            var result = await svc.VerifyChecksumAsync(zip, "https://x/SHA256SUMS.txt", "build.zip");
            Assert.Equal(ChecksumResult.Verified, result);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task Checksum_Mismatch_ReturnsFailed()
    {
        string zip = WriteTempZip([1, 1, 1], out _);
        var svc = new UpdateService(new FakeHandler(_ =>
            Json("0000000000000000000000000000000000000000000000000000000000000000  build.zip\n")));
        try
        {
            var result = await svc.VerifyChecksumAsync(zip, "https://x/SHA256SUMS.txt", "build.zip");
            Assert.Equal(ChecksumResult.Failed, result);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task Checksum_FilePresentButAssetUnlisted_FailsClosed()
    {
        string zip = WriteTempZip([4, 2], out string hex);
        // Sums file lists a different asset only — ours is absent.
        var svc = new UpdateService(new FakeHandler(_ => Json($"{hex}  other.zip\n")));
        try
        {
            var result = await svc.VerifyChecksumAsync(zip, "https://x/SHA256SUMS.txt", "build.zip");
            Assert.Equal(ChecksumResult.Failed, result); // NOT NoChecksumFile
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task Checksum_SupersetFilename_DoesNotMatch()
    {
        // The sums file lists a filename that merely ENDS WITH ours, with our real
        // hash. Exact filename-field matching must reject it (not the old EndsWith).
        string zip = WriteTempZip([5, 5, 5], out string hex);
        var svc = new UpdateService(new FakeHandler(_ => Json($"{hex}  evil-build.zip\n")));
        try
        {
            var result = await svc.VerifyChecksumAsync(zip, "https://x/SHA256SUMS.txt", "build.zip");
            Assert.Equal(ChecksumResult.Failed, result);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task Checksum_NonHttpsUrl_IsRejected()
    {
        string zip = WriteTempZip([1], out _);
        var svc = new UpdateService(new FakeHandler(_ => Json("")));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.VerifyChecksumAsync(zip, "http://insecure/SHA256SUMS.txt", "build.zip"));
        }
        finally { File.Delete(zip); }
    }
}
