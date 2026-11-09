using System.Numerics;
using OmniToolbox.NativeDragHotbarLocal;
using OmniToolbox.SuperHotbarInterop;

internal static class FeatureChecks
{
    public static void Run(string root, string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        var map = new SuperHotbarKeyMap([(49, 49), (192, 136)]);
        foreach (var command in new[] { "toggle", "on #1", "off 扩展栏", "show", "hide \"带 空格\"" })
            check(SuperHotbarCommands.TryParse(command, out var takeover, out _, out _) && !takeover,
                "legacy visibility command always selects extra-bar scope " + command);
        check(SuperHotbarCommands.TryParse("takeover toggle #10", out var nativeScope, out var action, out var selector)
            && nativeScope && action == HotbarVisibilityAction.Toggle && selector == "#10"
            && SuperHotbarCommands.TryParse("TAKEOVER\ton \"原生 2\"", out nativeScope, out action, out selector)
            && nativeScope && action == HotbarVisibilityAction.Show && selector == "原生 2",
            "takeover visibility uses an explicit independent command scope and preserves selectors");
        check(!SuperHotbarCommands.TryParse("takeover unknown #2", out _, out _, out _), "unknown takeover verbs cannot silently toggle anything");
        foreach (var mouse in new[] { 4, 5, 6 })
        {
            var shortcut = new SuperShortcut { Key = mouse, Modifiers = 2 };
            var gameMouse = mouse == 4 ? 161 : mouse == 5 ? 163 : 164;
            check(SuperShortcut.Valid(shortcut) && SuperInputKeys.Windows(map, gameMouse) == mouse
                && SuperInputKeys.Native(map, mouse) == gameMouse && SuperInputKeys.TryNative(map, mouse, out var native) && native == gameMouse,
                "mouse button survives capture, independent binding, conflict checks and native restore " + mouse);
            check(shortcut.Compact() == "cM" + (mouse - 1), "mouse label is distinct and compact " + mouse);
        }
        check(!SuperInputKeys.Valid(1) && !SuperInputKeys.Valid(2) && !SuperInputKeys.Valid(3)
            && !SuperInputKeys.Valid(7) && !SuperInputKeys.Valid(16) && !SuperInputKeys.Valid(255)
            && !SuperInputKeys.TryNative(map, 254, out _), "ordinary clicks, modifiers, unused mouse code and unmapped keys do not become shortcut bindings");

        var clock = new SuperRepeatClock();
        check(!clock.Tick(5, 1000, 10, true) && !clock.Tick(5, 1099, 10, true)
            && clock.Tick(5, 1100, 10, true), "built-in repeater owns only subsequent ticks after the edge-triggered first activation");
        check(clock.Tick(5, 9999, 10, true) && !clock.Tick(5, 9999, 10, true), "slow frames do not accumulate repeat bursts");
        check(!clock.Tick(5, 10000, 10, false) && !clock.Tick(5, 12000, 10, true), "releasing or pausing a key resets its repeat deadline");
        clock.Clear(); check(!clock.Tick(6, 1, float.NaN, true) && clock.Tick(6, 101, float.NaN, true), "invalid repeat rates safely use default CPS");
        var edges = new SuperKeyEdges();
        check(edges.Press(5, true, true, 2) && edges.CanRepeat(5, 2), "side mouse key has normal first-press and repeat ownership");
        edges.BlockHeld(k => k == 5);
        check(!edges.Press(5, true, true, 2) && !edges.CanRepeat(5, 2), "chat/focus/HUD suspension blocks an already held side button");
        edges.Press(5, false, true, 2);
        check(edges.Press(5, true, true, 2), "side mouse key rearms only after physical release");
        edges.Press(5, true, true, 0);
        check(!edges.CanRepeat(5, 2) && !edges.CanRepeat(5, 0), "changing modifiers while holding never activates a different repeat binding");

        check(NativeHotbarSafety.CanReadGeneralState(10, 4, true) && NativeHotbarSafety.CanReadGeneralState(10, 8, true)
            && !NativeHotbarSafety.CanReadGeneralState(10, 4, false) && !NativeHotbarSafety.CanReadGeneralState(10, 0, true),
            "Sprint/Return use the common-action cooldown path only with a valid GeneralAction identity");
        check(Enumerable.Range(0, 256).Where(t => t != 10).All(t => !NativeHotbarSafety.CanReadGeneralState((byte)t, 1004551, true))
            && !NativeHotbarSafety.CanReadCombatState(10, 4, true), "item/HQ/macro identities never enter the common cooldown path and common actions never enter unsafe charge getters");
        var options = new Dictionary<string, uint> { ["HotbarNoneSlotDisp01"] = 0, ["HotbarNoneSlotDisp10"] = 1 };
        check(SuperHotbarVisibility.HideEmpty(options, 0, false) && !SuperHotbarVisibility.HideEmpty(options, 9, true)
            && SuperHotbarVisibility.HideEmpty(options, 4, true), "all-ten native empty-slot options retain individual identities and fallback");
        for (var index = 0; index < 12; index++)
            check(SuperHotbarVisibility.Cell(index, 6, 40, new(4, 7)) == new Vector2(index % 6 * 44, index / 6 * 47),
                "visual-only empty-slot hiding retains fixed cell coordinates " + index);

        var area = new HotbarRect(new(0), new(100));
        var covers = new[] { new HotbarRect(new(20, 10), new(50, 80)), new HotbarRect(new(40, 40), new(90, 90)) };
        var visible = SuperHotbarVisibility.VisibleParts(area, covers);
        for (var x = 0; x < 100; x += 5)
            for (var y = 0; y < 100; y += 5)
            {
                var p = new Vector2(x + 0.1f, y + 0.1f);
                check(visible.Count(r => r.Contains(p)) == (covers.Any(r => r.Contains(p)) ? 0 : 1),
                    $"multiple overlapping native windows preserve exactly the uncovered hotbar pixels {x}/{y}");
            }
        check(SuperHotbarVisibility.VisibleParts(area, [area]).Count == 0
            && SuperHotbarVisibility.VisibleParts(area, [new(new(-10), new(0))]).Single() == area,
            "full cover and touching-only windows handle bounds without lost or doubled regions");

        var dir = Path.Combine(temp, "reset"); Directory.CreateDirectory(dir);
        var superPath = Path.Combine(dir, "SuperHotbar-1122334455667788.json");
        var superOther = Path.Combine(dir, "SuperHotbar-8877665544332211.json");
        File.WriteAllText(superPath, "native archive"); File.WriteAllText(superPath + ".bak", "native backup");
        Directory.CreateDirectory(superPath + ".history"); File.WriteAllText(Path.Combine(superPath + ".history", "old.json"), "old native");
        File.WriteAllText(superOther, "other character");
        var shared = HotbarSharedProfiles.PathFor(dir);
        var extra = new HotbarProfile { Owner = "0000000000000000" };
        extra.Bars[0].Slots[0] = new() { NativeType = 1, NativeId = 119 };
        HotbarProfileStore.Save(shared, extra, 0, false);
        File.WriteAllText(Path.Combine(dir, "OtherModule.json"), "unrelated");
        var archive = SuperHotbarResetStore.Clear(dir, false);
        check(!File.Exists(superPath) && !File.Exists(superOther) && !File.Exists(superPath + ".bak") && !Directory.Exists(superPath + ".history")
            && File.ReadAllText(Path.Combine(archive, Path.GetFileName(superOther))) == "other character",
            "clear-all takeover removes every character archive, backup and history from auto-load while preserving a manual snapshot");
        check(HotbarSharedProfiles.Load(dir, 1, out _, out _).Bars[0].Slots[0].NativeId == 119
            && File.ReadAllText(Path.Combine(dir, "OtherModule.json")) == "unrelated", "takeover-only reset preserves shared extra bars and unrelated modules");
        File.WriteAllText(superPath, "native again");
        HotbarProfileStore.Save(Path.Combine(dir, "NativeDragHotbar-1122334455667788.json"),
            new() { Owner = "1122334455667788" }, 0x1122334455667788, false);
        File.Copy(shared, shared + ".bak");
        var allArchive = SuperHotbarResetStore.Clear(dir, true);
        var clean = HotbarSharedProfiles.Load(dir, 0x1122334455667788, out var recovered, out var migrated);
        check(clean.Bars.Count == 0 && !recovered && migrated is null && !File.Exists(superPath) && !File.Exists(shared + ".bak")
            && !File.Exists(Path.Combine(dir, "NativeDragHotbar-1122334455667788.json")),
            "full reset writes canonical zero-bar state; restart cannot resurrect legacy profiles or backup content");
        check(File.Exists(Path.Combine(allArchive, Path.GetFileName(shared))) && File.ReadAllText(Path.Combine(dir, "OtherModule.json")) == "unrelated",
            "full reset keeps explicit manual archive and never changes another module");
        var rollbackDir = Path.Combine(temp, "reset-rollback"); Directory.CreateDirectory(rollbackDir);
        var first = Path.Combine(rollbackDir, "SuperHotbar-1122334455667788.json");
        var second = Path.Combine(rollbackDir, "SuperHotbar-1122334455667788.json.bak");
        File.WriteAllText(first, "first"); File.WriteAllText(second, "second");
        using (var locked = File.Open(second, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = false; try { SuperHotbarResetStore.Clear(rollbackDir, false); } catch (IOException) { failed = true; }
            check(failed && File.Exists(first) && File.ReadAllText(first) == "first", "partial reset failure rolls previously moved archives back");
        }
        check(File.ReadAllText(second) == "second", "failed reset preserves the locked backup unchanged");
    }
}
