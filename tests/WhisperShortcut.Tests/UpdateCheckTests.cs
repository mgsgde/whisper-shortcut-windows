using System.Net;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UpdateCheckTests
{
    [Fact]
    public void ReleaseUrls_UseTheRepoSlug()
    {
        Assert.Equal("mgsgde/whisper-shortcut-windows", AppIdentity.GitHubRepo);
        Assert.Contains("/repos/" + AppIdentity.GitHubRepo + "/releases/latest", UpdateCheck.LatestReleaseApi);
        Assert.Contains("/repos/" + AppIdentity.GitHubRepo + "/releases?per_page=30", UpdateCheck.ReleaseListApi);
        Assert.Contains("/" + AppIdentity.GitHubRepo + "/releases/latest", UpdateCheck.ReleasesPage);
    }

    [Fact]
    public void Parse_ReadsTagAndUrl()
    {
        var info = UpdateCheck.Parse("""{"tag_name":"v0.2.0","html_url":"https://github.com/x/y/releases/tag/v0.2.0"}""");
        Assert.Equal("0.2.0", info!.Version);
        Assert.Equal("https://github.com/x/y/releases/tag/v0.2.0", info.Url);
        Assert.Empty(info.Assets);
        Assert.Equal("", info.Notes);
        Assert.Equal("1.0", UpdateCheck.Parse("""{"tag_name":"V1.0"}""")!.Version);
    }

    [Fact]
    public void Parse_RejectsUnsafeHtmlUrlAndHttpAssets()
    {
        var info = UpdateCheck.Parse("""
            {"tag_name":"v1.0.0","html_url":"javascript:alert(1)","assets":[
              {"name":"ok.zip","browser_download_url":"https://github.com/x/ok.zip"},
              {"name":"bad.zip","browser_download_url":"http://evil.example/bad.zip"}]}
            """)!;
        Assert.Equal(UpdateCheck.ReleasesPage, info.Url);
        Assert.True(info.Assets.ContainsKey("ok.zip"));
        Assert.False(info.Assets.ContainsKey("bad.zip"));
    }

    [Fact]
    public void Parse_ReadsNotesAndAssets()
    {
        var info = UpdateCheck.Parse("""
            {"tag_name":"v0.4.0","body":"Neu: Updates","assets":[
              {"name":"a.zip","browser_download_url":"https://x/a.zip"},
              {"name":"a.zip.sig","browser_download_url":"https://x/a.zip.sig"},
              {"name":"broken"},{"browser_download_url":"https://x/y"}]}
            """)!;
        Assert.Equal("Neu: Updates", info.Notes);
        Assert.Equal(2, info.Assets.Count);
        Assert.Equal("https://x/a.zip", info.Assets["a.zip"]);
    }

    [Fact]
    public void AssetName_PicksByArchitecture()
    {
        Assert.Equal("WhisperShortcut-Windows-arm64.zip", UpdateCheck.AssetName(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("WhisperShortcut-Windows-x64.zip", UpdateCheck.AssetName(System.Runtime.InteropServices.Architecture.X64));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":5}""")]
    public void Parse_BadInput_IsNull(string json) => Assert.Null(UpdateCheck.Parse(json));

    [Theory]
    [InlineData("0.2.0", "0.1.1", true)]
    [InlineData("0.1.1", "0.1.1", false)]
    [InlineData("0.1.0", "0.1.1", false)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("v0.2.0", "0.1.1+abc123", true)]
    [InlineData("0.1.1", "0.1.1+abc123", false)]
    [InlineData("0.2.0-beta.1", "0.1.1", true)]
    [InlineData("abc", "0.1.1", false)]
    [InlineData("0.2.0", "", false)]
    [InlineData("0.2.x", "0.1.0", false)]
    public void IsNewer(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(latest, current));

    [Fact]
    public async Task CheckAsync_MapsStatus_AndSendsGithubHeaders()
    {
        var prev = UpdateCheck.ApiUrl;
        var h = new FakeHandler();
        try
        {
            UpdateCheck.ApiUrl = "https://example.test/releases/latest";
            using var http = new HttpClient(h);

            h.Responses.Enqueue((HttpStatusCode.NotFound, ""));
            Assert.Null(await UpdateCheck.CheckAsync(http, UpdateChannel.Stable, CancellationToken.None));

            h.Responses.Enqueue((HttpStatusCode.OK,
                """{"tag_name":"v1.2.3","html_url":"https://github.com/x/y/releases/tag/v1.2.3"}"""));
            var info = await UpdateCheck.CheckAsync(http, UpdateChannel.Stable, CancellationToken.None);
            Assert.Equal("1.2.3", info!.Version);
            Assert.Equal("https://github.com/x/y/releases/tag/v1.2.3", info.Url);

            h.Responses.Enqueue((HttpStatusCode.InternalServerError, "nope"));
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                UpdateCheck.CheckAsync(http, UpdateChannel.Stable, CancellationToken.None));

            var req = h.Calls[0].Request;
            Assert.Equal("https://example.test/releases/latest", req.RequestUri!.ToString());
            Assert.Contains(req.Headers.UserAgent, ua => ua.Product!.Name == "WhisperShortcut-Windows");
            Assert.Contains(req.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
        }
        finally
        {
            UpdateCheck.ApiUrl = prev;
        }
    }

    [Fact]
    public void ParseList_PicksHighestNonDraft_PrereleasesIncluded()
    {
        var info = UpdateCheck.ParseList("""
            [{"tag_name":"v0.9.0","draft":true,"prerelease":false},
             {"tag_name":"v0.8.2","draft":false,"prerelease":true,"html_url":"https://github.com/x/y/releases/tag/v0.8.2"},
             {"tag_name":"v0.8.1","draft":false,"prerelease":false}]
            """);
        Assert.Equal("0.8.2", info!.Version);
        Assert.Equal("https://github.com/x/y/releases/tag/v0.8.2", info.Url);
    }

    [Fact]
    public void ParseList_IgnoresListOrder_AndBadEntries()
    {
        var info = UpdateCheck.ParseList("""[{"tag_name":"v0.10.0"},"x",{"no_tag":1},{"tag_name":"v0.9.5"}]""");
        Assert.Equal("0.10.0", info!.Version);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"tag_name":"v1.0.0","draft":true}]""")]
    [InlineData("{}")]
    [InlineData("kaputt")]
    public void ParseList_NothingUsable_IsNull(string json) => Assert.Null(UpdateCheck.ParseList(json));

    [Theory]
    [InlineData(null, UpdateChannel.Stable)]
    [InlineData("", UpdateChannel.Stable)]
    [InlineData("quatsch", UpdateChannel.Stable)]
    [InlineData("stable", UpdateChannel.Stable)]
    [InlineData("latest", UpdateChannel.Latest)]
    [InlineData(" LATEST ", UpdateChannel.Latest)]
    public void UpdateChannel_UnknownOrMissing_IsStable(string? stored, UpdateChannel expected) =>
        Assert.Equal(expected, Choices.UpdateChannel.Parse(stored));

    [Fact]
    public void UpdateChannel_Setting_DefaultsToStable_AndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ws-channel-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            Assert.Equal(UpdateChannel.Stable, Settings.Load(path, new PlainSecretProtector()).UpdateChannel);
            File.WriteAllText(path, "{\"updateChannel\": \"quatsch\"}");
            Assert.Equal(UpdateChannel.Stable, Settings.Load(path, new PlainSecretProtector()).UpdateChannel);

            var s = Settings.Load(path, new PlainSecretProtector());
            s.UpdateChannel = UpdateChannel.Latest;
            s.Save();
            Assert.Contains("\"updateChannel\": \"latest\"", File.ReadAllText(path));
            Assert.Equal(UpdateChannel.Latest, Settings.Load(path, new PlainSecretProtector()).UpdateChannel);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CheckAsync_Latest_AsksTheList_Stable_AsksReleasesLatest()
    {
        var prevApi = UpdateCheck.ApiUrl;
        var prevList = UpdateCheck.ListUrl;
        var h = new FakeHandler();
        try
        {
            UpdateCheck.ApiUrl = "https://example.test/releases/latest";
            UpdateCheck.ListUrl = "https://example.test/releases?per_page=30";
            using var http = new HttpClient(h);

            h.Responses.Enqueue((HttpStatusCode.OK, """{"tag_name":"v1.0.0"}"""));
            Assert.Equal("1.0.0", (await UpdateCheck.CheckAsync(http, UpdateChannel.Stable, CancellationToken.None))!.Version);

            h.Responses.Enqueue((HttpStatusCode.OK, """[{"tag_name":"v1.1.0"},{"tag_name":"v1.0.0"}]"""));
            Assert.Equal("1.1.0", (await UpdateCheck.CheckAsync(http, UpdateChannel.Latest, CancellationToken.None))!.Version);

            h.Responses.Enqueue((HttpStatusCode.OK, "[]"));
            Assert.Null(await UpdateCheck.CheckAsync(http, UpdateChannel.Latest, CancellationToken.None));

            Assert.Equal("https://example.test/releases/latest", h.Calls[0].Request.RequestUri!.ToString());
            Assert.Equal("https://example.test/releases?per_page=30", h.Calls[1].Request.RequestUri!.ToString());
        }
        finally
        {
            UpdateCheck.ApiUrl = prevApi;
            UpdateCheck.ListUrl = prevList;
        }
    }
}
