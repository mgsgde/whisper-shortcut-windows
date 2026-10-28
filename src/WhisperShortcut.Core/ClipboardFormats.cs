namespace WhisperShortcut.Core;

/// <summary>
/// Which clipboard format ids a snapshot should copy.
/// </summary>
public static class ClipboardFormats
{
    private const uint CfText = 1;
    private const uint CfOemText = 7;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfDibV5 = 17;

    /// <summary>
    /// False for formats whose handle is not a global memory block (CF_BITMAP, CF_METAFILEPICT, CF_PALETTE,
    /// CF_ENHMETAFILE, the owner-display family) and for 0x200-0x3FF (private formats may hold any handle;
    /// GDI objects). CF_DIB/CF_DIBV5 are memory and keep images alive; Windows regenerates CF_BITMAP from them.
    /// </summary>
    public static bool IsMemoryFormat(uint format) => format switch
    {
        2 or 3 or 9 or 14 or 0x80 or 0x82 or 0x83 or 0x8E => false,
        >= 0x200 and <= 0x3FF => false,
        _ => true,
    };

    /// <summary>
    /// Formats worth copying, in the original order. Windows synthesizes CF_DIB from CF_DIBV5 and
    /// CF_TEXT/CF_OEMTEXT from CF_UNICODETEXT, so copying both doubles the size; Windows regenerates
    /// the dropped formats on restore. CF_LOCALE (16) is kept so the conversion back is correct.
    /// </summary>
    public static IReadOnlyList<uint> ToSnapshot(IReadOnlyList<uint> available)
    {
        bool hasDibV5 = false;
        bool hasUnicode = false;
        foreach (uint format in available)
        {
            if (format == CfDibV5) hasDibV5 = true;
            else if (format == CfUnicodeText) hasUnicode = true;
        }

        var kept = new List<uint>(available.Count);
        var seen = new HashSet<uint>();
        foreach (uint format in available)
        {
            if (!IsMemoryFormat(format)) continue;
            if (hasDibV5 && format == CfDib) continue;
            if (hasUnicode && (format == CfText || format == CfOemText)) continue;
            if (!seen.Add(format)) continue;
            kept.Add(format);
        }
        return kept;
    }
}
