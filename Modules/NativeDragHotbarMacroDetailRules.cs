using System;
using System.Collections.Generic;

namespace OmniToolbox.NativeDragHotbarLocal;

internal readonly record struct MacroDetailTarget(byte Type, uint Id, string Name);

internal static class NativeHotbarMacroDetailRules
{
    internal static bool TryMacroAddress(uint id, out uint set, out uint index)
    {
        set = id >> 8; index = id & 255;
        return HotbarRules.IsValidId(7, id);
    }

    internal static bool TryResolve(string text, Func<MacroIconDirective, MacroDetailTarget?> find,
        out MacroDetailTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        // An explicit icon directive identifies a sheet entry, unlike a manually selected icon.
        if (HotbarPresentation.TryIconDirective(text, out var icon) && Resolve(icon, find, out target)) return true;
        foreach (var line in HotbarRules.Commands(text))
        {
            if (!HotbarPresentation.TryTokens(line, out var tokens) || tokens.Count < 2) continue;
            var category = tokens[0].ToLowerInvariant() switch
            {
                "/ac" or "/action" or "/技能" => "action",
                "/pvpac" or "/pvpaction" or "/对战技能" or "/對戰技能" => "pvpaction",
                "/blueaction" or "/青魔法" or "/青魔法技能" => "blueaction",
                "/gaction" or "/generalaction" or "/通用技能" or "/共通技能" => "general",
                "/item" or "/useitem" or "/道具" or "/使用物品" or "/使用道具" => "item",
                "/mount" or "/坐骑" or "/坐騎" => "mount",
                "/minion" or "/companion" or "/宠物" or "/寵物" => "companion",
                _ => "",
            };
            // Targets and <wait.N> are not part of a name. Unquoted multiword names are
            // deliberately not guessed; quoted names use the same tokenizer as /micon.
            if (category.Length == 0 || tokens[1].StartsWith('<')
                || tokens[1].Contains('\u0002') || tokens[1].Contains('\u0003')) continue;
            if (tokens.Count > 2 && !tokens[2].StartsWith('<')) continue;
            if (Resolve(new(tokens[1], category), find, out target)) return true;
        }
        return false;
    }

    private static bool Resolve(MacroIconDirective spec, Func<MacroIconDirective, MacroDetailTarget?> find,
        out MacroDetailTarget target)
    {
        target = default;
        var category = Category(spec.Category);
        if (category.Length == 0 || string.IsNullOrWhiteSpace(spec.Name)
            || spec.Name.Contains('\u0002') || spec.Name.Contains('\u0003')) return false;
        var found = find(new(spec.Name, category));
        if (found is not { } value || !NativeHotbarDetail.TryCreate(value.Type, value.Id, out _)) return false;
        target = value; return true;
    }

    internal static string Category(string category) => category.ToLowerInvariant() switch
    {
        "action" or "技能" => "action",
        "pvpaction" or "对战技能" or "對戰技能" => "pvpaction",
        "blueaction" or "青魔法" or "青魔法技能" => "blueaction",
        "general" or "generalaction" or "通用" or "通用技能" or "共通技能" => "general",
        "item" or "道具" or "物品" => "item",
        "mount" or "坐骑" or "坐騎" => "mount",
        "minion" or "companion" or "宠物" or "寵物" => "companion",
        _ => "",
    };

    internal static string ItemName(string name, out bool hq)
    {
        hq = name.EndsWith('\uE03C');
        return hq ? name[..^1].TrimEnd() : name;
    }

    internal static SuperMacro? SavedMacro(IReadOnlyList<SuperMacro> macros, uint id)
    {
        if (!TryMacroAddress(id, out var set, out var index)) return null;
        foreach (var macro in macros)
            if (macro.Set == set && macro.Index == index) return macro;
        return null;
    }
}
