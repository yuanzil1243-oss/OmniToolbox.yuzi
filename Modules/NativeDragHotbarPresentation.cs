using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OmniToolbox.NativeDragHotbarLocal;

internal readonly record struct MacroIconDirective(string Name, string Category);
internal readonly record struct HotbarSlotTints(uint Surface, uint Image, uint Frame);

internal static class HotbarPresentation
{
    public static bool TryIconDirective(string text, out MacroIconDirective directive)
    {
        directive = default;
        foreach (var line in HotbarRules.Commands(text))
        {
            if (!TryTokens(line, out var tokens) || tokens.Count == 0) continue;
            if (!IsIconCommand(tokens[0])) continue;
            if (tokens.Count is < 2 or > 3 || string.IsNullOrWhiteSpace(tokens[1])) return false;
            directive = new(tokens[1], tokens.Count == 3 ? tokens[2].ToLowerInvariant() : "action");
            return true;
        }
        return false;
    }

    public static bool IsIconCommand(string command) => command.ToLowerInvariant()
        is "/micon" or "/macroicon" or "/宏图标" or "/宏圖標";

    public static bool IsIconLine(string line)
    {
        var end = line.IndexOfAny([' ', '\t', '\r', '\n']);
        return IsIconCommand(end < 0 ? line : line[..end]);
    }

    private static bool TryTokens(string text, out List<string> tokens)
    {
        tokens = [];
        for (var i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length) break;
            var quote = text[i] is '"' or '\'' or '“' ? text[i++] : '\0';
            var close = quote == '“' ? '”' : quote;
            var start = i;
            if (quote != '\0')
            {
                while (i < text.Length && text[i] != close) i++;
                if (i == text.Length) return false;
                tokens.Add(text[start..i++]);
                if (i < text.Length && !char.IsWhiteSpace(text[i])) return false;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                tokens.Add(text[start..i]);
            }
        }
        return true;
    }

    public static uint SelectIcon(HotbarEntry entry, uint commandIcon, uint nativeIcon)
    {
        if (entry.Mode == HotbarEntryMode.Command && commandIcon > 0) return commandIcon;
        if (entry.IconOverride && entry.IconId > 0) return entry.IconId;
        return nativeIcon > 0 ? nativeIcon : entry.IconId;
    }

    public static bool EnsureFallbackIcon(HotbarEntry entry, uint resolvedIcon, Func<int, int, int> random)
    {
        if (entry.Mode != HotbarEntryMode.Command || string.IsNullOrWhiteSpace(entry.Command)
            || resolvedIcon > 0 || entry.IconId > 0) return false;
        entry.IconId = (uint)random(234001, 234064);
        entry.IconOverride = false;
        return true;
    }

    public static uint WithOpacity(uint color, float opacity)
    {
        var alpha = (uint)MathF.Round((color >> 24) * Math.Clamp(opacity, 0, 1));
        return (color & 0x00FFFFFF) | (alpha << 24);
    }

    public static HotbarSlotTints SlotTints(float barOpacity, float themeAlpha, uint border)
    {
        var surfaceAlpha = Math.Clamp(barOpacity, 0, 1) * Math.Clamp(themeAlpha, 0, 1);
        return new(WithOpacity(0xCC161616, surfaceAlpha), WithOpacity(0xFFFFFFFF, themeAlpha),
            WithOpacity(border, surfaceAlpha));
    }

    public static uint EditorShade(bool awaitingReplacement) => awaitingReplacement ? 0x2E000000u : 0u;

    public static Vector2 ReplacementCenter(bool editorVisible, Vector2 editorMin, Vector2 editorMax,
        Vector2 viewportPosition, Vector2 viewportSize) => editorVisible
            ? (editorMin + editorMax) * 0.5f : viewportPosition + viewportSize * 0.5f;
}

internal enum HotbarVisibilityAction { Toggle, Show, Hide }

internal static class HotbarVisibilityCommands
{
    public static bool TryParse(string arguments, out HotbarVisibilityAction action, out string selector)
    {
        var parts = arguments.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        selector = parts.Length > 1 ? parts[1].Trim() : "";
        if (selector.Length >= 2 && selector[0] == '"' && selector[^1] == '"') selector = selector[1..^1];
        var verb = parts.Length == 0 ? "toggle" : parts[0].ToLowerInvariant();
        // The displayed 1.0.0 command promised a visibility toggle. Preserve that spelling as an alias.
        action = verb switch { "on" => HotbarVisibilityAction.Show, "hide" or "off" => HotbarVisibilityAction.Hide,
            _ => HotbarVisibilityAction.Toggle };
        return verb is "show" or "toggle" or "on" or "off" or "hide";
    }

    public static bool Apply(bool visible, HotbarVisibilityAction action) => action switch
    { HotbarVisibilityAction.Show => true, HotbarVisibilityAction.Hide => false, _ => !visible };

    public static bool Apply(HotbarProfile profile, HotbarVisibilityAction action, string selector, out int index)
    {
        index = -1;
        if (selector.Length > 0)
        {
            if (!HotbarRules.TryResolveBarSelector(profile.Bars, selector, out index)) return false;
            var bar = profile.Bars[index];
            bar.Visible = Apply(profile.Show && bar.Visible, action);
            if (bar.Visible) profile.Show = true;
        }
        else
        {
            profile.Show = Apply(profile.Show && profile.Bars.Any(bar => bar.Visible), action);
            if (profile.Show && !profile.Bars.Any(bar => bar.Visible))
                foreach (var bar in profile.Bars) bar.Visible = true;
        }
        return true;
    }
}
