using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

// Mac 5e041f0a (#383): VS Code-family editors copy the whole line for Ctrl+C without a selection.
public class EditorEmptySelectionTests
{
    private const string Json = "{\"version\":1,\"isFromEmptySelection\":true,\"multicursorText\":null,\"mode\":\"plaintext\"}";

    [Fact]
    public void MarkerInVscodeEditorDataIsDetected()
    {
        Assert.True(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(null, Json));
        Assert.True(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(null, "{\"isFromEmptySelection\" : true}"));
    }

    [Fact]
    public void FalseValueIsNotAMarker()
    {
        Assert.False(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(null, "{\"version\":1,\"isFromEmptySelection\":false}"));
        Assert.False(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(Encoding.Unicode.GetBytes(Json.Replace("true", "false")), null));
    }

    [Fact]
    public void MarkerInUtf16LeCustomDataIsDetected()
        => Assert.True(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(Encoding.Unicode.GetBytes(Json), null));

    [Fact]
    public void MarkerInUtf8CustomDataIsDetected()
        => Assert.True(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(Encoding.UTF8.GetBytes(Json), null));

    [Fact]
    public void NullInputsAreNoMarker()
        => Assert.False(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(null, null));

    [Fact]
    public void UnrelatedDataIsNoMarker()
        => Assert.False(SelectionCopyPolicy.IsEditorEmptySelectionLineCopy(new byte[] { 0x01, 0x02, 0x03, 0xff }, "hello"));
}
