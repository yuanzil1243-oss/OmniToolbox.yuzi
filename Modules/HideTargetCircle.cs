using System;
using System.IO;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.HideTargetCircleOnline;

public sealed unsafe class HideTargetCircle : ModuleBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly HideTargetCircleConfig config = new();
    private GameObject* modifiedTarget;
    private byte originalTargetStatus;
    private bool hasOriginalTargetStatus;
    private bool wasInCombat;
    private bool combatStateInitialized;
    private DateTime combatEndedAt;

    public override ModuleInfo Info { get; } = new()
    {
        Title = "隐藏目标圈", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "隐藏选中目标的目标圈，并可按拔出武器、战斗状态和战斗结束延迟控制显示。"
    };
    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        LoadConfig();
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        RestoreTarget();
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
            changed |= ImGui.DragFloat("战斗结束后延迟隐藏（秒）", ref config.CombatEndDelaySeconds, 0.1f, 0, 600, "%.1f");
        }
        config.CombatEndDelaySeconds = Math.Clamp(config.CombatEndDelaySeconds, 0, 600);
        if (changed) SaveConfig();
        return changed;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var control = Control.Instance();
        if (control == null)
        {
            RestoreTarget();
            wasInCombat = false;
            return;
        }

        var inCombat = Conditions.Instance()->InCombat;
        if (combatStateInitialized && wasInCombat && !inCombat)
            combatEndedAt = DateTime.UtcNow;
        wasInCombat = inCombat;
        combatStateInitialized = true;

        var show = (config.ShowWhenWeaponDrawn && control->TargetSystem.TargetCircleManager.WeaponDrawn)
            || (config.ShowInCombat && (inCombat
                || (combatEndedAt != default
                    && (DateTime.UtcNow - combatEndedAt).TotalSeconds < config.CombatEndDelaySeconds)));
        var target = control->TargetSystem.Target;
        if (target == null || show)
        {
            RestoreTarget();
            return;
        }

        if (modifiedTarget != target)
        {
            RestoreTarget();
            modifiedTarget = target;
            originalTargetStatus = target->NextTargetStatus;
            hasOriginalTargetStatus = true;
        }

        if (target->NextTargetStatus != 0)
            target->NextTargetStatus = 0;
    }

    private void RestoreTarget()
    {
        if (modifiedTarget != null && hasOriginalTargetStatus)
            modifiedTarget->NextTargetStatus = originalTargetStatus;
        modifiedTarget = null;
        hasOriginalTargetStatus = false;
    }

    private static string ConfigPath => Path.Combine(
        DalamudServices.PluginInterface.GetPluginConfigDirectory(), "HideTargetCircle.json");

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var saved = JsonSerializer.Deserialize<HideTargetCircleConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            if (saved == null) return;
            config.ShowWhenWeaponDrawn = saved.ShowWhenWeaponDrawn;
            config.ShowInCombat = saved.ShowInCombat;
            config.CombatEndDelaySeconds = Math.Clamp(saved.CombatEndDelaySeconds, 0, 600);
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取隐藏目标圈配置失败"); }
    }

    private void SaveConfig()
    {
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存隐藏目标圈配置失败"); }
    }
}

[Serializable]
public sealed class HideTargetCircleConfig
{
    public bool ShowWhenWeaponDrawn;
    public bool ShowInCombat;
    public float CombatEndDelaySeconds = 3;
}
