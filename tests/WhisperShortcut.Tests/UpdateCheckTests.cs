using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UpdateCheckTests
{
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
}
