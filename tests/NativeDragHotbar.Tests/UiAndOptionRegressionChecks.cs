using System.Reflection;
using System.Collections;
using System.Numerics;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class UiAndOptionRegressionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(WorkshopGameRules.FloatCompatible(.61f, .7f, .61f, .69f, .78f),
            "LockonDefaultZoom native default .61 is accepted outside editable .69-.78 range");
        check(WorkshopGameRules.FloatCompatible(.61f, .61f, .61f, .69f, .78f),
            "unchanged LockonDefaultZoom default is a valid no-op on source and target");
        check(!WorkshopGameRules.FloatCompatible(.62f, .7f, .61f, .69f, .78f),
            "arbitrary out-of-range zoom is still rejected instead of silently resetting to default");
        foreach (var v in new[] { .69f, .71f, .78f })
            check(WorkshopGameRules.FloatCompatible(v, .7f, .61f, .69f, .78f), "float range endpoints/interior remain valid: " + v);
        check(WorkshopGameRules.FloatCompatible(.5f, .5f, .61f, .69f, .78f),
            "existing live value outside ordinary range needs no write and passes preflight");
        foreach (var v in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            check(!WorkshopGameRules.FloatCompatible(v, v, v, 0, 1), "non-finite float never passes no-op/default exceptions");
        check(!WorkshopGameRules.FloatCompatible(.7f, .71f, .61f, .78f, .69f)
            && !WorkshopGameRules.FloatCompatible(.7f, .71f, .61f, float.NaN, .78f),
            "inverted or invalid metadata cannot authorize a changed float");
        check(WorkshopGameRules.UIntCompatible(0, 2, 0, 1, 4)
            && WorkshopGameRules.UIntCompatible(9, 9, 0, 1, 4), "UInt defaults and unchanged live values use the same native-safe exceptions");
        check(!WorkshopGameRules.UIntCompatible(5, 2, 0, 1, 4)
            && !WorkshopGameRules.UIntCompatible(0, 2, 0, 4, 1), "UInt arbitrary out-of-range and inverted metadata still fail");
        for (var mask = 0; mask < 16; mask++)
        {
            var visible = NativeHotbarUiRules.Visible((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
            check(visible == (mask == 1), "UI/cutscene/transition visibility truth table: " + mask);
        }
    }

    internal static void Compiled(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        const string ns = "OmniToolbox.NativeDragHotbarLocal.";
        var module = assembly.GetType(ns + "NativeDragHotbar", true)!;
        string[] Calls(Type t, string name) => ItemAndFontChecks.Calls(t.GetMethod(name, flags)!).ToArray();
        var input = Calls(module, "SuperInputAllowed");
        check(!input.Contains("IsMouseDown") && input.Contains("IsTextInputActive") && input.Contains("get_WantTextInput")
            && input.Contains("HudEditing") && input.Contains("get_IsDragging") && input.Contains("IsGameForeground")
            && Calls(module, "UpdateSuper").Contains("SuperInputAllowed") && Calls(module, "RepeatSuperShortcut").Contains("SuperInputAllowed"),
            "ordinary mouse clicks cannot cancel built-in/external held-key repeats; text input, native dragging, HUD editing and foreground checks remain");
        var native = assembly.GetType(ns + "WorkshopGameNative", true)!;
        var preflight = Calls(native, "Preflight");
        check(preflight.Contains("FloatCompatible") && preflight.Contains("UIntCompatible") && preflight.Contains("get_Default")
            && preflight.Contains("TryGetFloat") && preflight.Contains("TryGetUInt"),
            "actual preflight validates typed properties with current value and native default, not only min/max");
        check(new[] { "Draw", "DrawSuperBars", "UpdateSuper", "SuperInputAllowed", "FindDropArea", "ValidRequest", "OnSuperNativeDraw", "OnSuperNativeMouse" }
            .All(name => Calls(module, name).Contains("Visible")),
            "compiled draw, keys/repeat, drops and native suppression all honor game/cutscene visibility");
        check(new[] { "Release", "Clear", "BlockHeld", "RestoreSuperVisibility" }.All(name => Calls(module, "SuspendHotbarUi").Contains(name)),
            "hidden UI releases owned tooltip/native mouse lease, clears repeat/drop state, blocks held keys");
        check(Calls(module, "HotbarDrawList").Contains("GetBackgroundDrawList")
            && Calls(module, "DrawSlot").Contains("HotbarDrawList") && Calls(module, "DrawSuperBar").Contains("HotbarDrawList")
            && !Calls(module, "DrawSuperSlotOverlay").Contains("GetWindowDrawList"),
            "all takeover pixels, including lock and cooldown/key text, share the background canvas");
        var text = Calls(assembly.GetType(ns + "NativeHotbarIconVisual", true)!, "Text");
        check(text.Contains("PushTextureID") && text.Contains("PopTextureID"),
            "background game-font drawing scopes the selected atlas without leaking texture state");
        var instance = Activator.CreateInstance(module)!;
        var superField = module.GetField("super", flags)!;
        var compiledSaved = JsonSerializer.Deserialize(JsonSerializer.Serialize(new SuperHotbarDocument
            { Enabled = true, Show = true, Appearance = [new() { Visible = false }] }), superField.FieldType)!;
        superField.SetValue(instance, compiledSaved);
        var before = JsonSerializer.Serialize(compiledSaved, superField.FieldType);
        var bars = (IList)superField.FieldType.GetProperty("Appearance")!.GetValue(compiledSaved)!;
        var entry = ((IList)bars[0]!.GetType().GetProperty("Slots")!.GetValue(bars[0])!)[0]!;
        var targets = (IList)module.GetField("targets", flags)!.GetValue(instance)!;
        var area = module.GetNestedType("DropArea", flags)!;
        targets.Add(Activator.CreateInstance(area, flags, null, [bars[0], 0, Vector2.Zero, new Vector2(48), entry, false, false], null));
        var repeat = module.GetField("builtInRepeat", flags)!.GetValue(instance)!;
        repeat.GetType().GetMethod("Tick")!.Invoke(repeat, [49, 1000L, 10f, true]);
        foreach (var field in new[] { "movingBar", "localDragBar" }) module.GetField(field, flags)!.SetValue(instance, bars[0]);
        module.GetField("localDragIndex", flags)!.SetValue(instance, 0);
        module.GetField("superReady", flags)!.SetValue(instance, true);
        module.GetMethod("SuspendHotbarUi", flags)!.Invoke(instance, null);
        check(JsonSerializer.Serialize(compiledSaved, superField.FieldType) == before,
            "actual compiled hide transition preserves all saved takeover/layout/visibility state");
        check(!(bool)module.GetField("superReady", flags)!.GetValue(instance)! && targets.Count == 0
            && module.GetField("movingBar", flags)!.GetValue(instance) is null
            && module.GetField("localDragBar", flags)!.GetValue(instance) is null
            && (int)module.GetField("localDragIndex", flags)!.GetValue(instance)! == -1,
            "actual hide transition removes stale input targets and active slot/bar drags");
        check(!(bool)repeat.GetType().GetMethod("Tick")!.Invoke(repeat, [49, 2000L, 10f, true])!,
            "actual hide transition clears repeat schedule so resume cannot immediately burst");
        var tooltipType = assembly.GetType(ns + "NativeHotbarDetailTooltips", true)!;
        var visible = true;
        var tooltip = Activator.CreateInstance(tooltipType, flags, null,
            [(Func<nint>)(() => 0), (Func<int, nint>)(_ => 0), (Func<bool>)(() => visible)], null)!;
        var ownership = tooltipType.GetField("ownership", flags)!.GetValue(tooltip)!;
        ownership.GetType().GetMethod("Mark", flags)!.Invoke(ownership, [(nint)1, (nint)2, (ushort)3, 1000L]);
        tooltipType.GetMethod("Tick", flags)!.Invoke(tooltip, [1001L]);
        check((bool)ownership.GetType().GetProperty("Active", flags)!.GetValue(ownership)!,
            "visible fresh native tooltip remains owned until normal expiration");
        visible = false; tooltipType.GetMethod("Tick", flags)!.Invoke(tooltip, [1002L]);
        check(!(bool)ownership.GetType().GetProperty("Active", flags)!.GetValue(ownership)!,
            "game-hidden watchdog immediately clears even a fresh owned tooltip without a draw callback");
    }
}
