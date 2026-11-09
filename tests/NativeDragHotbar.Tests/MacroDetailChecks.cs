using System.Reflection;
using OmniToolbox.NativeDragHotbarLocal;

internal static class MacroDetailChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var action = new MacroDetailTarget(1, 119, "技能 A");
        var item = new MacroDetailTarget(2, 4566, "药品");
        var craft = new MacroDetailTarget(9, 100001, "制作");
        var targets = new Dictionary<MacroIconDirective, MacroDetailTarget>
        {
            [new("技能 A", "action")] = action, [new("技能B", "action")] = new(1, 120, "技能B"),
            [new("药品", "item")] = item, [new("药品\uE03C", "item")] = new(2, 1004566, "药品"),
            [new("制作", "action")] = craft, [new("冲刺", "general")] = new(10, 4, "冲刺"),
            [new("坐骑", "mount")] = new(17, 1, "坐骑"), [new("宠物", "companion")] = new(13, 1, "宠物"),
            [new("任务道具", "item")] = new(4, 2000010, "任务道具"),
            [new("对战", "pvpaction")] = new(1, 29000, "对战"), [new("青魔法", "blueaction")] = new(1, 11385, "青魔法"),
        };
        MacroDetailTarget? Find(MacroIconDirective spec) => targets.TryGetValue(spec, out var value) ? value : null;
        void Expect(string text, MacroDetailTarget expected, string message) =>
            check(NativeHotbarMacroDetailRules.TryResolve(text, Find, out var actual) && actual == expected, message);
        foreach (var alias in new[] { "/ac", "/action", "/技能", "／AC" })
            Expect(alias + " \"技能 A\" <mo> <wait.1>", action, "skill macro alias and target parsed: " + alias);
        foreach (var alias in new[] { "/item", "/useitem", "/道具", "/使用物品", "/使用道具" })
            Expect(alias + " 药品 <me>", item, "item macro alias parsed: " + alias);
        foreach (var alias in new[] { "/micon", "/macroicon", "/宏图标", "/宏圖標" })
            Expect(alias + " \"技能 A\"\n/item 药品", action, "explicit supported icon target takes priority: " + alias);
        Expect("/item 药品\n/micon 技能B action", targets[new("技能B", "action")], "icon directive after execution line still selects its named target");
        Expect("/micon 药品 物品\n/ac \"技能 A\"", item, "localized icon category identifies item rather than skill");
        Expect("/echo text\n/micon 惊讶 emote\n/ac \"技能 A\" <t>", action, "unsupported decorative icon falls back to actual skill command");
        Expect("/micon 不存在 action\n/item 药品", item, "unresolved icon falls back to valid body item");
        Expect("/micon \"unfinished\n/ac \"技能 A\"", action, "malformed icon does not block an otherwise resolvable body");
        Expect("/ac 不存在\n/item 药品\n/ac 技能B", item, "first resolvable body entry wins in a multi-action macro");
        Expect("/echo /ac \"技能 A\"\n/item 药品", item, "chat text is never parsed as an embedded command");
        Expect("/ac 制作 <wait.3>", craft, "crafting macros use CraftAction identity");
        Expect("/gaction 冲刺", targets[new("冲刺", "general")], "general action command selects the general action namespace");
        Expect("/共通技能 冲刺", targets[new("冲刺", "general")], "current CN general-action alias is recognized");
        Expect("/pvpac 对战 <t>", targets[new("对战", "pvpaction")], "explicit PvP macro is resolved separately");
        Expect("/blueaction 青魔法 <t>", targets[new("青魔法", "blueaction")], "blue action macro is recognized");
        Expect("/青魔法技能 青魔法 <t>", targets[new("青魔法", "blueaction")], "current CN blue-action alias is recognized");
        Expect("/坐骑 坐骑", targets[new("坐骑", "mount")], "mount macro selects mount details");
        Expect("/宠物 宠物", targets[new("宠物", "companion")], "companion macro selects companion details");
        Expect("/item 任务道具", targets[new("任务道具", "item")], "event-item macro preserves event-item identity");
        Expect("/item 药品\uE03C <me>", targets[new("药品\uE03C", "item")], "HQ suffix survives parser resolution");
        Expect("\r\n／Ac\t“技能 A”\t<2>\r\n", action, "smart quotes tabs and CRLF use existing macro tokenization");
        foreach (var text in new[] { "", " ", "/echo /ac 技能B", "/wait 1", "/em 惊讶", "/micon 惊讶 emote",
            "/ac", "/ac \"unfinished", "/ac 技能 A <t>", "/ac <t>", "/item 药品 extra", "/ac \u0002技能B\u0003" })
            check(!NativeHotbarMacroDetailRules.TryResolve(text, Find, out _), "unresolved/unsafe macro retains text help: " + text);
        foreach (var bad in new[] { new MacroDetailTarget(7, 0, "macro"), new MacroDetailTarget(1, 0, "zero"), new MacroDetailTarget(2, 2000000, "invalid item") })
            check(!NativeHotbarMacroDetailRules.TryResolve("/ac 技能B", _ => bad, out _), "resolver rejects unsupported or invalid returned identity " + bad);
        check(NativeHotbarMacroDetailRules.ItemName("药品 \uE03C", out var hq) == "药品" && hq
            && NativeHotbarMacroDetailRules.ItemName("药品 (HQ)", out hq) == "药品 (HQ)" && !hq,
            "only the actual HQ glyph is stripped; literal name punctuation is preserved");
        for (uint set = 0; set < 2; set++) for (uint index = 0; index < 100; index++)
            check(NativeHotbarMacroDetailRules.TryMacroAddress((set << 8) | index, out var actualSet, out var actualIndex)
                && actualSet == set && actualIndex == index, $"macro address {set}:{index} resolves exact personal/shared bank");
        foreach (var id in new uint[] { 100, 255, 356, 511, 512, uint.MaxValue })
            check(!NativeHotbarMacroDetailRules.TryMacroAddress(id, out _, out _), "invalid macro address rejected before native lookup " + id);
        var personal = new SuperMacro { Set = 0, Index = 0, Lines = ["/ac 技能B"] };
        var shared = new SuperMacro { Set = 1, Index = 0, Lines = ["/item 药品"] };
        check(ReferenceEquals(NativeHotbarMacroDetailRules.SavedMacro([personal, shared], 0), personal)
            && ReferenceEquals(NativeHotbarMacroDetailRules.SavedMacro([personal, shared], 256), shared)
            && NativeHotbarMacroDetailRules.SavedMacro([personal, shared], 1) is null,
            "backup lookup preserves exact macro bank and slot; absent backup content stays absent");
        Expect(string.Join('\n', personal.Lines), targets[new("技能B", "action")], "captured macro content resolves its own skill");
        personal.Lines[0] = "/item 药品";
        Expect(string.Join('\n', personal.Lines), item, "edited macro content resolves new target without retaining old skill");
    }

    internal static void Compiled(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        const string ns = "OmniToolbox.NativeDragHotbarLocal.";
        var module = assembly.GetType(ns + "NativeDragHotbar", true)!;
        var calls = ItemAndFontChecks.Calls(module.GetMethod("TrySlotDetail", flags)!).ToArray();
        check(!calls.Contains("SavedMacro") && calls.Contains("TryReadMacro") && calls.Contains("TryMacroDetail") && calls.Contains("TryDetail")
            && !calls.Any(x => x.StartsWith("Execute") || x is "SetString" or "SaveFile"),
            "compiled hover uses live macros for both bar kinds without executing or writing macros");
        var bridge = assembly.GetType(ns + "NativeHotbarBridge", true)!;
        calls = ItemAndFontChecks.Calls(bridge.GetMethod("TryReadMacro", flags)!).ToArray();
        check(Array.IndexOf(calls, "TryMacroAddress") < Array.IndexOf(calls, "GetMacro")
            && !calls.Any(x => x.StartsWith("Execute") || x is "SetString" or "SaveFile"),
            "compiled live macro reader validates address before reading its 15 lines; no execution or writes");
        calls = ItemAndFontChecks.Calls(bridge.GetMethod("TryMacroDetail", flags)!).ToArray();
        check(calls.Contains("TryResolve") && calls.Contains("TryDetail"), "resolved macro targets share normal dynamic-appearance and sheet validation");
        var resolver = assembly.GetType(ns + "NativeHotbarMacroDetails", true)!;
        calls = ItemAndFontChecks.Calls(resolver.GetMethod("Find", flags)!).ToArray();
        check(calls.Contains("ExtractText") && calls.Contains("get_CanBeHq") && calls.Contains("get_IsPvP") && !calls.Contains("FindIcon"),
            "compiled macro lookup uses canonical sheet names, HQ capability and separate PvP identity instead of icon matching");
    }
}
