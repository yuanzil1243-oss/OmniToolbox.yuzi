using System.Reflection;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;
using OmniToolbox.KeyRepeaterOnline;
using OmniToolbox.SuperHotbarInterop;

internal static class MappingAndRepeatChecks
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        string[] names = ["_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
            "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09"];
        for (var b = 0; b < 10; b++)
        {
            check(NativeHotbarIdentity.AddonName(b) == names[b] && NativeHotbarIdentity.Matches(b, (uint)b),
                $"display bar {b + 1} maps to exact native addon and identity {names[b]}");
            for (var page = 0; page < 10; page++)
                check(NativeHotbarIdentity.LogicalBar(b, page) == (b == 0 ? page : b),
                    $"physical bar {b + 1} uses correct content for primary page {page + 1}");
            for (var s = 0; s < 12; s++)
            {
                var id = NativeHotbarIdentity.InputId(b, s);
                check(id == 57 + b * 12 + s && NativeHotbarIdentity.TryInput(id, out var bar, out var slot) && bar == b && slot == s,
                    $"bar {b + 1} slot {s + 1} binding round-trips to InputId {id}");
            }
            if (b != 0) check(!NativeHotbarIdentity.Matches(b, (uint)((b + 1) % 10)), $"bar {b + 1} rejects a different native bar");
        }
        reject(() => NativeHotbarIdentity.AddonName(10), "no nonexistent _ActionBar10 can be requested");
        reject(() => NativeHotbarIdentity.InputId(0, 12), "out of range slot cannot alias the next bar");
        check(!NativeHotbarIdentity.TryInput(56, out _, out _) && !NativeHotbarIdentity.TryInput(177, out _, out _),
            "page and pet bindings never masquerade as ordinary positions");

        // Captured read-only from the user's running game on 2026-11-08; no live calls or writes in replay.
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests/NativeDragHotbar.Tests/Fixtures/native-bar-identities.json")));
        int[] codes = [1, 1, 2, 1, 2, 2, 2, 1, 4, 2];
        var rows = runtime.RootElement.EnumerateArray().Where(r => r.GetProperty("Loaded").GetBoolean()).ToArray();
        check(rows.Length == 10, "runtime evidence includes every one of the ten native bars");
        for (var b = 0; b < 10; b++)
        {
            var row = rows[b];
            check(row.GetProperty("Loaded").GetBoolean() && row.GetProperty("Name").GetString() == names[b]
                && row.GetProperty("DisplayBar").GetInt32() == b + 1
                && row.GetProperty("Identity").GetInt32() == b
                && row.GetProperty("Layout").GetInt32() == codes[b], $"runtime evidence bar {b + 1}: identity and layout code {codes[b]}");
            var target = new DragHotbar { Name = "保留", Opacity = 0.4f, AnchorX = 0.3f, AnchorY = 0.7f };
            var slots = target.Slots; var id = target.Id;
            var source = new DragHotbar { X = b * 100, Y = b * 20, Scale = 1.25f, Visible = b % 2 == 0 };
            NativeHotbarLayout.ApplyNativeShape(source, codes[b]);
            NativeHotbarLayout.ApplyNativeAppearance(target, source);
            check(target.Columns == source.Columns && target.Rows == source.Rows && target.X == source.X && target.Y == source.Y
                && target.Scale == source.Scale && target.Visible == source.Visible && target.Id == id && target.Name == "保留"
                && target.Opacity == 0.4f && ReferenceEquals(target.Slots, slots) && target.AnchorX is null && target.AnchorY is null,
                $"all-bar appearance repair {b + 1} preserves slot storage, identity and custom styling");
        }
        var invalid = new DragHotbar { Scale = float.NaN }; var unchanged = new DragHotbar();
        var before = JsonSerializer.Serialize(unchanged);
        reject(() => NativeHotbarLayout.ApplyNativeAppearance(unchanged, invalid), "invalid appearance rejected before mutation");
        check(before == JsonSerializer.Serialize(unchanged), "rejected appearance leaves target unchanged");

        var data = new SuperHotbarDocument();
        data.Bindings = [new() { InputId = 69, Primary = new() { Key = 65, Modifiers = 2 } },
            new() { InputId = 57, Secondary = new() { Key = 65, Modifiers = 2 } },
            new() { InputId = 58, Primary = new() { Key = 66 } }, new() { InputId = 45, Primary = new() { Key = 66 } }];
        check(SuperHotbarRules.FirstBinding(data, 65, 2)?.InputId == 57 && SuperHotbarRules.FirstBinding(data, 65, 3) is null,
            "primary and alternate keys use exact modifiers and first position priority regardless of storage order");
        check(SuperHotbarRules.RepeatChords(data).SequenceEqual(new[] { 65 | 2 << 8 }),
            "repeat uses independent module keys, deduplicates positions and excludes page-switch conflicts");
        foreach (var chord in new[] { -1, 0, 16, 17, 18, 91, 92, 160, 165, 255, 65 | 8 << 8 })
            check(!SuperHotbarRepeatProtocol.TryChord(chord, out _, out _), $"IPC rejects unsupported chord {chord}");
        check(SuperHotbarRepeatProtocol.TryChord(112 | 7 << 8, out var key, out var mods) && key == 112 && mods == 7,
            "IPC retains all modifier combinations and function keys");

        foreach (var pollFirst in new[] { false, true })
        {
            var edges = new SuperKeyEdges(); var timer = new KeyRepeatTiming(); var executions = 0;
            void Poll() { if (edges.Press(65, true, true, 2)) executions++; }
            void Repeat(long time)
            { if (timer.Tick(65 | 2 << 8, time, 10, out var first) && !first && edges.CanRepeat(65, 2)) executions++; }
            if (pollFirst) { Poll(); Repeat(0); } else { Repeat(0); Poll(); }
            check(executions == 1, $"first physical press executes exactly once with pollFirst={pollFirst}");
            Repeat(99); check(executions == 1, "repeat does not fire before 10 CPS interval");
            Repeat(100); check(executions == 2, "repeat fires once at next 10 CPS interval");
            Repeat(1000); Repeat(1000); check(executions == 3, "delayed frames never burst accumulated repeat actions");
            edges.Press(65, true, false, 2); Repeat(1100);
            edges.Press(65, true, true, 2); Repeat(1200);
            check(executions == 3, "chat focus and blocked contexts require release before repeat resumes");
            edges.Press(65, false, true, 2); timer.Release(65 | 2 << 8); Poll(); Repeat(1300);
            check(executions == 4, "release re-arms one physical activation with no duplicate first tick");
            edges.Press(65, true, true, 3); edges.Press(65, true, true, 2);
            check(!edges.CanRepeat(65, 2), "modifier change cancels repeats until key release");
            edges.BlockHeld(k => k == 65); check(!edges.CanRepeat(65, 2), "job/context transitions revoke held-key repeat permission");
        }
        var timing = new KeyRepeatTiming();
        check(timing.Tick(1, 0, float.NaN, out var firstTick) && firstTick && !timing.Tick(1, 99, float.NaN, out _)
            && timing.Tick(1, 100, float.NaN, out firstTick) && !firstTick, "invalid CPS safely defaults to ten");
        timing.Clear(); check(timing.Tick(1, 100, 10, out firstTick) && firstTick, "clearing timing re-arms initial tick");
    }

    public static void CheckNativeInputs(Assembly structs, Action<bool, string> check)
    {
        var input = structs.GetType("FFXIVClientStructs.FFXIV.Client.System.Input.InputId", true)!;
        string[] suffixes = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "A", "B"];
        for (var b = 0; b < 10; b++)
            for (var s = 0; s < 12; s++)
                check(Convert.ToInt32(Enum.Parse(input, $"HOTBAR_{b + 1}_{suffixes[s]}")) == NativeHotbarIdentity.InputId(b, s),
                    $"actual FCS InputId matches bar {b + 1} slot {s + 1}");
    }
}
