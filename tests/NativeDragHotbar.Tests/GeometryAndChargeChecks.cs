using System.Numerics;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class GeometryAndChargeChecks
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (var (total, elapsed, max, charges, seconds, percent) in new[] {
            (60f, 22f, 2u, 0u, 8, 73), (60f, 52f, 2u, 1u, 8, 73),
            (90f, 22f, 3u, 0u, 8, 73), (90f, 52f, 3u, 1u, 8, 73),
            (90f, 82f, 3u, 2u, 8, 73), (60f, 30f, 2u, 1u, 30, 1),
            (60f, 0f, 2u, 0u, 30, 1), (60f, 59.5f, 2u, 1u, 1, 98),
            (30f, 22f, 1u, 0u, 8, 73), (60f, 60f, 2u, 2u, 0, 0),
            (60f, 0f, 2u, 2u, 0, 0), (60f, 30f, 2u, 2u, 0, 0),
            (0f, 0f, 2u, 0u, 0, 0), (float.NaN, 1f, 2u, 0u, 0, 0),
            (60f, float.PositiveInfinity, 2u, 0u, 0, 0), (60f, -1f, 2u, 0u, 0, 0),
            (60f, 22f, 0u, 0u, 0, 0), (60f, 22f, 20u, 0u, 0, 0) })
        {
            var actual = NativeIconVisualRules.NextCharge(total, elapsed, max, charges);
            check(actual == (percent, seconds), $"single-charge recast {total}/{elapsed}/{max}/{charges}: {seconds}s, {percent}%");
        }
        for (uint max = 2; max <= 3; max++)
            for (var tick = 0; tick < max * 300; tick++)
            {
                var elapsed = tick / 10f;
                var result = NativeIconVisualRules.NextCharge(max * 30, elapsed, max, (uint)(elapsed / 30));
                if (result.Seconds is < 1 or > 30 || result.Percent is < 1 or > 99)
                    throw new Exception("Multi-charge recast escaped the single-charge range.");
            }
        check(true, "two- and three-charge recovery remains in one-charge range through every tenth-second boundary");

        foreach (var columns in NativeHotbarLayout.NativeColumns)
            foreach (var scale in new[] { 0.5f, 0.8f, 0.9f, 1f, 1.1f, 1.2f, 2.5f })
            {
                var geometry = NativeBarGeometry.FromRoot(new(1097, 1213), scale, columns, 12 / columns);
                geometry.Validate();
                var display = geometry.OnViewport(new(-1920, 50));
                for (var slot = 0; slot < 12; slot++)
                {
                    var expected = new Vector2(1097, 1213) + new Vector2(46 + slot % columns * 45, 14 + slot / columns * 49) * scale;
                    if (Vector2.Distance(geometry.Frame(slot), expected) > 0.001f
                        || Vector2.Distance(display.Image(slot), expected + new Vector2(4, 3) * scale + new Vector2(-1920, 50)) > 0.001f
                        || Math.Abs(geometry.ImageSize.X - 40 * scale) > 0.001f)
                        throw new Exception("Native geometry/image/viewport alignment mismatch.");
                }
                check(Vector2.Distance(geometry.Root, new(1097, 1213)) < 0.001f,
                    $"all 12 slots align and restoration recovers addon origin for {columns}x{12 / columns}, scale {scale}");
                var bar = new DragHotbar { Opacity = 0.37f, Visible = false, AnchorX = 0.2f, AnchorY = 0.4f };
                var entries = bar.Slots; var id = bar.Id;
                check(geometry.Store(bar) && !geometry.Store(bar) && bar.Id == id && ReferenceEquals(bar.Slots, entries)
                    && bar.Opacity == 0.37f && !bar.Visible && bar.AnchorX is null && bar.AnchorY is null,
                    "geometry storage is idempotent and preserves content, identity, opacity and chosen visibility");
            }
        reject(() => new NativeBarGeometry(new(float.NaN, 0), 1, 12, 1).Validate(), "NaN geometry cannot enter the archive");
        reject(() => new NativeBarGeometry(Vector2.Zero, 1, 5, 3).Validate(), "unsupported native shape cannot enter geometry");
        reject(() => NativeNodeGeometry.Compose([]), "empty native parent chain rejected");
        reject(() => NativeNodeGeometry.Compose(new NativeNodeTransform[25]), "unbounded native parent chain rejected");
        var composed = NativeNodeGeometry.Compose([
            new(new(4, 3), Vector2.One, Vector2.Zero), new(new(46, 14), Vector2.One, Vector2.Zero),
            new(new(100, 200), new(1.2f), Vector2.Zero)]);
        check(Vector2.Distance(composed.Position, new(160, 220.4f)) < 0.001f && composed.Scale == new Vector2(1.2f),
            "local node composition recovers icon position even when hidden screen coordinates are stale");
        composed = NativeNodeGeometry.Compose([new(new(10, 20), new(2, 3), new(4, 5))]);
        check(composed.Position == new Vector2(6, 10), "node origin is respected while applying scaled parent transforms");

        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/NativeDragHotbar.Tests/Fixtures/native-bar-geometry.json")));
        var bars = evidence.RootElement.EnumerateArray().ToArray();
        check(bars.Length == 10, "geometry evidence covers every native bar");
        for (var b = 0; b < bars.Length; b++)
        {
            var fields = bars[b];
            var geometry = NativeBarGeometry.FromRoot(new(fields.GetProperty("RootX").GetSingle(), fields.GetProperty("RootY").GetSingle()),
                fields.GetProperty("ScaleX").GetSingle(), 12, 1);
            check(bars[b].GetProperty("Name").GetString() == NativeHotbarIdentity.AddonName(b)
                && Vector2.Distance(geometry.FirstFrame, new(fields.GetProperty("FirstFrameX").GetSingle(), fields.GetProperty("FirstFrameY").GetSingle())) < 0.002f
                && fields.GetProperty("Width").GetInt32() == 48 && fields.GetProperty("Height").GetInt32() == 48,
                $"real native bar {b + 1} confirms first-frame offset, HD size and native scale");
        }
        using var chainEvidence=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/NativeDragHotbar.Tests/Fixtures/native-local-chains.json")));
        var localChains=chainEvidence.RootElement.EnumerateArray().ToArray();
        check(localChains.Length==10,"read-only local-parent transform fixture covers all ten native bars");
        for(var b=0;b<localChains.Length;b++)
        {
            var row=localChains[b];
            var nodes=row.GetProperty("Chain").EnumerateArray().Select(n=>new NativeNodeTransform(
                new(n.GetProperty("X").GetSingle(),n.GetProperty("Y").GetSingle()),
                new(n.GetProperty("ScaleX").GetSingle(),n.GetProperty("ScaleY").GetSingle()),
                new(n.GetProperty("OriginX").GetSingle(),n.GetProperty("OriginY").GetSingle()))).ToArray();
            var actual=NativeNodeGeometry.Compose(nodes);var screen=row.GetProperty("FrameScreen");
            check(row.GetProperty("Name").GetString()==NativeHotbarIdentity.AddonName(b)
                && Vector2.Distance(actual.Position,new(screen[0].GetSingle(),screen[1].GetSingle()))<0.002f,
                $"compiled parent-transform math matches actual game first-frame screen coordinates for bar {b+1}");
        }
        foreach (var locked in new[] { false, true }) foreach (var hud in new[] { false, true })
        {
            check(NativeBarInteraction.Edit(true, locked, hud) == (!locked && !hud)
                && NativeBarInteraction.Input(true, hud) == !hud && NativeBarInteraction.Alpha(true, hud) == (hud ? 0.4f : 1),
                $"takeover HUD input/dimming and lock policy: locked={locked}, HUD={hud}");
            check(NativeBarInteraction.Input(false, hud) && NativeBarInteraction.Alpha(false, hud) == 1
                && NativeBarInteraction.Edit(false, locked, hud) == !locked,
                "extra-bar policy is isolated from native HUD editing");
        }
        var pulse = new NativeIconAnimation(); pulse.Trigger(5, 10);
        check(pulse.Pulse(5, 10) == 1 && Math.Abs(pulse.Pulse(5, 10.12) - 0.5) < 0.0001
            && pulse.Pulse(5, 10.3) == 0 && pulse.Pulse(6, 10.05) == 0 && pulse.Pulse(5, 9) == 0,
            "trigger feedback fades, expires, respects identity and clears on clock reversal");
        pulse.Trigger(5, 10.4); pulse.Trigger(5, 10.41);
        check(pulse.Pulse(5, 10.42) < 1, "high-CPS feedback advances rather than pinning at first frame");
        pulse.Trigger(6, 10.43);
        check(pulse.Pulse(6, 10.43) == 1 && pulse.Pulse(5, 10.43) == 0, "replaced action never inherits old trigger feedback");
    }
}
