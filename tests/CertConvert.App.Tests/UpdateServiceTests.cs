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
}
