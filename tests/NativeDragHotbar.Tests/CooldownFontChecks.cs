using System.Reflection;
using OmniToolbox.NativeDragHotbarLocal;

internal static class CooldownFontChecks
{
    internal static void Compiled(Assembly assembly, Assembly structs, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        const string ns = "OmniToolbox.NativeDragHotbarLocal.";
        var style = NativeHotbarTextStyles.TakeoverTimer;
        check(style == new NativeHotbarFontStyle(3, 24) && style.Pixels() == 32,
            "takeover cooldown uses the built-in Trump Gothic font at 32 physical pixels per 48-pixel slot");
        foreach (var (scale, pixels) in new[] { (0.75f, 24), (1f, 32), (1.25f, 40), (1.5f, 48), (2f, 64) })
            check(style.Pixels(scale) == pixels, $"takeover timer scales with its bar: {scale} => {pixels}px");
        check(NativeHotbarTextStyles.Default.Timer == new NativeHotbarFontStyle(0, 12),
            "extension default timer remains Axis 12; takeover styling is separate");
        var styles = assembly.GetType(ns + "NativeHotbarTextStyles", true)!;
        var compiledStyle = styles.GetProperty("TakeoverTimer", flags)!.GetValue(null)!;
        check((byte)compiledStyle.GetType().GetProperty("Family")!.GetValue(compiledStyle)! == 3
            && (byte)compiledStyle.GetType().GetProperty("Size")!.GetValue(compiledStyle)! == 24,
            "compiled module contains the independent game cooldown font rather than native-node fallback sizing");
        var reader = assembly.GetType(ns + "NativeHotbarFontReader", true)!;
        check(reader.GetMethod("ReadTimer", flags) is null,
            "takeover no longer attempts to follow another module's reused FrameIcon text node");
        var visual = assembly.GetType(ns + "NativeHotbarIconVisual", true)!;
        var draw = visual.GetMethod("DrawStyled", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bounds = visual.GetMethod("TakeoverBounds", flags)!;
        check(ItemAndFontChecks.Calls(draw).Contains("get_TakeoverTimer")
            && ItemAndFontChecks.Calls(bounds).Contains("get_TakeoverTimer"),
            "both takeover drawing and native-window clipping use the same independent timer style");
        var styledText = visual.GetMethod("StyledText", flags)!;
        check(ItemAndFontChecks.Calls(styledText).Contains("TimerInk")
            && ItemAndFontChecks.Calls(bounds).Contains("TimerInk"),
            "visible glyph bounds drive both numeric centering and clipping");
        var ink = visual.GetMethod("TimerInk", flags)!;
        check(ItemAndFontChecks.Calls(ink).Contains("FindGlyph")
            && ItemAndFontChecks.Calls(ink).Contains("GetDistanceAdjustmentForPair"),
            "timer centering uses actual game glyph bearings, height and pair adjustments");
        var module = assembly.GetType(ns + "NativeDragHotbar", true)!;
        var overlay = module.GetMethod("DrawSuperSlotOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var calls = ItemAndFontChecks.Calls(overlay).ToArray();
        check(calls.Contains("TakeoverBounds") && calls.Contains("DrawBelowNative"),
            "takeover timer remains below native windows with measured clipping bounds");
    }
}
