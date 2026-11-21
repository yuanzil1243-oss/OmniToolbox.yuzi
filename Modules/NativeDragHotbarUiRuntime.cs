using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static unsafe class NativeHotbarUiContext
{
    internal static bool ExtensionsVisible(bool followNativeHide, bool nativeVisible)
        => NativeHotbarUiRules.ExtensionsVisible(RaptureAtkModule.Instance() != null, nativeVisible, followNativeHide);

    internal static bool Visible()
    {
        var atk = RaptureAtkModule.Instance();
        if (atk == null) return false;
        var condition = DService.Instance().Condition;
        return NativeHotbarUiRules.Visible(atk->IsUiVisible,
            (atk->RaptureAtkUnitManager.UiFlags & (UiFlags.Hud | UiFlags.ActionBars)) != 0,
            condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78]
                || condition[ConditionFlag.OccupiedInCutSceneEvent],
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]);
    }
}

public sealed partial class NativeDragHotbar
{
    private readonly NativeHotbarDrawFallback extensionDrawFallback = new();
    private int hotbarDrawFrame = -1;
    private string extensionDrawError = "";

    private void DrawExtensionsWhenHostHidden()
    {
        // The host's normal callback runs first. Never draw twice or bypass a
        // normal host error/paused state while the game's hotbars are visible.
        if (profile.FollowNativeHide || hotbarDrawFrame == Dalamud.Bindings.ImGui.ImGui.GetFrameCount()
            || NativeHotbarUiContext.Visible()) return;
        Draw();
    }

    private void SuspendHotbarUi()
    {
        superReady = false;
        RestoreSuperVisibility();
        nativeDetails?.Release();
        builtInRepeat.Clear();
        superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
        targets.Clear(); received.Clear();
        localDragBar = null; localDragIndex = -1; movingBar = null;
    }
}
