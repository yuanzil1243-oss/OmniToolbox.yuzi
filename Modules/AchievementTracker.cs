using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Info.Game.Enums;
using OmenTools.Extensions;
using FfxivAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;
using LuminaAchievement = Lumina.Excel.Sheets.Achievement;
using LuminaMap = Lumina.Excel.Sheets.Map;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using TreeTools.Lifecycle;

namespace OmniToolbox.AchievementTrackerOnline;

[ModuleApiVersion(1)]
public sealed unsafe class AchievementTracker : OmniModuleBase
{
    // Offline region supplements use stable game IDs; localized names are resolved from TerritoryType.
    // Refresh these curated entries in a module release after expansion Wiki data is complete.
    private static readonly IReadOnlyDictionary<uint, uint[]> OfflineRegionSupplements = new Dictionary<uint, uint[]>
    {
        [3672] = new uint[] { 1252 },
        [3673] = new uint[] { 1252 },
        [3674] = new uint[] { 1252 },
    };
    private Settings config = new();
    private AchievementTrackerProfileData profile = new();

    private sealed class Settings
    {
        public bool DefaultShowOverlay = true;
        public bool DefaultShowCompleted = true;
        public float DefaultOpacity = 0.92f;
    }
    private readonly Dictionary<uint, AchievementProgress> progress = new();
    private readonly Queue<uint> requests = new();
    private Dalamud.Hooking.Hook<ReceiveAchievementProgressDelegate>? progressHook;
    private string currentRegion = "";
    private uint currentTerritoryId;
    private readonly Dictionary<int, string> achievementSearch = new();
    private readonly Dictionary<int, string> territorySearch = new();
    private AchievementGoal draftGoal = new();
    private bool draftExpanded = true;
    private const long ProgressRequestSpacingMs = 500;
    private const long ProgressRefreshIntervalMs = 5000;
    private const long ProgressRequestTimeoutMs = 5000;
    private const long TerritoryRefreshDelayMs = 1500;
    private long nextProgressRequestAt;
    private long nextProgressRefreshAt;
    private long progressRequestDeadline;
    private long territoryRefreshAt;
    private uint progressRequestInFlight;
    private bool territoryRefreshPending;
    private ulong activeContentId;
    private bool nativeWatchlistImported;
    private long nativeWatchlistImportAfter;
    private long nextNativeWatchlistPoll;
    private string nativeWatchlistSnapshot = "";
    private bool legacyMigrationPending;

    private delegate void ReceiveAchievementProgressDelegate(FfxivAchievement* achievement, uint id, uint current, uint max);
    private const string OmniCommand = AchievementTrackerCommandRoute.Command;
    private const string OmniOverlayCommand = AchievementTrackerCommandRoute.Alias;
    public override ModuleInfo Info { get; } = new()
    {
        Author = "Yuzi", Category = ModuleCategory.Item,
        Commands = new[]
        {
            new ModuleCommand("ToggleOverlayAliasCommand", $"{OmniCommand} {OmniOverlayCommand}"),
        },
    };
    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(new Dictionary<string, string>
    {
        ["Title"] = "成就追踪",
        ["Description"] = "读取游戏原生成就进度，按角色保存追踪目标与显示设置。",
        ["ToggleOverlayAliasCommand"] = "打开或关闭游戏内成就追踪悬浮窗",
        ["NewCharacterDefaults"] = "新角色默认设置",
        ["DefaultShowOverlay"] = "新角色默认显示悬浮窗",
        ["DefaultShowCompleted"] = "新角色的目标默认完成后仍显示",
        ["DefaultOpacity"] = "新角色默认窗口透明度",
        ["ProfileReadOnly"] = "角色档案读取失败或版本不兼容，已暂停修改和写入以保护原文件。请查看日志及 .bak 备份。",
        ["SaveFailed"] = "角色档案保存失败，当前修改仍在内存中。请查看日志，确认配置目录可写。",
        ["WaitingForCharacter"] = "角色信息加载中…",
        ["AchievementName"] = "成就名称",
        ["ConditionAndProgress"] = "达成条件与进度",
        ["Note"] = "备注",
        ["ShowCompleted"] = "完成后仍显示",
        ["SaveGoal"] = "保存目标",
        ["ClearDraft"] = "清空编辑",
        ["Delete"] = "删除",
        ["NoGoals"] = "暂无目标。可使用上方搜索添加，或加入游戏原生目标列表后自动同步。",
        ["ShowOverlay"] = "显示游戏内窗口",
        ["ClearAll"] = "清空全部",
        ["RefreshProgress"] = "重新读取进度",
        ["RefreshHelp"] = "同时刷新游戏原生目标列表",
        ["SelectAchievement"] = "选择成就…",
        ["UnmatchedName"] = "待匹配名称，可在上方搜索框重新选择",
        ["SearchAchievement"] = "搜索成就中文名或 ID",
        ["Category"] = "分类",
        ["SelectAchievementHelp"] = "选择成就后自动读取目标条件和进度",
        ["InvalidAchievement"] = "无效成就 ID",
        ["ReadingProgress"] = "正在读取成就进度…",
        ["NoCounter"] = "此成就没有进度计数器",
        ["IgnoreRegion"] = "不限制地区",
        ["AddCurrentTerritory"] = "添加当前地区",
        ["AddCurrentTerritoryHelp"] = "添加角色所在地区，并自动取消“不限制地区”",
        ["CurrentTerritoryUnavailable"] = "当前地区尚未就绪",
        ["SearchTerritory"] = "搜索区域、地区中文名或 ID",
        ["Select"] = "选择",
        ["RegionGroup"] = "区域",
        ["Territory"] = "地区",
        ["ID"] = "ID",
        ["FinishSelection"] = "完成选择",
        ["DragUpOnly"] = "仅可将目标向上拖动",
        ["DraftExpanded"] = "▼ 手动添加目标成就",
        ["DraftCollapsed"] = "▶ 手动添加目标成就",
        ["DraftSaveHelp"] = "完成设置后点击保存",
        ["ExpandHelp"] = "点击展开",
        ["Uncategorized"] = "未分类",
        ["NoAchievement"] = "未选择成就",
        ["TrackedAchievements"] = "目标成就",
        ["UnmatchedGoal"] = "待匹配成就：请在模块设置中选择原生成就",
        ["Conditions"] = "达成条件：",
        ["Completed"] = "已完成",
        ["InProgress"] = "进行中",
        ["GoalCard"] = "目标 {0} · {1}",
        ["AchievementID"] = "ID：{0}",
        ["CategoryValue"] = "分类：{0}",
        ["InvalidAchievementID"] = "无效成就 ID：{0}",
        ["ConditionValue"] = "达成条件：{0}",
        ["ReadingTarget"] = "目标：{0}（正在读取当前进度…）",
        ["TargetWithoutCounter"] = "目标：{0}（游戏未返回当前计数）",
        ["SelectTerritory"] = "选择地区…",
        ["TerritoryCount"] = "已选择 {0} 个地区",
        ["GoalNumber"] = "目标 {0}",
        ["GoalWithoutTerritory"] = "{0} · 未选择地区",
        ["GoalTerritoryCount"] = "{0} · {1} 个地区",
        ["TargetValue"] = "目标：{0}",
    });

    private string L(string key, params object[] arguments)
    {
        var text = OmniLoc.Get(this, key);
        return arguments.Length == 0 ? text : string.Format(text, arguments);
    }

    public override bool HasSettings => true;

    public override bool ResetSettings()
    {
        if (config.DefaultShowOverlay && config.DefaultShowCompleted && config.DefaultOpacity == 0.92f) return false;
        config = new Settings();
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        var command = arguments.Trim();
        if (!string.Equals(command, OmniOverlayCommand, StringComparison.Ordinal)) return false;
        ToggleOverlay();
        return true;
    }

    private void ToggleOverlay()
    {
        EnsureCharacterProfile();
        if (activeContentId == 0 || profileReadOnly) return;
        profile.ShowOverlay = !profile.ShowOverlay;
        if (profile.ShowOverlay)
        {
            QueueProgressRequests();
            if (territoryRefreshPending)
                territoryRefreshAt = Environment.TickCount64;
        }
        else
        {
            requests.Clear();
        }
        SaveConfig();
    }

    private readonly AchievementTrackerProfileStore storage = new((error, path) =>
        DalamudServices.PluginLog.Error(error, $"成就追踪：角色档案写入失败: {path}"));
    private bool storageLifetimeRegistered;
    private bool profileReadOnly;
    private ulong legacyMigrationOwnerContentId;

    private void EnsureStorageLifetime()
    {
        if (storageLifetimeRegistered) return;
        InstanceLifetime.AddAsync(() => new ValueTask(storage.FlushAsync()));
        storageLifetimeRegistered = true;
    }

    protected override void OnStart(FeatureLifetime activation)
    {
        EnsureStorageLifetime();
        activation.AddAsync(() => new ValueTask(storage.FlushAsync()));
        var hook = DService.Instance().Hook.HookFromMemberFunction<ReceiveAchievementProgressDelegate>(
            typeof(FfxivAchievement.MemberFunctionPointers), "ReceiveAchievementProgress", ReceiveAchievementProgress);
        progressHook = hook;
        activation.Add(hook);
        activation.OnStopping(hook.Disable);
        hook.Enable();
        var framework = DalamudServices.Framework;
        activation.OnStopping(() => framework.Update -= OnFrameworkUpdate);
        framework.Update += OnFrameworkUpdate;
        var clientState = DService.Instance().ClientState;
        activation.OnStopping(() => clientState.TerritoryChanged -= OnTerritoryChanged);
        clientState.TerritoryChanged += OnTerritoryChanged;
        var uiBuilder = DalamudServices.PluginInterface.UiBuilder;
        activation.OnStopping(() => uiBuilder.Draw -= DrawOverlay);
        uiBuilder.Draw += DrawOverlay;
        UpdateTerritory();
        EnsureCharacterProfile();
        activation.Add(AchievementTrackerCommandRoute.Attach(DalamudServices.CommandManager, ToggleOverlay));
        DalamudServices.PluginLog.Information("成就追踪：已注册 /omni 成就追踪悬浮窗");
    }

    protected override void OnStop()
    {
        SaveConfig();
        progressHook = null;
        progress.Clear();
        requests.Clear();
        nextProgressRequestAt = 0;
        nextProgressRefreshAt = 0;
        progressRequestDeadline = 0;
        territoryRefreshAt = 0;
        territoryRefreshPending = false;
        progressRequestInFlight = 0;
        activeContentId = 0;
        nativeWatchlistImported = false;
        nextNativeWatchlistPoll = 0;
        nativeWatchlistSnapshot = "";
    }

    private bool DrawDefaultSettings()
    {
        if (!ImGui.CollapsingHeader(L("NewCharacterDefaults"))) return false;
        var changed = ImGui.Checkbox(L("DefaultShowOverlay"), ref config.DefaultShowOverlay);
        changed |= ImGui.Checkbox(L("DefaultShowCompleted"), ref config.DefaultShowCompleted);
        ImGui.SliderFloat(L("DefaultOpacity"), ref config.DefaultOpacity, 0.1f, 1f, "%.2f");
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        return changed;
    }

    public override bool DrawSettings()
    {
        EnsureStorageLifetime();
        var defaultsChanged = DrawDefaultSettings();
        EnsureCharacterProfile();
        if (activeContentId == 0)
        {
            ImGui.TextDisabled(L("WaitingForCharacter"));
            return defaultsChanged;
        }

        if (profileReadOnly) ImGui.TextWrapped(L("ProfileReadOnly"));
        if (storage.GetError(ProfileConfigPath) is not null) ImGui.TextWrapped(L("SaveFailed"));
        ImGui.BeginDisabled(profileReadOnly);
        NormalizeExpandedCards();
        var changed = false;
        var controlHeight = ImGui.GetFrameHeight();

        if (ImGui.BeginTable("##add-goal-panel", 1,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH
                | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
            if (DrawDraftHeader(draftExpanded, controlHeight))
            {
                draftExpanded = !draftExpanded;
                if (draftExpanded) CollapseGoalCards();
                changed = true;
            }

            if (draftExpanded)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled(L("AchievementName"));
                changed |= DrawAchievementPicker(-1, draftGoal, false);

                if (draftGoal.Id > 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextDisabled(L("ConditionAndProgress"));
                    DrawAchievementProgress(draftGoal);

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    changed |= DrawTerritoryPicker(-1, draftGoal);

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextDisabled(L("Note"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##draft-note", ref draftGoal.Note, 128);

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    changed |= ImGui.Checkbox(L("ShowCompleted"), ref draftGoal.ShowCompleted);

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var actionWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
                    if (ImGui.Button(L("SaveGoal"), new System.Numerics.Vector2(actionWidth, controlHeight)))
                    {
                        profile.Goals.Add(new AchievementGoal
                        {
                            Id = draftGoal.Id,
                            Name = draftGoal.Name,
                            Region = draftGoal.Region,
                            RegionGroup = draftGoal.RegionGroup,
                            RegionId = draftGoal.RegionId,
                            Note = draftGoal.Note,
                            Enabled = draftGoal.Enabled,
                            IgnoreRegion = draftGoal.IgnoreRegion,
                            ShowCompleted = draftGoal.ShowCompleted,
                            Regions = draftGoal.Regions.Select(x => x.Clone()).ToList(),
                            Expanded = true,
                        });
                        draftGoal = new AchievementGoal { ShowCompleted = profile.ShowCompleted };
                        draftExpanded = false;
                        achievementSearch.Remove(-1);
                        territorySearch.Remove(-1);
                        changed = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(L("ClearDraft"), new System.Numerics.Vector2(actionWidth, controlHeight)))
                    {
                        draftGoal = new AchievementGoal { ShowCompleted = profile.ShowCompleted };
                        achievementSearch.Remove(-1);
                        territorySearch.Remove(-1);
                        changed = true;
                    }
                }
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        for (var i = 0; i < profile.Goals.Count; i++)
        {
            var goal = profile.Goals[i];
            ImGui.PushID(i);
            if (ImGui.BeginTable("##goal-card", 1,
                    ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH
                    | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
            {
                var headerHeight = controlHeight + ImGui.GetStyle().FramePadding.Y * 2;
                ImGui.TableNextRow(ImGuiTableRowFlags.None, headerHeight);
                ImGui.TableSetColumnIndex(0);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
                var headerTop = ImGui.GetCursorPosY();
                ImGui.SetCursorPosY(headerTop + (headerHeight - controlHeight) * 0.5f);
                changed |= DrawGoalDragHandle(i);
                ImGui.SameLine();
                ImGui.SetCursorPosY(headerTop + (headerHeight - controlHeight) * 0.5f);
                changed |= ImGui.Checkbox("##enabled", ref goal.Enabled);
                ImGui.SameLine();
                var deleteWidth = ImGui.CalcTextSize(L("Delete")).X + ImGui.GetStyle().FramePadding.X * 2;
                var titleWidth = MathF.Max(80,
                    ImGui.GetContentRegionAvail().X
                    - ImGui.GetStyle().ItemSpacing.X
                    - deleteWidth);
                ImGui.SetCursorPosY(headerTop + (headerHeight - controlHeight) * 0.5f);
                if (ImGui.Selectable(
                        L("GoalCard", i + 1, GetGoalSummary(goal)) + "##goal-toggle",
                        goal.Expanded,
                        ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(titleWidth, controlHeight)))
                {
                    var expand = !goal.Expanded;
                    CollapseGoalCards();
                    goal.Expanded = expand;
                    if (expand) draftExpanded = false;
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.SetCursorPosY(headerTop + (headerHeight - controlHeight) * 0.5f);
                var remove = ImGui.Button(L("Delete"), new System.Numerics.Vector2(deleteWidth, controlHeight));
                if (remove)
                {
                    profile.Goals.RemoveAt(i);
                    changed = true;
                    ImGui.EndTable();
                    ImGui.PopID();
                    break;
                }

                if (goal.Expanded)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Indent(12);
                    ImGui.TextDisabled(L("AchievementName"));
                    changed |= DrawAchievementPicker(i, goal);

                    ImGui.Spacing();
                    ImGui.TextDisabled(L("ConditionAndProgress"));
                    ImGui.Indent(12);
                    DrawAchievementProgress(goal);
                    ImGui.Unindent(12);

                    ImGui.Spacing();
                    ImGui.Indent(12);
                    changed |= DrawTerritoryPicker(i, goal);
                    ImGui.Unindent(12);

                    ImGui.Spacing();
                    ImGui.TextDisabled(L("Note"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("##note", ref goal.Note, 128);
                    changed |= ImGui.IsItemDeactivatedAfterEdit();

                    ImGui.Spacing();
                    changed |= ImGui.Checkbox(L("ShowCompleted"), ref goal.ShowCompleted);
                    ImGui.Unindent(12);
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            ImGui.PopID();
        }
        if (profile.Goals.Count == 0)
            ImGui.TextDisabled(L("NoGoals"));

        if (ImGui.BeginTable("##display-options", 2,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH
                | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            changed |= ImGui.Checkbox(L("ShowOverlay"), ref profile.ShowOverlay);
            ImGui.TableSetColumnIndex(1);
            if (profile.Goals.Count > 0)
            {
                var clearWidth = ImGui.CalcTextSize(L("ClearAll")).X + ImGui.GetStyle().FramePadding.X * 2;
                var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - clearWidth;
                ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), right));
                if (ImGui.Button(L("ClearAll"), new System.Numerics.Vector2(clearWidth, controlHeight)))
                {
                    profile.Goals.Clear();
                    progress.Clear();
                    changed = true;
                }
            }
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Button(L("RefreshProgress"), new System.Numerics.Vector2(0, controlHeight)))
            {
                RefreshNativeWatchlistAndProgress();
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled(L("RefreshHelp"));
            ImGui.EndTable();
        }
        ImGui.EndDisabled();
        if (changed) { SaveConfig(); QueueProgressRequests(); }
        return defaultsChanged;
    }

    private bool DrawAchievementPicker(int index, AchievementGoal goal, bool showMetadata = true)
    {
        var changed = false;
        var popupId = $"AchievementPicker-{index}";
        achievementSearch.TryGetValue(index, out var query);
        query ??= goal.Name ?? "";
        var row = goal.Id > 0
            ? DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>().GetRowOrDefault((uint)goal.Id)
            : null;
        var name = row.HasValue ? row.Value.Name.ToString() : goal.Name;
        if (string.IsNullOrWhiteSpace(name)) name = L("SelectAchievement");

        if (ImGui.Button($"{name}##achievement-picker", new System.Numerics.Vector2(-1, 0)))
        {
            query = "";
            achievementSearch[index] = query;
            ImGui.OpenPopup(popupId);
        }
        if (showMetadata && row.HasValue)
        {
            ImGui.TextDisabled(L("AchievementID", row.Value.RowId));
            ImGui.SameLine();
            ImGui.TextDisabled(L("CategoryValue", GetAchievementCategory(row.Value)));
        }
        else if (showMetadata && goal.Id > 0)
        {
            ImGui.TextDisabled(L("InvalidAchievementID", goal.Id));
        }
        else if (showMetadata && !string.IsNullOrWhiteSpace(goal.Name))
        {
            ImGui.TextDisabled(L("UnmatchedName"));
        }

        var popupOpen = ImGui.IsPopupOpen(popupId);
        var matches = popupOpen
            ? DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>()
                .Where(candidate => MatchesSearch(query, candidate.RowId, candidate.Name.ToString(), GetAchievementCategory(candidate)))
                .Take(200)
                .ToList()
            : new List<LuminaAchievement>();
        var childHeight = popupOpen ? SearchResultsHeight(matches.Count, 280) : 280;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, childHeight + 60), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(360, 120), new System.Numerics.Vector2(700, 560));
        if (ImGui.BeginPopup(popupId))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##achievement-search", L("SearchAchievement"), ref query, 128);
            achievementSearch[index] = query;

            if (ImGui.BeginChild("##achievement-results", new System.Numerics.Vector2(-1, childHeight), false))
            {
                if (ImGui.BeginTable("##achievement-result-table", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn(L("AchievementName"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
                    ImGui.TableSetupColumn(L("Category"), ImGuiTableColumnFlags.WidthStretch, 1.0f);
                    ImGui.TableHeadersRow();
                    foreach (var candidate in matches)
                    {
                        var candidateName = candidate.Name.ToString();
                        var category = GetAchievementCategory(candidate);
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        if (ImGui.Selectable($"{candidateName}##achievement-{index}-{candidate.RowId}", goal.Id == candidate.RowId))
                        {
                            SetAchievement(goal, candidate);
                            if (index >= 0)
                            {
                                CollapseGoalCards();
                                goal.Expanded = true;
                                draftExpanded = false;
                            }
                            ImGui.CloseCurrentPopup();
                            changed = true;
                        }
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextDisabled(category);
                    }
                    ImGui.EndTable();
                }
                ImGui.EndChild();
            }
            ImGui.EndPopup();
        }

        return changed;
    }

    private void DrawAchievementProgress(AchievementGoal goal)
    {
        if (goal.Id <= 0)
        {
            ImGui.TextDisabled(L("SelectAchievementHelp"));
            return;
        }

        var row = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>().GetRowOrDefault((uint)goal.Id);
        if (!row.HasValue)
        {
            ImGui.TextDisabled(L("InvalidAchievement"));
            return;
        }

        var description = row.Value.Description.ToString();
        var prerequisiteIds = GetAchievementPrerequisiteIds(row.Value);
        if (IsAchievementPrerequisiteDescription(description) && prerequisiteIds.Count > 0)
        {
            DrawAchievementPrerequisiteList(prerequisiteIds);
            return;
        }

        var declaredTarget = GetDeclaredAchievementTarget(row.Value);
        var hasSingleCompletionTarget = declaredTarget == 1;
        if (hasSingleCompletionTarget)
        {
            DrawWrappedOverlayText(L("ConditionValue", description));
            DrawAchievementStatus(IsAchievementComplete((uint)goal.Id));
            return;
        }

        ImGui.TextWrapped(description);
        if (!progress.TryGetValue((uint)goal.Id, out var state))
        {
            ImGui.TextDisabled(declaredTarget > 0
                ? L("ReadingTarget", declaredTarget)
                : L("ReadingProgress"));
            return;
        }

        if (state.Max == 0)
        {
            ImGui.TextDisabled(declaredTarget > 0
                ? L("TargetWithoutCounter", declaredTarget)
                : L("NoCounter"));
            return;
        }

        var ratio = state.Max == 0 ? 0 : Math.Clamp(state.Current / (float)state.Max, 0, 1);
        ImGui.ProgressBar(ratio, new System.Numerics.Vector2(-1, 0), $"{state.Current} / {state.Max} ({ratio:P0})");
    }

    private bool DrawTerritoryPicker(int index, AchievementGoal goal)
    {
        MigrateLegacyRegion(goal);
        var changed = ImGui.Checkbox(L("IgnoreRegion"), ref goal.IgnoreRegion);

        ImGui.SameLine();
        var clientState = DService.Instance().ClientState;
        var currentTerritory = clientState.IsLoggedIn
            ? DalamudServices.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(clientState.TerritoryType)
            : null;
        var currentArea = currentTerritory.HasValue
            ? currentTerritory.Value.PlaceName.ValueNullable?.Name.ToString() ?? ""
            : "";
        var canAddCurrentTerritory = currentTerritory.HasValue
            && currentTerritory.Value.RowId > 0
            && !string.IsNullOrWhiteSpace(currentArea);
        ImGui.BeginDisabled(!canAddCurrentTerritory);
        if (ImGui.Button(L("AddCurrentTerritory"), new System.Numerics.Vector2(0, ImGui.GetFrameHeight()))
            && canAddCurrentTerritory)
        {
            var row = currentTerritory!.Value;
            var regionId = (int)row.RowId;
            var alreadySelected = goal.Regions.Any(region => region.Id == regionId);
            if (!alreadySelected)
            {
                goal.Regions.Add(new AchievementRegion
                {
                    Id = regionId,
                    Group = row.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "",
                    Name = currentArea,
                });
            }
            changed |= goal.IgnoreRegion || !alreadySelected;
            goal.IgnoreRegion = false;
            SyncLegacyRegionFields(goal);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L(canAddCurrentTerritory ? "AddCurrentTerritoryHelp" : "CurrentTerritoryUnavailable"));

        if (goal.IgnoreRegion) return changed;

        ImGui.SameLine();

        var popupId = $"TerritoryPicker-{index}";
        territorySearch.TryGetValue(index, out var query);
        query ??= "";

        var label = GetRegionSelectionSummary(goal, index);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Button(label, new System.Numerics.Vector2(-1, ImGui.GetFrameHeight()))) ImGui.OpenPopup(popupId);

        var popupOpen = ImGui.IsPopupOpen(popupId);
        var matches = popupOpen
            ? DalamudServices.DataManager.GetExcelSheet<TerritoryType>()
                .Select(candidate => new TerritorySearchResult(
                    candidate,
                    candidate.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "",
                    candidate.PlaceName.ValueNullable?.Name.ToString() ?? ""))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Area)
                    && MatchesSearch(query, candidate.Row.RowId, candidate.Group, candidate.Area))
                .ToList()
            : new List<TerritorySearchResult>();
        var childHeight = popupOpen ? SearchResultsHeight(matches.Count, 240) : 240;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, childHeight + 60), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(360, 120), new System.Numerics.Vector2(700, 520));
        if (ImGui.BeginPopup(popupId))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##territory-search", L("SearchTerritory"), ref query, 128);
            territorySearch[index] = query ?? "";
            if (ImGui.BeginChild("##territory-results", new System.Numerics.Vector2(-1, childHeight), false))
            {
                if (ImGui.BeginTable("##territory-result-table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn(L("Select"), ImGuiTableColumnFlags.WidthFixed, 48);
                    ImGui.TableSetupColumn(L("RegionGroup"), ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn(L("Territory"), ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn(L("ID"), ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableHeadersRow();
                    foreach (var candidate in matches)
                    {
                        var group = candidate.Group;
                        var area = candidate.Area;
                        var regionId = (int)candidate.Row.RowId;
                        var selected = goal.Regions.Any(x => x.Id == regionId);
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        if (ImGui.Checkbox($"##territory-{index}-{regionId}", ref selected))
                        {
                            if (selected)
                            {
                                goal.Regions.Add(new AchievementRegion
                                {
                                    Id = regionId,
                                    Group = group,
                                    Name = area,
                                });
                            }
                            else
                            {
                                goal.Regions.RemoveAll(x => x.Id == regionId);
                            }
                            SyncLegacyRegionFields(goal);
                            changed = true;
                        }
                        ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(group);
                        ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(area);
                        ImGui.TableSetColumnIndex(3); ImGui.TextDisabled(candidate.Row.RowId.ToString());
                    }
                    ImGui.EndTable();
                }
                ImGui.EndChild();
            }
            ImGui.Spacing();
            if (ImGui.Button(L("FinishSelection"), new System.Numerics.Vector2(-1, ImGui.GetFrameHeight())))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        return changed;
    }

    private static bool MatchesSearch(string? query, uint rowId, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var text = query.Trim();
        if (rowId.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        return values.Any(value => value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static float SearchResultsHeight(int resultCount, float maximumHeight)
    {
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        var desiredHeight = (resultCount + 1) * rowHeight + ImGui.GetStyle().ItemSpacing.Y;
        return Math.Clamp(desiredHeight, rowHeight * 2, maximumHeight);
    }

    private string GetRegionSelectionSummary(AchievementGoal goal, int index)
    {
        if (goal.Regions.Count == 0)
            return L("SelectTerritory") + $"##territory-picker-{index}";
        if (goal.Regions.Count == 1)
            return $"{goal.Regions[0].Group} / {goal.Regions[0].Name}##territory-picker-{index}";
        return L("TerritoryCount", goal.Regions.Count) + $"##territory-picker-{index}";
    }

    private static void MigrateLegacyRegion(AchievementGoal goal)
        => AchievementTrackerProfileJson.MigrateLegacyRegion(goal);

    private static void SyncLegacyRegionFields(AchievementGoal goal)
    {
        var first = goal.Regions.FirstOrDefault();
        if (first == null)
        {
            goal.RegionId = 0;
            goal.RegionGroup = "";
            goal.Region = "";
            return;
        }

        goal.RegionId = first.Id;
        goal.RegionGroup = first.Group;
        goal.Region = first.Name;
    }

    private bool DrawGoalDragHandle(int index)
    {
        var controlHeight = ImGui.GetFrameHeight();
        ImGui.Button("≡", new System.Numerics.Vector2(controlHeight, controlHeight));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(L("DragUpOnly"));

        if (ImGui.BeginDragDropSource())
        {
            var payload = BitConverter.GetBytes(index);
            ImGui.SetDragDropPayload("ACHIEVEMENT_GOAL", payload);
            ImGui.TextUnformatted(L("GoalNumber", index + 1));
            ImGui.EndDragDropSource();
        }

        var changed = false;
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("ACHIEVEMENT_GOAL");
            if (!payload.IsNull && payload.IsDelivery() && payload.DataSize >= sizeof(int))
            {
                var sourceIndex = System.Runtime.InteropServices.Marshal.ReadInt32((nint)payload.Data);
                if (sourceIndex > index && sourceIndex < profile.Goals.Count)
                {
                    var moved = profile.Goals[sourceIndex];
                    profile.Goals.RemoveAt(sourceIndex);
                    profile.Goals.Insert(index, moved);
                    achievementSearch.Clear();
                    territorySearch.Clear();
                    changed = true;
                }
            }
            ImGui.EndDragDropTarget();
        }

        return changed;
    }

    private static void SetAchievement(AchievementGoal goal, LuminaAchievement row)
    {
        var changedAchievement = goal.Id != (int)row.RowId;
        if (changedAchievement)
        {
            goal.IgnoreRegion = true;
            goal.Regions.Clear();
            SyncLegacyRegionFields(goal);
        }

        goal.Id = (int)row.RowId;
        goal.Name = row.Name.ToString();
        goal.Enabled = true;
        if (changedAchievement)
            ApplyRegionPreset(goal, row);
    }

    private static void ApplyRegionPreset(AchievementGoal goal, LuminaAchievement row)
    {
        var regions = GetExplicitAchievementRegions(row);
        if (regions.Count is 0 or > 10) return;

        goal.IgnoreRegion = false;
        goal.Regions = regions;
        SyncLegacyRegionFields(goal);
    }

    private static List<AchievementRegion> GetExplicitAchievementRegions(LuminaAchievement row)
    {
        if (OfflineRegionSupplements.TryGetValue(row.RowId, out var territoryIds))
        {
            var distinctIds = territoryIds.Distinct().ToList();
            if (distinctIds.Count is 0 or > 10) return new List<AchievementRegion>();

            var territorySheet = DalamudServices.DataManager.GetExcelSheet<TerritoryType>();
            var supplementedRegions = new List<AchievementRegion>(distinctIds.Count);
            foreach (var territoryId in distinctIds)
            {
                var territoryRow = territorySheet.GetRowOrDefault(territoryId);
                if (!territoryRow.HasValue) return new List<AchievementRegion>();

                var supplementedGroup = territoryRow.Value.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "";
                var supplementedArea = territoryRow.Value.PlaceName.ValueNullable?.Name.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(supplementedArea)) return new List<AchievementRegion>();

                supplementedRegions.Add(new AchievementRegion
                {
                    Id = (int)territoryId,
                    Group = supplementedGroup,
                    Name = supplementedArea,
                });
            }

            return supplementedRegions;
        }

        // Achievement type 8 points directly to a Map row that resolves to one TerritoryType.
        if (row.Type != 8) return new List<AchievementRegion>();

        var map = DalamudServices.DataManager.GetExcelSheet<LuminaMap>().GetRowOrDefault(row.Key.RowId);
        if (!map.HasValue) return new List<AchievementRegion>();

        var territory = map.Value.TerritoryType.ValueNullable;
        if (!territory.HasValue || territory.Value.RowId == 0)
            return new List<AchievementRegion>();

        var group = territory.Value.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "";
        var area = territory.Value.PlaceName.ValueNullable?.Name.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(area)) return new List<AchievementRegion>();

        return new List<AchievementRegion>
        {
            new()
            {
                Id = (int)territory.Value.RowId,
                Group = group,
                Name = area,
            },
        };
    }

    private bool DrawDraftHeader(bool expanded, float height)
    {
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##draft-toggle", new System.Numerics.Vector2(-1, height));
        var end = ImGui.GetItemRectMax();
        var label = expanded ? L("DraftExpanded") : L("DraftCollapsed");
        var hint = expanded ? L("DraftSaveHelp") : L("ExpandHelp");
        ImGui.SetCursorScreenPos(start + new System.Numerics.Vector2(8, (height - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.TextDisabled(hint);
        ImGui.SetCursorScreenPos(new System.Numerics.Vector2(start.X, end.Y));
        return clicked;
    }

    private void CollapseGoalCards()
    {
        foreach (var goal in profile.Goals)
            goal.Expanded = false;
    }

    private void NormalizeExpandedCards()
    {
        if (draftExpanded)
        {
            CollapseGoalCards();
            return;
        }

        var foundExpanded = false;
        foreach (var goal in profile.Goals.Where(goal => goal.Expanded))
        {
            if (foundExpanded)
                goal.Expanded = false;
            else
                foundExpanded = true;
        }
    }

    private string GetAchievementCategory(LuminaAchievement row)
    {
        var category = row.AchievementCategory.ValueNullable;
        if (!category.HasValue) return L("Uncategorized");

        var categoryName = category.Value.Name.ToString();
        var kindName = category.Value.AchievementKind.ValueNullable?.Name.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(kindName)) return string.IsNullOrWhiteSpace(categoryName) ? L("Uncategorized") : categoryName;
        if (string.IsNullOrWhiteSpace(categoryName)) return kindName;
        return $"{kindName} - {categoryName}";
    }

    private static uint GetDeclaredAchievementTarget(LuminaAchievement row)
    {
        try
        {
            var target = row.Data[0].RowId;
            return target;
        }
        catch
        {
            return 0;
        }
    }

    private string GetGoalSummary(AchievementGoal goal)
    {
        MigrateLegacyRegion(goal);
        var name = string.IsNullOrWhiteSpace(goal.Name) ? L("NoAchievement") : goal.Name;
        if (goal.IgnoreRegion) return name;
        if (goal.Regions.Count == 0) return L("GoalWithoutTerritory", name);
        if (goal.Regions.Count == 1) return $"{name} · {goal.Regions[0].Name}";
        return L("GoalTerritoryCount", name, goal.Regions.Count);
    }

    private readonly record struct TerritorySearchResult(TerritoryType Row, string Group, string Area);

    private void DrawOverlay()
    {
        if (activeContentId == 0 || !profile.ShowOverlay) return;
        ImGui.SetNextWindowBgAlpha(profile.Opacity);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(380, 360), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new System.Numerics.Vector2(280, 120),
            new System.Numerics.Vector2(480, 720));
        var flags = ImGuiWindowFlags.NoTitleBar;
        if (!ImGui.Begin("##AchievementTrackerOverlay", flags)) { ImGui.End(); return; }
        ImGui.TextUnformatted(L("TrackedAchievements"));
        foreach (var goal in profile.Goals)
        {
            MigrateLegacyRegion(goal);
            var regionAllowed = goal.IgnoreRegion
                || goal.Regions.Any(region =>
                    (region.Id > 0 && region.Id == currentTerritoryId)
                    || (region.Id <= 0 && string.Equals(region.Name, currentRegion, StringComparison.OrdinalIgnoreCase)));
            if (!goal.Enabled || !regionAllowed) continue;
            if (goal.Id <= 0)
            {
                ImGui.Separator();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(goal.Name) ? L("NoAchievement") : goal.Name);
                ImGui.TextDisabled(L("UnmatchedGoal"));
                if (!string.IsNullOrWhiteSpace(goal.Note)) ImGui.TextDisabled(goal.Note);
                continue;
            }
            var row = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>().GetRowOrDefault((uint)goal.Id);
            var hasProgressResponse = progress.TryGetValue((uint)goal.Id, out var state);
            var declaredTarget = row.HasValue ? GetDeclaredAchievementTarget(row.Value) : 0;
            var hasProgressCounter = hasProgressResponse && state.Max > 0;
            var description = row.HasValue ? row.Value.Description.ToString() : "";
            IReadOnlyList<uint> prerequisiteIds = row.HasValue ? GetAchievementPrerequisiteIds(row.Value) : Array.Empty<uint>();
            var hasPrerequisiteList = row.HasValue
                && IsAchievementPrerequisiteDescription(description)
                && prerequisiteIds.Count > 0;
            var hasSingleCompletionTarget = row.HasValue
                && (declaredTarget == 1 || (hasProgressCounter && state.Max == 1));
            var showCondition = row.HasValue && (hasPrerequisiteList || hasSingleCompletionTarget);
            var completed = hasSingleCompletionTarget
                ? IsAchievementComplete((uint)goal.Id)
                : hasProgressCounter && state.Current >= state.Max;
            if (!goal.ShowCompleted && completed) continue;
            ImGui.Separator();
            ImGui.TextUnformatted(row.HasValue ? row.Value.Name.ToString() : (string.IsNullOrWhiteSpace(goal.Name) ? L("InvalidAchievementID", goal.Id) : goal.Name));
            if (!string.IsNullOrWhiteSpace(goal.Note))
                ImGui.TextDisabled(goal.Note);

            if (hasPrerequisiteList)
            {
                DrawAchievementPrerequisiteList(prerequisiteIds);
                continue;
            }

            if (showCondition)
                DrawWrappedOverlayText(L("ConditionValue", description));

            if (hasSingleCompletionTarget)
            {
                DrawAchievementStatus(completed);
                continue;
            }

            if (!hasProgressCounter)
            {
                if (showCondition)
                {
                    if (!hasProgressResponse)
                        ImGui.TextDisabled(L("ReadingProgress"));
                }
                else if (hasProgressResponse && row.HasValue && declaredTarget == 0)
                {
                    DrawWrappedOverlayText(L("TargetValue", description));
                }
                else
                {
                    var progressStatus = !hasProgressResponse
                        ? declaredTarget > 0 ? L("ReadingTarget", declaredTarget) : L("ReadingProgress")
                        : declaredTarget > 0 ? L("TargetWithoutCounter", declaredTarget) : null;
                    if (progressStatus != null)
                    {
                        ImGui.TextDisabled(progressStatus);
                    }
                    else if (row.HasValue)
                    {
                        DrawWrappedOverlayText(L("TargetValue", description));
                    }
                }
                continue;
            }

            var ratio = Math.Clamp(state.Current / (float)state.Max, 0, 1);
            ImGui.ProgressBar(ratio, new System.Numerics.Vector2(-1, 0), $"{state.Current} / {state.Max} ({ratio:P0})");
            DrawAchievementStatus(completed);
        }
        ImGui.End();
    }

    private static bool IsAchievementPrerequisiteDescription(string description)
        => description.Contains("成就", StringComparison.Ordinal);

    private static List<uint> GetAchievementPrerequisiteIds(LuminaAchievement row)
    {
        if (row.Type != 2) return new List<uint>();

        var ids = new List<uint>();
        AddAchievementId(row.Key.RowId, ids);
        foreach (var reference in row.Data)
            AddAchievementId(reference.RowId, ids);
        return ids;
    }

    private static void AddAchievementId(uint id, ICollection<uint> ids)
    {
        if (id > 0 && !ids.Contains(id))
            ids.Add(id);
    }

    private void DrawAchievementPrerequisiteList(IReadOnlyList<uint> prerequisiteIds)
    {
        ImGui.TextDisabled(L("Conditions"));
        var achievement = FfxivAchievement.Instance();
        var completionLoaded = achievement != null
            && achievement->State == FfxivAchievement.AchievementState.Loaded;
        foreach (var id in prerequisiteIds)
        {
            var name = GetAchievementName(id);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var completed = completionLoaded && achievement->IsComplete((int)id);
            var marker = completed ? "✓" : completionLoaded ? "□" : "…";
            if (completed)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.3f, 1, 0.6f, 1), $"{marker} {name}");
            }
            else
            {
                ImGui.TextDisabled($"{marker} {name}");
            }
        }
    }

    private static bool IsAchievementComplete(uint achievementId)
    {
        var achievement = FfxivAchievement.Instance();
        return achievement != null
            && achievement->State == FfxivAchievement.AchievementState.Loaded
            && achievement->IsComplete((int)achievementId);
    }

    private void DrawAchievementStatus(bool completed)
        => ImGui.TextColored(
            completed ? new System.Numerics.Vector4(0.3f, 1, 0.6f, 1) : new System.Numerics.Vector4(1, .75f, .3f, 1),
            completed ? L("Completed") : L("InProgress"));

    private static void DrawWrappedOverlayText(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
    }

    private void QueueProgressRequests()
    {
        requests.Clear();
        var ids = profile.Goals
            .Where(x => x.Enabled && x.Id > 0 && NeedsProgressRequest((uint)x.Id))
            .Select(x => (uint)x.Id);
        if (draftGoal.Enabled && draftGoal.Id > 0 && NeedsProgressRequest((uint)draftGoal.Id))
            ids = ids.Append((uint)draftGoal.Id);
        foreach (var id in ids.Distinct())
            requests.Enqueue(id);
        if (profile.ShowOverlay)
            nextProgressRefreshAt = Environment.TickCount64 + ProgressRefreshIntervalMs;
    }

    private static bool NeedsProgressRequest(uint achievementId)
    {
        var row = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>()
            .GetRowOrDefault(achievementId);
        if (!row.HasValue) return true;

        var description = row.Value.Description.ToString();
        return row.Value.Type != 2
            && !IsAchievementPrerequisiteDescription(description)
            && GetDeclaredAchievementTarget(row.Value) != 1;
    }

    private void RefreshNativeWatchlistAndProgress()
    {
        progress.Clear();
        nativeWatchlistImported = false;
        nativeWatchlistImportAfter = Environment.TickCount64;
        nextNativeWatchlistPoll = 0;
        nativeWatchlistSnapshot = "";
        progressRequestInFlight = 0;
        progressRequestDeadline = 0;
        QueueProgressRequests();
        nextProgressRefreshAt = Environment.TickCount64 + ProgressRefreshIntervalMs;
        DalamudServices.PluginLog.Information("成就追踪：手动刷新已排队，将重新读取原生目标列表和成就进度");
    }

    private void AddGoalById(uint achievementId, bool expanded)
    {
        if (achievementId == 0 || activeContentId == 0 || profileReadOnly) return;
        var existing = profile.Goals.FirstOrDefault(goal => goal.Id == (int)achievementId);
        if (existing != null)
        {
            existing.Enabled = true;
            existing.Expanded = expanded;
            SaveConfig();
            QueueProgressRequests();
            return;
        }

        var addedGoal = new AchievementGoal
        {
            Id = (int)achievementId,
            Name = GetAchievementName(achievementId),
            ShowCompleted = profile.ShowCompleted,
            Enabled = true,
            IgnoreRegion = true,
            Region = "",
            Expanded = expanded,
        };
        var addedRow = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>()
            .GetRowOrDefault(achievementId);
        if (addedRow.HasValue)
            ApplyRegionPreset(addedGoal, addedRow.Value);
        profile.Goals.Add(addedGoal);
        SaveConfig();
        QueueProgressRequests();
    }

    private unsafe bool TryReadNativeWatchlist(out ulong listContentId, out ushort[] ids)
    {
        listContentId = 0;
        ids = Array.Empty<ushort>();
        var list = AchievementListModule.Instance();
        if (list == null) return false;

        var count = Math.Min((int)list->WatchlistCount, list->WatchList.Length);
        listContentId = list->CharacterContentId;
        ids = list->WatchList[..count].ToArray();
        return true;
    }

    private void SyncNativeWatchlist(string reason)
    {
        if (profileReadOnly) return;
        if (activeContentId == 0 || !TryReadNativeWatchlist(out var listContentId, out var ids))
        {
            if (reason != "framework")
                DalamudServices.PluginLog.Warning($"成就追踪：无法读取原生目标列表，reason={reason}");
            return;
        }

        // The game may leave this field at zero while the user-file module is
        // loading. A non-zero value belonging to another character is still
        // rejected so profiles cannot be mixed during character changes.
        if (listContentId != 0 && listContentId != activeContentId)
        {
            var mismatch = $"mismatch:{listContentId:X16}";
            if (nativeWatchlistSnapshot != mismatch)
            {
                nativeWatchlistSnapshot = mismatch;
                DalamudServices.PluginLog.Warning(
                    $"成就追踪：原生目标列表角色不匹配，当前={activeContentId:X16}, 列表={listContentId:X16}");
            }
            nativeWatchlistImported = false;
            return;
        }

        var snapshot = $"{listContentId:X16}|{string.Join(',', ids)}";
        if (snapshot == nativeWatchlistSnapshot && nativeWatchlistImported)
            return;

        nativeWatchlistSnapshot = snapshot;
        var existingIds = profile.Goals.Select(goal => (uint)goal.Id).ToHashSet();
        var imported = 0;
        foreach (var achievementId in ids)
        {
            if (achievementId == 0 || !existingIds.Add(achievementId)) continue;
            var importedGoal = new AchievementGoal
            {
                Id = achievementId,
                Name = GetAchievementName(achievementId),
                ShowCompleted = profile.ShowCompleted,
                Enabled = true,
                IgnoreRegion = true,
                Expanded = false,
            };
            var importedRow = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>()
                .GetRowOrDefault(achievementId);
            if (importedRow.HasValue)
                ApplyRegionPreset(importedGoal, importedRow.Value);
            profile.Goals.Add(importedGoal);
            imported++;
        }

        nativeWatchlistImported = true;
        if (imported > 0)
        {
            SaveConfig();
            QueueProgressRequests();
        }
        DalamudServices.PluginLog.Information(
            $"成就追踪：原生目标列表同步完成，reason={reason}, 当前角色={activeContentId:X16}, 列表角色={(listContentId == 0 ? "未提供" : listContentId.ToString("X16"))}, 原生目标={ids.Length}, 新同步={imported}, ID=[{string.Join(',', ids)}]");
    }

    private static string GetAchievementName(uint achievementId)
    {
        return DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>()
            .GetRowOrDefault(achievementId)?.Name.ToString() ?? "";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        EnsureCharacterProfile();
        var now = Environment.TickCount64;
        if (activeContentId != 0 && now >= nativeWatchlistImportAfter && now >= nextNativeWatchlistPoll)
        {
            nextNativeWatchlistPoll = now + 500;
            SyncNativeWatchlist("framework");
        }

        var achievement = FfxivAchievement.Instance();
        if (achievement != null && achievement->ProgressAchievementId != 0
            && achievement->ProgressRequestState == FfxivAchievement.AchievementState.Loaded)
        {
            progress[achievement->ProgressAchievementId] = new(achievement->ProgressCurrent, achievement->ProgressMax);
        }

        if (!profile.ShowOverlay || !DService.Instance().ClientState.IsLoggedIn || achievement == null)
            return;

        if (territoryRefreshPending && now >= territoryRefreshAt)
        {
            territoryRefreshPending = false;
            QueueProgressRequests();
            nextProgressRefreshAt = now + ProgressRefreshIntervalMs;
            DalamudServices.PluginLog.Debug("成就追踪：过图数据稳定，开始异步刷新成就进度");
        }

        if (now >= nextProgressRefreshAt && progressRequestInFlight == 0 && requests.Count == 0)
        {
            QueueProgressRequests();
            nextProgressRefreshAt = now + ProgressRefreshIntervalMs;
        }

        if (progressRequestInFlight != 0)
        {
            if (now >= progressRequestDeadline)
            {
                requests.Enqueue(progressRequestInFlight);
                progressRequestInFlight = 0;
                progressRequestDeadline = 0;
                nextProgressRequestAt = now + ProgressRequestSpacingMs;
                DalamudServices.PluginLog.Debug("成就追踪：成就进度请求超时，将稍后重试");
            }
            return;
        }

        if (requests.Count == 0 || now < nextProgressRequestAt)
            return;

        var id = requests.Dequeue();
        progressRequestInFlight = id;
        progressRequestDeadline = now + ProgressRequestTimeoutMs;
        try
        {
            achievement->RequestAchievementProgress(id);
        }
        catch (Exception exception)
        {
            progressRequestInFlight = 0;
            progressRequestDeadline = 0;
            nextProgressRequestAt = now + ProgressRequestSpacingMs;
            DalamudServices.PluginLog.Error(exception, $"成就追踪：请求成就进度失败，ID={id}");
        }
    }

    private void ReceiveAchievementProgress(FfxivAchievement* achievement, uint id, uint current, uint max)
    {
        progressHook!.Original(achievement, id, current, max);
        progress[id] = new(current, max);
        if (progressRequestInFlight == id)
        {
            progressRequestInFlight = 0;
            progressRequestDeadline = 0;
            nextProgressRequestAt = Environment.TickCount64 + ProgressRequestSpacingMs;
        }
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        UpdateTerritory();
        territoryRefreshPending = true;
        territoryRefreshAt = Environment.TickCount64 + TerritoryRefreshDelayMs;
    }

    private void UpdateTerritory()
    {
        currentTerritoryId = DService.Instance().ClientState.TerritoryType;
        var row = DalamudServices.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(currentTerritoryId);
        currentRegion = row.HasValue ? row.Value.PlaceName.ValueNullable?.Name.ToString() ?? "" : "";
    }

    private static string ConfigDirectory => DalamudServices.PluginInterface.GetPluginConfigDirectory();
    private static string LegacyConfigPath => Path.Combine(ConfigDirectory, "AchievementTracker.json");
    private static string LegacyMigrationMarkerPath => Path.Combine(ConfigDirectory, "AchievementTracker.legacy-migrated");
    private string ProfileConfigPath => Path.Combine(ConfigDirectory, $"AchievementTracker-{activeContentId:X16}.json");

    private void EnsureCharacterProfile()
    {
        var clientState = DService.Instance().ClientState;
        var playerState = DService.Instance().PlayerState;
        var contentId = clientState.IsLoggedIn && playerState.IsLoaded ? playerState.ContentId : 0;
        if (contentId == activeContentId) return;

        SaveConfig();
        activeContentId = 0;
        ResetCharacterConfig();
        progress.Clear();
        requests.Clear();
        progressRequestInFlight = 0;
        progressRequestDeadline = 0;
        nextProgressRequestAt = 0;
        nextProgressRefreshAt = 0;
        territoryRefreshAt = 0;
        territoryRefreshPending = false;
        achievementSearch.Clear();
        territorySearch.Clear();
        draftGoal = new AchievementGoal { ShowCompleted = profile.ShowCompleted };
        nativeWatchlistImported = false;
        nativeWatchlistImportAfter = 0;
        nextNativeWatchlistPoll = 0;
        nativeWatchlistSnapshot = "";
        legacyMigrationPending = false;
        profileReadOnly = false;

        if (contentId == 0) return;

        activeContentId = contentId;
        if (storage.TryGetSnapshot(ProfileConfigPath, out _) || File.Exists(ProfileConfigPath) || File.Exists(ProfileConfigPath + ".bak"))
        {
            LoadConfigFile(ProfileConfigPath);
        }
        else if (legacyMigrationOwnerContentId == 0 && !File.Exists(LegacyMigrationMarkerPath) && File.Exists(LegacyConfigPath))
        {
            legacyMigrationOwnerContentId = contentId;
            if (LoadConfigFile(LegacyConfigPath))
            {
                legacyMigrationPending = true;
                SaveConfig();
            }
        }

        draftGoal = new AchievementGoal { ShowCompleted = profile.ShowCompleted };
        nativeWatchlistImportAfter = Environment.TickCount64 + 2000;
        nextNativeWatchlistPoll = 0;
        DalamudServices.PluginLog.Information(
            $"成就追踪：已加载角色配置，角色={activeContentId:X16}，将在登录数据稳定后读取原生目标列表");
        QueueProgressRequests();
    }

    private bool LoadConfigFile(string path)
    {
        if (storage.TryGetSnapshot(path, out var queued))
        {
            try { ApplyProfileJson(queued); return true; }
            catch (Exception error)
            {
                profileReadOnly = true;
                DalamudServices.PluginLog.Error(error, "成就追踪：待写入角色档案读取失败");
                return false;
            }
        }
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                ApplyProfileJson(File.ReadAllText(candidate));
                if (candidate != path)
                {
                    storage.PreserveBackup(path);
                    DalamudServices.PluginLog.Warning($"成就追踪：已读取备份，后续保存将恢复主文件: {path}");
                }
                return true;
            }
            catch (NotSupportedException error)
            {
                profileReadOnly = true;
                DalamudServices.PluginLog.Error(error, $"成就追踪：档案版本不兼容，保留原文件: {candidate}");
                return false;
            }
            catch (Exception error)
            {
                DalamudServices.PluginLog.Error(error, $"成就追踪：读取档案失败: {candidate}");
            }
        }
        profileReadOnly = true;
        return false;
    }

    private void ApplyProfileJson(string json)
        => profile = AchievementTrackerProfileJson.Deserialize(json, activeContentId);

    private void ResetCharacterConfig()
    {
        profile = new AchievementTrackerProfileData
        {
            ShowOverlay = config.DefaultShowOverlay,
            ShowCompleted = config.DefaultShowCompleted,
            Opacity = float.IsFinite(config.DefaultOpacity) ? Math.Clamp(config.DefaultOpacity, 0.1f, 1f) : 0.92f,
        };
    }

    private void SaveConfig()
    {
        if (activeContentId == 0 || profileReadOnly) return;
        try
        {
            EnsureStorageLifetime();
            var owner = activeContentId.ToString("X16");
            var json = AchievementTrackerProfileJson.Serialize(profile, activeContentId);
            storage.Save(ProfileConfigPath, json,
                legacyMigrationPending ? LegacyMigrationMarkerPath : null,
                legacyMigrationPending ? owner : null);
        }
        catch (Exception error) { DalamudServices.PluginLog.Error(error, "成就追踪：保存角色档案失败"); }
    }

}
