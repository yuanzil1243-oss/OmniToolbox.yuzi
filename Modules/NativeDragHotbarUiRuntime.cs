using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static unsafe class NativeHotbarUiContext
{
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
