using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class NativeLayoutChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        int[] expectedColumns = [12, 6, 4, 3, 2, 1];
        int[] expectedRows = [1, 2, 3, 4, 6, 12];
        var bar = new DragHotbar
        {
            Name = "保留的栏", X = 321, Y = 654, Scale = 1.25f, Opacity = 0.4f, Visible = false,
            Slots = Enumerable.Range(0, 14).Select(i => new HotbarEntry
                { NativeType = 2, NativeId = (uint)(1004551 + i), Name = "物品 " + i }).ToList(),
        };
        var slots = bar.Slots;
        var originals = slots.ToArray();
        var id = bar.Id;
        for (var code = 0; code < 6; code++)
        {
            check(NativeHotbarLayout.TryNativeShape(code, out var columns, out var rows)
                && columns == expectedColumns[code] && rows == expectedRows[code] && columns * rows == 12,
                $"native layout code {code} maps to {expectedColumns[code]}x{expectedRows[code]} in game HUD order");
            bar.AnchorX = 0.8f; bar.AnchorY = 0.2f;
            NativeHotbarLayout.ApplyNativeShape(bar, code);
            check(bar.Columns == columns && bar.Rows == rows && bar.SlotCount == 12
                && ReferenceEquals(bar.Slots, slots) && bar.Slots.SequenceEqual(originals)
                && bar.Slots.Select(s => s.NativeId).SequenceEqual(Enumerable.Range(0, 14).Select(i => (uint)(1004551 + i))),
                $"layout {columns}x{rows} preserves all slot objects order and identities including hidden slots");
            check(bar.X == 321 && bar.Y == 654 && bar.Scale == 1.25f && bar.Opacity == 0.4f
                && !bar.Visible && bar.Name == "保留的栏" && bar.Id == id && bar.AnchorX is null && bar.AnchorY is null,
                $"layout {columns}x{rows} keeps position appearance and visibility while rebasing size anchors");
        }
        foreach (var code in new[] { -1, 6, 255, int.MinValue, int.MaxValue })
        {
            var before = JsonSerializer.Serialize(bar);
            check(!NativeHotbarLayout.TryNativeShape(code, out var columns, out var rows) && columns == 0 && rows == 0,
                $"unknown native layout {code} cannot be guessed");
            reject(() => NativeHotbarLayout.ApplyNativeShape(bar, code), $"unknown layout {code} is rejected before applying");
            check(JsonSerializer.Serialize(bar) == before && ReferenceEquals(bar.Slots, slots),
                $"rejecting layout {code} leaves the full bar untouched");
        }
        var fixturePath = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_LAYOUT_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            using var snapshot = JsonDocument.Parse(File.ReadAllText(fixturePath));
            var bars = Enumerable.Range(0, 10).Select(_ => new DragHotbar { Columns = 3, Rows = 4 }).ToArray();
            var count = 0;
            foreach (var item in snapshot.RootElement.EnumerateArray())
            {
                if (!item.GetProperty("Loaded").GetBoolean()) continue;
                var index = item.GetProperty("Bar").GetInt32();
                var code = item.GetProperty("Code").GetInt32();
                NativeHotbarLayout.ApplyNativeShape(bars[index], code);
                check(bars[index].Columns == expectedColumns[code] && bars[index].Rows == expectedRows[code],
                    $"read-only runtime layout fixture for bar {index + 1} retains its actual HUD shape");
                count++;
            }
            check(count == 10, "corrected layout replay covers all ten loaded native bars");
        }
    }
}
