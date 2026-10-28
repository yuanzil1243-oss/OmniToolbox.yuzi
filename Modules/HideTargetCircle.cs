using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using OmenTools;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using TreeTools.Lifecycle;

namespace OmniToolbox.HideTargetCircleOnline;

[ModuleApiVersion(1)]
public sealed unsafe class HideTargetCircle : OmniModuleBase
{
    private Settings config = new();
    private readonly Stopwatch combatBufferTimer = new();
    private uint originalTargetCircleType;
    private bool hasOriginalTargetCircleType;
    private bool wasInCombat;
    private bool combatStateInitialized;

    public override ModuleInfo Info { get; } = new()
    {
        Author = "Yuzi", Category = ModuleCategory.Item
    };
    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(new Dictionary<string, string>
    {
        ["Title"] = "隐藏目标圈", ["Description"] = "隐藏选中目标的目标圈，并可按拔出武器、战斗状态和战斗结束延迟控制显示。"
    });
    public override bool HasSettings => true;

    protected override void OnStart(FeatureLifetime activation)
    {
        originalTargetCircleType = DService.Instance().GameConfig.UiControl.GetUInt("TargetCircleType");
        hasOriginalTargetCircleType = true;
        combatBufferTimer.Reset();
        wasInCombat = false;
        combatStateInitialized = false;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnStop()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        if (hasOriginalTargetCircleType)
            DService.Instance().GameConfig.UiControl.Set("TargetCircleType", originalTargetCircleType);
        hasOriginalTargetCircleType = false;
        combatBufferTimer.Reset();
        wasInCombat = false;
        combatStateInitialized = false;
    }

    public override bool DrawSettings()
    {
        var changed = ImGui.Checkbox("拔出武器时显示目标圈", ref config.ShowWhenWeaponDrawn);
        changed |= ImGui.Checkbox("战斗状态下显示目标圈", ref config.ShowInCombat);
        if (config.ShowInCombat)
        {
            ImGui.SetNextItemWidth(180);
            changed |= ImGui.DragFloat("战斗结束后延迟隐藏（秒）", ref config.CombatEndDelaySeconds, 0.1f, 0, 300, "%.1f");
        }
        config.CombatEndDelaySeconds = Math.Clamp(config.CombatEndDelaySeconds, 0, 300);
        return changed;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var services = DService.Instance();
        var inCombat = services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        if (combatStateInitialized && wasInCombat && !inCombat)
            combatBufferTimer.Restart();
        else if (inCombat)
            combatBufferTimer.Restart();
        else if (combatStateInitialized && !wasInCombat && !inCombat && combatBufferTimer.IsRunning
                 && combatBufferTimer.Elapsed.TotalSeconds >= config.CombatEndDelaySeconds)
            combatBufferTimer.Stop();
        wasInCombat = inCombat;
        combatStateInitialized = true;

        var weaponDrawn = services.ObjectTable.LocalPlayer?.StatusFlags.HasFlag(StatusFlags.WeaponOut) == true;
        var show = (config.ShowWhenWeaponDrawn && weaponDrawn)
            || (config.ShowInCombat && (inCombat
                || (combatBufferTimer.IsRunning && combatBufferTimer.Elapsed.TotalSeconds < config.CombatEndDelaySeconds)));
        var targetCircleType = show ? 1u : 0u;
        var uiControl = services.GameConfig.UiControl;
        if (uiControl.GetUInt("TargetCircleType") != targetCircleType)
            uiControl.Set("TargetCircleType", targetCircleType);
    }

    private sealed class Settings
    {
        public bool ShowWhenWeaponDrawn;
        public bool ShowInCombat;
        public float CombatEndDelaySeconds = 3;
    }
}
