using System;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmniToolbox.Host;
using OmniToolbox.SuperHotbarInterop;
using TreeTools.Lifecycle;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private long superRepeatAttempts;
    private readonly SuperRepeatClock builtInRepeat = new();

    private void PollBuiltInRepeat(bool allowed)
    {
        if (!allowed || super is not { BuiltInRepeat: true }) { builtInRepeat.Clear(); return; }
        var down = NativeHotbarBridge.IsKeyDown;
        var modifiers = (down(16) ? 1 : 0) | (down(17) ? 2 : 0) | (down(18) ? 4 : 0);
        var now = Environment.TickCount64;
        for (var key = 4; key <= 254; key++)
        {
            if (!SuperInputKeys.Valid(key) || !down(key) || !superEdges.CanRepeat(key, modifiers))
            { builtInRepeat.Release(key); continue; }
            var binding = SuperHotbarRules.FirstBinding(super, key, modifiers);
            var eligible = binding is not null && NativeHotbarIdentity.TryInput(binding.InputId, out _, out _);
            if (!builtInRepeat.Tick(key, now, super.RepeatCps, eligible)) continue;
            if (NativeHotbarIdentity.TryInput(binding!.InputId, out var bar, out var slot))
            { ExecuteSuperPosition(bar, slot); superRepeatAttempts++; }
        }
    }
    private void StartSuperRepeatBridge(FeatureLifetime lifetime)
    {
        var active = DalamudServices.PluginInterface.GetIpcProvider<bool>(SuperHotbarRepeatProtocol.Active);
        active.RegisterFunc(() => SuperActive);
        lifetime.OnStopping(active.UnregisterFunc);
        var bindings = DalamudServices.PluginInterface.GetIpcProvider<int[]>(SuperHotbarRepeatProtocol.Bindings);
        bindings.RegisterFunc(() => SuperActive && !super!.BuiltInRepeat && super.AllowKeyRepeater ? SuperHotbarRules.RepeatChords(super) : []);
        lifetime.OnStopping(bindings.UnregisterFunc);
        var repeat = DalamudServices.PluginInterface.GetIpcProvider<int, bool, bool>(SuperHotbarRepeatProtocol.Repeat);
        repeat.RegisterFunc(RepeatSuperShortcut);
        lifetime.OnStopping(repeat.UnregisterFunc);
    }

    private unsafe bool SuperInputAllowed()
    {
        if (!SuperActive || !NativeHotbarUiContext.Visible() || NativeBarGeometryReader.HudEditing() || Environment.TickCount64 - superLastDraw > 500) return false;
        var atk = RaptureAtkModule.Instance();
        return atk != null && atk->IsUiVisible && NativeHotbarBridge.IsGameForeground()
            && !atk->IsTextInputActive() && !ImGui.GetIO().WantTextInput
            && !editorOpen && replacing is null && superRecording < 0 && !restoreConfirm && superImport is null && superResetConfirm == 0
            // Ordinary UI clicks must not revoke held-key repeat ownership. Suspend
            // only an actual native payload, slot drag or whole-bar move.
            && bridge?.IsDragging != true && localDragBar is null && movingBar is null
            && !NativeHotbarBridge.IsKeyDown(91) && !NativeHotbarBridge.IsKeyDown(92);
    }

    private bool RepeatSuperShortcut(int chord, bool firstTick)
    {
        if (!SuperActive || !SuperHotbarRepeatProtocol.TryChord(chord, out var key, out var modifiers)) return false;
        // The module's normal edge poll owns the FIRST physical activation, independent of
        // framework subscriber ordering. Repeater only owns subsequent scheduled ticks.
        if (firstTick || super!.BuiltInRepeat || !super.AllowKeyRepeater) return true;
        try
        {
            if (!SuperInputAllowed()) { superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); return true; }
            var down = NativeHotbarBridge.IsKeyDown;
            var currentModifiers = (down(16) ? 1 : 0) | (down(17) ? 2 : 0) | (down(18) ? 4 : 0);
            if (!down(key) || modifiers != currentModifiers || !superEdges.CanRepeat(key, modifiers)) return true;
            if (SuperNativeArchive.CurrentGroup(owner) != superGroup)
            { superEdges.BlockHeld(down); return true; }
            var binding = SuperHotbarRules.FirstBinding(super, key, modifiers);
            if (binding is not null && NativeHotbarIdentity.TryInput(binding.InputId, out var bar, out var slot))
            { ExecuteSuperPosition(bar, slot); superRepeatAttempts++; }
            return true;
        }
        catch (Exception error)
        {
            PauseSuper(error);
            DalamudServices.PluginLog.Error(error, "热键栏工坊：连点适配暂停");
            return true;
        }
    }
}
