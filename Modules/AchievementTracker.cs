using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;
using OmenTools.OmenService.Abstractions;
using OmenTools.Extensions;
using FfxivAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;
using LuminaAchievement = Lumina.Excel.Sheets.Achievement;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.AchievementTrackerOnline;

public sealed unsafe class AchievementTracker : ModuleBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, WriteIndented = true };
    private readonly AchievementTrackerConfig config = new();
    private readonly Dictionary<uint, AchievementProgress> progress = new();
    private readonly Queue<uint> requests = new();
    private Dalamud.Hooking.Hook<ReceiveAchievementProgressDelegate>? progressHook;
    private AchievementContextMenuEntry? contextMenuEntry;
    private string currentRegion = "";

    private delegate void ReceiveAchievementProgressDelegate(FfxivAchievement* achievement, uint id, uint current, uint max);

    public override ModuleInfo Info { get; } = new()
    {
        Title = "成就追踪", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "读取游戏原生成就进度并在游戏内显示多个目标，支持地区限制和本地保存。"
    };
    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        LoadConfig();
        progressHook = DService.Instance().Hook.HookFromMemberFunction<ReceiveAchievementProgressDelegate>(
            typeof(FfxivAchievement.MemberFunctionPointers), "ReceiveAchievementProgress", ReceiveAchievementProgress);
        progressHook.Enable();
        DalamudServices.Framework.Update += OnFrameworkUpdate;
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
        contextMenuEntry = new AchievementContextMenuEntry(this);
        OmenServiceBase<ContextMenuManager>.Instance().Reg(contextMenuEntry);
        DalamudServices.PluginInterface.UiBuilder.Draw += DrawOverlay;
        UpdateTerritory();
        QueueProgressRequests();
    }

    protected override void OnDisable()
    {
        DalamudServices.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;
        if (contextMenuEntry != null)
            OmenServiceBase<ContextMenuManager>.Instance().Unreg(contextMenuEntry);
        contextMenuEntry = null;
        progressHook?.Dispose();
        progressHook = null;
        progress.Clear();
        requests.Clear();
    }

    public override bool DrawSettings()
    {
        var changed = false;
        ImGui.TextUnformatted("输入成就表 ID；名称与进度由游戏成就系统读取。");
        ImGui.Separator();
        for (var i = 0; i < config.Goals.Count; i++)
        {
            var goal = config.Goals[i];
            ImGui.PushID(i);
            changed |= ImGui.Checkbox("启用", ref goal.Enabled);
            ImGui.SameLine();
            changed |= ImGui.InputInt("成就 ID", ref goal.Id);
            if (goal.Id > 0)
            {
                var row = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>().GetRowOrDefault((uint)goal.Id);
                ImGui.TextDisabled(row.HasValue ? row.Value.Name.ToString() : "无效成就 ID");
            }
            changed |= ImGui.InputText("地区（留空为不限制）", ref goal.Region, 64);
            changed |= ImGui.Checkbox("不限制地区", ref goal.IgnoreRegion);
            ImGui.Separator();
            ImGui.PopID();
        }
        changed |= ImGui.Checkbox("显示游戏内窗口", ref config.ShowOverlay);
        changed |= ImGui.Checkbox("已完成目标仍然显示", ref config.ShowCompleted);
        if (ImGui.Button("重新读取进度")) { QueueProgressRequests(); changed = true; }
        if (changed) { SaveConfig(); QueueProgressRequests(); }
        return changed;
    }

    private void DrawOverlay()
    {
        if (!config.ShowOverlay) return;
        ImGui.SetNextWindowBgAlpha(config.Opacity);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize;
        if (!ImGui.Begin("##AchievementTrackerOverlay", flags)) { ImGui.End(); return; }
        ImGui.TextUnformatted("目标成就 · " + (string.IsNullOrEmpty(currentRegion) ? "未知地区" : currentRegion));
        foreach (var goal in config.Goals)
        {
            if (!goal.Enabled || goal.Id <= 0 || (!goal.IgnoreRegion && !string.IsNullOrWhiteSpace(goal.Region) && !string.Equals(goal.Region, currentRegion, StringComparison.OrdinalIgnoreCase))) continue;
            progress.TryGetValue((uint)goal.Id, out var state);
            var completed = state.Max > 0 && state.Current >= state.Max;
            if (!config.ShowCompleted && completed) continue;
            var row = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>().GetRowOrDefault((uint)goal.Id);
            ImGui.Separator();
            ImGui.TextUnformatted(row.HasValue ? row.Value.Name.ToString() : $"无效成就 ID: {goal.Id}");
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(goal.Region) ? "不限制地区" : goal.Region);
            var ratio = state.Max == 0 ? 0 : Math.Clamp(state.Current / (float)state.Max, 0, 1);
            ImGui.ProgressBar(ratio, new System.Numerics.Vector2(220, 0), state.Max == 0 ? "读取中…" : $"{state.Current} / {state.Max} ({ratio:P0})");
            ImGui.TextColored(completed ? new System.Numerics.Vector4(0.3f, 1, 0.6f, 1) : new System.Numerics.Vector4(1, .75f, .3f, 1), completed ? "已完成" : "进行中");
        }
        ImGui.End();
    }

    private void QueueProgressRequests()
    {
        requests.Clear();
        foreach (var id in config.Goals.Where(x => x.Enabled && x.Id > 0).Select(x => (uint)x.Id).Distinct())
            requests.Enqueue(id);
    }

    private sealed class AchievementContextMenuEntry(AchievementTracker module) : ContextMenuEntry
    {
        public override string Identifier => "AchievementTracker";

        public unsafe override ContextMenuItem? Create(ContextMenuOpenedArgs args)
        {
            if (!string.Equals(args.AddonName, "Achievement", StringComparison.Ordinal))
                return null;

            var agent = AgentAchievement.Instance();
            if (agent == null || agent->ContextMenuSelectedItemId == 0)
                return null;

            var achievementId = agent->ContextMenuSelectedItemId;
            var submenuEntries = new List<ContextMenuEntry>(module.config.Goals.Count);
            for (var index = 0; index < module.config.Goals.Count; index++)
            {
                var slot = index;
                submenuEntries.Add(new ContextMenuEntryInfo(
                    $"AchievementTracker.Target{slot + 1}",
                    _ => new ContextMenuItem
                    {
                        Name = $"目标 {slot + 1}",
                        OnClicked = _ => module.SetGoalFromMenu(slot, achievementId),
                    }));
            }

            return new ContextMenuItem
            {
                Name = "添加到成就追踪",
                Priority = -1,
                Submenu = new ContextMenuSubmenu
                {
                    Title = "添加到成就追踪",
                    Entries = submenuEntries,
                },
            };
        }
    }

    private void SetGoalFromMenu(int slot, uint achievementId)
    {
        if (slot < 0 || slot >= config.Goals.Count || achievementId == 0) return;
        config.Goals[slot].Id = (int)achievementId;
        config.Goals[slot].Enabled = true;
        config.Goals[slot].IgnoreRegion = true;
        config.Goals[slot].Region = "";
        SaveConfig();
        QueueProgressRequests();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!DService.Instance().ClientState.IsLoggedIn || requests.Count == 0) return;
        var id = requests.Dequeue();
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestAchievement, id);
    }

    private void ReceiveAchievementProgress(FfxivAchievement* achievement, uint id, uint current, uint max)
    {
        progressHook!.Original(achievement, id, current, max);
        progress[id] = new(current, max);
    }

    private void OnTerritoryChanged(uint territoryType) => UpdateTerritory();

    private void UpdateTerritory()
    {
        var row = DalamudServices.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(DService.Instance().ClientState.TerritoryType);
        currentRegion = row.HasValue ? row.Value.PlaceName.ValueNullable?.Name.ToString() ?? "" : "";
    }

    private static string ConfigPath => Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "AchievementTracker.json");

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var saved = JsonSerializer.Deserialize<AchievementTrackerConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            if (saved == null) return;
            config.ShowOverlay = saved.ShowOverlay; config.ShowCompleted = saved.ShowCompleted; config.Opacity = saved.Opacity;
            config.Goals.Clear(); config.Goals.AddRange(saved.Goals);
            while (config.Goals.Count < 3) config.Goals.Add(new AchievementGoal());
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取成就追踪配置失败"); }
    }

    private void SaveConfig()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!); File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions)); }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存成就追踪配置失败"); }
    }
}

[Serializable]
public sealed class AchievementTrackerConfig
{
    public List<AchievementGoal> Goals = new() { new(), new(), new() };
    public bool ShowOverlay = true;
    public bool ShowCompleted = true;
    public float Opacity = 0.92f;
}

[Serializable]
public sealed class AchievementGoal
{
    public int Id;
    public string Region = "";
    public bool Enabled = true;
    public bool IgnoreRegion = true;
}

public readonly record struct AchievementProgress(uint Current, uint Max);
