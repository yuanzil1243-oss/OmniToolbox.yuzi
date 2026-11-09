using System;
using System.Globalization;

namespace OmniToolbox.NativeDragHotbarLocal;

internal readonly record struct NativeIconLookup(uint Id, bool Hq);

internal static class NativeHotbarItems
{
    internal const uint QualityOffset = 1_000_000;
    // The item and icon namespaces independently encode HQ with the same offset.
    // Decode the ICON ID before constructing GameIconLookup; never use an item ID as an icon.
    internal static NativeIconLookup Icon(uint icon) => icon is >= QualityOffset and < QualityOffset * 2
        ? new(icon - QualityOffset, true) : new(icon, false);
    internal static bool TryItem(uint id, out uint baseId, out bool hq)
    {
        hq = id is > QualityOffset and < QualityOffset * 2;
        baseId = hq ? id - QualityOffset : id;
        return baseId is > 0 and < QualityOffset;
    }
    internal static uint SelectIcon(uint item, uint nativeIcon, uint sheetIcon)
    {
        if (!TryItem(item, out _, out var hq)) return nativeIcon;
        if (nativeIcon != 0 && nativeIcon != 234001 && Icon(nativeIcon).Id is > 0 and < QualityOffset) return nativeIcon;
        return sheetIcon is > 0 and < QualityOffset ? sheetIcon + (hq ? QualityOffset : 0) : nativeIcon;
    }
    internal static NativeSlotState State(int percent, int seconds, uint quantity, bool usable = true) =>
        new(Math.Clamp(percent, 0, 100), Math.Max(0, seconds), 0, 1, false, usable && quantity > 0)
        { ItemCount = quantity };
    internal static string CountText(uint count) => "x" + count.ToString(CultureInfo.InvariantCulture);
}

internal readonly record struct NativeHotbarFontStyle(byte Family, byte Size)
{
    internal bool Valid => Family <= 5 && Size is >= 6 and <= 96;
    // Native text-node sizes are points; GameFontStyle/AddText sizes are physical pixels.
    internal int Pixels(float scale = 1) => Math.Clamp((int)MathF.Round(Size * (4f / 3f)
        * (float.IsFinite(scale) && scale > 0 ? scale : 1)), 6, 256);
}

internal readonly record struct NativeHotbarTextStyles(NativeHotbarFontStyle Shortcut,
    NativeHotbarFontStyle Timer, NativeHotbarFontStyle Quantity)
{
    // Read-only client evidence: shortcut MiedingerMed 12; icon text nodes 8/9 Axis 12.
    internal static NativeHotbarTextStyles Default => new(new(1, 12), new(0, 12), new(0, 12));
}
