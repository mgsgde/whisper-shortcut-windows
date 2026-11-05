using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class InstallLocationTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wsw-il-root"));
    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static InstallPlace Classify(string exe) =>
        InstallLocation.Classify(exe, P("Temp"), P("Downloads"), P("Desktop"), InstallLocation.CanonicalDir(P("Local")));

    [Fact]
    public void CanonicalFolder() =>
        Assert.Equal(InstallPlace.Canonical, Classify(Path.Combine(InstallLocation.CanonicalDir(P("Local")), "WhisperShortcut.exe")));

    [Fact]
    public void ExeInsideAZipOpenedFromExplorerIsTemporary() =>
        Assert.Equal(InstallPlace.Temporary, Classify(P("Temp", "Temp1_WhisperShortcut.zip", "WhisperShortcut.exe")));

    [Fact]
    public void DownloadsAndSubfolders()
    {
        Assert.Equal(InstallPlace.Downloads, Classify(P("Downloads", "WhisperShortcut-Windows-x64.exe")));
        Assert.Equal(InstallPlace.Downloads, Classify(P("Downloads", "sub", "WhisperShortcut.exe")));
    }

    [Fact]
    public void Desktop() => Assert.Equal(InstallPlace.Desktop, Classify(P("Desktop", "WhisperShortcut.exe")));

    [Fact]
    public void OtherAndSiblingPrefixIsNotInside()
    {
        Assert.Equal(InstallPlace.Other, Classify(P("Tools", "WhisperShortcut.exe")));
        Assert.Equal(InstallPlace.Other, Classify(P("Downloads2", "WhisperShortcut.exe")));
        Assert.Equal(InstallPlace.Other, Classify(P("Local", "Programs", "WhisperShortcut-old", "WhisperShortcut.exe")));
    }

    [Fact]
    public void CaseInsensitiveAndMissingFolders()
    {
        Assert.Equal(InstallPlace.Downloads, Classify(P("DOWNLOADS", "x.exe")));
        Assert.Equal(InstallPlace.Other, InstallLocation.Classify(P("Downloads", "x.exe"), null, null, "", null));
        Assert.Equal(InstallPlace.Other, InstallLocation.Classify("", null, null, null, null));
    }

    [Theory]
    [InlineData(InstallPlace.Canonical, false, false, true)]
    [InlineData(InstallPlace.Downloads, false, false, true)]
    [InlineData(InstallPlace.Desktop, false, false, true)]
    [InlineData(InstallPlace.Other, false, false, true)]
    [InlineData(InstallPlace.Temporary, false, false, false)]
    [InlineData(InstallPlace.Canonical, true, false, false)]
    [InlineData(InstallPlace.Canonical, false, true, false)]
    public void MayRegister(InstallPlace place, bool debug, bool test, bool expected) =>
        Assert.Equal(expected, InstallLocation.MayRegister(place, debug, test));

    [Fact]
    public void MoveIsOfferedOnlyOutsideAPermanentPlace()
    {
        Assert.True(InstallLocation.ShouldOfferMove(InstallPlace.Temporary));
        Assert.True(InstallLocation.ShouldOfferMove(InstallPlace.Downloads));
        Assert.True(InstallLocation.ShouldOfferMove(InstallPlace.Desktop));
        Assert.False(InstallLocation.ShouldOfferMove(InstallPlace.Canonical));
        Assert.False(InstallLocation.ShouldOfferMove(InstallPlace.Other));
    }
}
