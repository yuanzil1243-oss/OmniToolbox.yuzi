using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Info.Game.Enums;
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
    private Dalamud.Hooking.Hook<AchievementListModule.Delegates.AddToWatchlist>? addToWatchlistHook;
    private string currentRegion = "";
    private uint currentTerritoryId;
    private readonly Dictionary<int, string> achievementSearch = new();
    private readonly Dictionary<int, string> territorySearch = new();
    private AchievementGoal draftGoal = new();
    private ulong activeContentId;
    private bool nativeWatchlistImported;
    private long nativeWatchlistImportAfter;
    private bool legacyMigrationPending;
    private readonly HashSet<ushort> pendingNativeAdds = new();

    private delegate void ReceiveAchievementProgressDelegate(FfxivAchievement* achievement, uint id, uint current, uint max);
    public override ModuleInfo Info { get; } = new()
    {
        Title = "成就追踪", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "读取游戏原生成就进度并在游戏内显示不限数量的目标，自动同步原生目标列表并按角色保存。",
        Commands = new[]
        {
            new ModuleCommand("显示或隐藏成就追踪窗口", "achievement"),
        },
    };
    public override bool HasSettings => true;

    public override bool TryHandleCommand(string command)
        => TryHandleCommand(command, string.Empty);

    public override bool TryHandleCommand(string command, string arguments)
    {
        var value = string.IsNullOrWhiteSpace(command) ? arguments : command;
        value = value.Trim().TrimStart('/');
        if (value.StartsWith("omni ", StringComparison.OrdinalIgnoreCase))
            value = value[5..].TrimStart();
        var name = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(name, "achievement", StringComparison.OrdinalIgnoreCase))
            return false;

        EnsureCharacterProfile();
        if (activeContentId == 0) return false;
        config.ShowOverlay = !config.ShowOverlay;
        SaveConfig();
        return true;
    }

    protected override void OnEnable()
    {
        progressHook = DService.Instance().Hook.HookFromMemberFunction<ReceiveAchievementProgressDelegate>(
            typeof(FfxivAchievement.MemberFunctionPointers), "ReceiveAchievementProgress", ReceiveAchievementProgress);
        progressHook.Enable();
        addToWatchlistHook = DService.Instance().Hook.HookFromMemberFunction<AchievementListModule.Delegates.AddToWatchlist>(
            typeof(AchievementListModule.MemberFunctionPointers), "AddToWatchlist", AddToWatchlist);
        addToWatchlistHook.Enable();
        DalamudServices.Framework.Update += OnFrameworkUpdate;
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
        DalamudServices.PluginInterface.UiBuilder.Draw += DrawOverlay;
        UpdateTerritory();
        EnsureCharacterProfile();
    }

    protected override void OnDisable()
    {
        DalamudServices.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;
        SaveConfig();
        progressHook?.Dispose();
        progressHook = null;
        addToWatchlistHook?.Dispose();
        addToWatchlistHook = null;
        progress.Clear();
        requests.Clear();
        activeContentId = 0;
        nativeWatchlistImported = false;
        pendingNativeAdds.Clear();
    }

    public override bool DrawSettings()
    {
        EnsureCharacterProfile();
        if (activeContentId == 0)
        {
            ImGui.TextDisabled("角色信息加载中…");
            return false;
        }

        var changed = false;
        var controlHeight = ImGui.GetFrameHeight();

        if (ImGui.BeginTable("##add-goal-panel", 1,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH
                | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
            ImGui.TextUnformatted("添加目标成就");
            ImGui.SameLine();
            ImGui.TextDisabled("完成设置后点击保存，目标才会加入下方列表");

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextDisabled("成就名称");
            changed |= DrawAchievementPicker(-1, draftGoal, false);

            if (draftGoal.Id > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("达成条件与进度");
                DrawAchievementProgress(draftGoal);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                changed |= DrawTerritoryPicker(-1, draftGoal);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("备注");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                changed |= ImGui.InputText("##draft-note", ref draftGoal.Note, 128);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                changed |= ImGui.Checkbox("完成后仍显示", ref draftGoal.ShowCompleted);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var actionWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
                if (ImGui.Button("保存目标", new System.Numerics.Vector2(actionWidth, controlHeight)))
                {
                    config.Goals.Add(new AchievementGoal
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
                    draftGoal = new AchievementGoal();
                    achievementSearch.Remove(-1);
                    territorySearch.Remove(-1);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("清空编辑", new System.Numerics.Vector2(actionWidth, controlHeight)))
                {
                    draftGoal = new AchievementGoal();
                    achievementSearch.Remove(-1);
                    territorySearch.Remove(-1);
                    changed = true;
                }
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        for (var i = 0; i < config.Goals.Count; i++)
        {
            var goal = config.Goals[i];
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
                var deleteWidth = ImGui.CalcTextSize("删除").X + ImGui.GetStyle().FramePadding.X * 2;
                var titleWidth = MathF.Max(80,
                    ImGui.GetContentRegionAvail().X
                    - ImGui.GetStyle().ItemSpacing.X
                    - deleteWidth);
                ImGui.SetCursorPosY(headerTop + (headerHeight - controlHeight) * 0.5f);
                if (ImGui.Selectable(
                        $"目标 {i + 1} · {GetGoalSummary(goal)}##goal-toggle",
                        goal.Expanded,
                        ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(titleWidth, controlHeight)))
                {
                    goal.Expanded = !goal.Expanded;
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.SetCursorPosY(headerTop + (headerHeight - controlHeight) * 0.5f);
                var remove = ImGui.Button("删除", new System.Numerics.Vector2(deleteWidth, controlHeight));
                if (remove)
                {
                    config.Goals.RemoveAt(i);
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
                    ImGui.TextDisabled("成就名称");
                    changed |= DrawAchievementPicker(i, goal);

                    ImGui.Spacing();
                    ImGui.TextDisabled("达成条件与进度");
                    ImGui.Indent(12);
                    DrawAchievementProgress(goal);
                    ImGui.Unindent(12);

                    ImGui.Spacing();
                    ImGui.Indent(12);
                    changed |= DrawTerritoryPicker(i, goal);
                    ImGui.Unindent(12);

                    ImGui.Spacing();
                    ImGui.TextDisabled("备注");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1);
                    changed |= ImGui.InputText("##note", ref goal.Note, 128);

                    ImGui.Spacing();
                    changed |= ImGui.Checkbox("完成后仍显示", ref goal.ShowCompleted);
                    ImGui.Unindent(12);
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            ImGui.PopID();
        }
        if (config.Goals.Count == 0)
            ImGui.TextDisabled("暂无目标。可使用上方搜索添加，或加入游戏原生目标列表后自动同步。");

        if (ImGui.BeginTable("##display-options", 2,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            changed |= ImGui.Checkbox("显示游戏内窗口", ref config.ShowOverlay);
            ImGui.TableSetColumnIndex(1);
            if (config.Goals.Count > 0)
            {
                var clearWidth = ImGui.CalcTextSize("清空全部").X + ImGui.GetStyle().FramePadding.X * 2;
                var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - clearWidth;
                ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), right));
                if (ImGui.Button("清空全部", new System.Numerics.Vector2(clearWidth, controlHeight)))
                {
                    config.Goals.Clear();
                    progress.Clear();
                    changed = true;
                }
            }
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Button("重新读取进度", new System.Numerics.Vector2(0, controlHeight)))
            {
                QueueProgressRequests();
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled("目标进度由游戏原生数据读取");
            ImGui.EndTable();
        }
        if (changed) { SaveConfig(); QueueProgressRequests(); }
        return changed;
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
        if (string.IsNullOrWhiteSpace(name)) name = "选择成就…";

        if (ImGui.Button($"{name}##achievement-picker", new System.Numerics.Vector2(-1, 0)))
        {
            query = "";
            achievementSearch[index] = query;
            ImGui.OpenPopup(popupId);
        }
        if (showMetadata && row.HasValue)
        {
            ImGui.TextDisabled($"ID: {row.Value.RowId}");
            ImGui.SameLine();
            ImGui.TextDisabled($"分类: {GetAchievementCategory(row.Value)}");
        }
        else if (showMetadata && goal.Id > 0)
        {
            ImGui.TextDisabled($"无效成就 ID: {goal.Id}");
        }
        else if (showMetadata && !string.IsNullOrWhiteSpace(goal.Name))
        {
            ImGui.TextDisabled("待匹配名称，可在上方搜索框重新选择");
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
            ImGui.InputTextWithHint("##achievement-search", "搜索成就中文名或 ID", ref query, 128);
            achievementSearch[index] = query;

            if (ImGui.BeginChild("##achievement-results", new System.Numerics.Vector2(-1, childHeight), false))
            {
                if (ImGui.BeginTable("##achievement-result-table", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("成就名称", ImGuiTableColumnFlags.WidthStretch, 1.3f);
                    ImGui.TableSetupColumn("分类", ImGuiTableColumnFlags.WidthStretch, 1.0f);
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
            ImGui.TextDisabled("选择成就后自动读取目标条件和进度");
            return;
        }

        var row = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>().GetRowOrDefault((uint)goal.Id);
        if (!row.HasValue)
        {
            ImGui.TextDisabled("无效成就 ID");
            return;
        }

        ImGui.TextWrapped(row.Value.Description.ToString());
        progress.TryGetValue((uint)goal.Id, out var state);
        var ratio = state.Max == 0 ? 0 : Math.Clamp(state.Current / (float)state.Max, 0, 1);
        ImGui.ProgressBar(ratio, new System.Numerics.Vector2(-1, 0), state.Max == 0 ? "读取中…" : $"{state.Current} / {state.Max} ({ratio:P0})");
    }

    private bool DrawTerritoryPicker(int index, AchievementGoal goal)
    {
        MigrateLegacyRegion(goal);
        var changed = ImGui.Checkbox("不限制地区", ref goal.IgnoreRegion);
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
            ImGui.InputTextWithHint("##territory-search", "搜索区域、地区中文名或 ID", ref query, 128);
            territorySearch[index] = query ?? "";
            if (ImGui.BeginChild("##territory-results", new System.Numerics.Vector2(-1, childHeight), false))
            {
                if (ImGui.BeginTable("##territory-result-table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("选择", ImGuiTableColumnFlags.WidthFixed, 48);
                    ImGui.TableSetupColumn("区域", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("地区", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 60);
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
            if (ImGui.Button("完成选择", new System.Numerics.Vector2(-1, ImGui.GetFrameHeight())))
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

    private static string GetRegionSelectionSummary(AchievementGoal goal, int index)
    {
        if (goal.Regions.Count == 0)
            return $"选择地区…##territory-picker-{index}";
        if (goal.Regions.Count == 1)
            return $"{goal.Regions[0].Group} / {goal.Regions[0].Name}##territory-picker-{index}";
        return $"已选择 {goal.Regions.Count} 个地区##territory-picker-{index}";
    }

    private static void MigrateLegacyRegion(AchievementGoal goal)
    {
        if (goal.Regions.Count > 0 || (goal.RegionId <= 0 && string.IsNullOrWhiteSpace(goal.Region)))
            return;

        goal.Regions.Add(new AchievementRegion
        {
            Id = goal.RegionId,
            Group = goal.RegionGroup,
            Name = goal.Region,
        });
    }

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
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("仅可将目标向上拖动");

        if (ImGui.BeginDragDropSource())
        {
            var payload = BitConverter.GetBytes(index);
            ImGui.SetDragDropPayload("ACHIEVEMENT_GOAL", payload);
            ImGui.TextUnformatted($"目标 {index + 1}");
            ImGui.EndDragDropSource();
        }

        var changed = false;
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("ACHIEVEMENT_GOAL");
            if (!payload.IsNull && payload.IsDelivery() && payload.DataSize >= sizeof(int))
            {
                var sourceIndex = System.Runtime.InteropServices.Marshal.ReadInt32((nint)payload.Data);
                if (sourceIndex > index && sourceIndex < config.Goals.Count)
                {
                    var moved = config.Goals[sourceIndex];
                    config.Goals.RemoveAt(sourceIndex);
                    config.Goals.Insert(index, moved);
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
        goal.Id = (int)row.RowId;
        goal.Name = row.Name.ToString();
        goal.Enabled = true;
    }

    private static string GetAchievementCategory(LuminaAchievement row)
    {
        var category = row.AchievementCategory.ValueNullable;
        if (!category.HasValue) return "未分类";

        var categoryName = category.Value.Name.ToString();
        var kindName = category.Value.AchievementKind.ValueNullable?.Name.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(kindName)) return string.IsNullOrWhiteSpace(categoryName) ? "未分类" : categoryName;
        if (string.IsNullOrWhiteSpace(categoryName)) return kindName;
        return $"{kindName} - {categoryName}";
    }

    private static string GetGoalSummary(AchievementGoal goal)
    {
        MigrateLegacyRegion(goal);
        var name = string.IsNullOrWhiteSpace(goal.Name) ? "未选择成就" : goal.Name;
        if (goal.IgnoreRegion) return name;
        if (goal.Regions.Count == 0) return $"{name} · 未选择地区";
        if (goal.Regions.Count == 1) return $"{name} · {goal.Regions[0].Name}";
        return $"{name} · {goal.Regions.Count} 个地区";
    }

    private readonly record struct TerritorySearchResult(TerritoryType Row, string Group, string Area);

    private void DrawOverlay()
    {
        if (activeContentId == 0 || !config.ShowOverlay) return;
        ImGui.SetNextWindowBgAlpha(config.Opacity);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize;
        if (!ImGui.Begin("##AchievementTrackerOverlay", flags)) { ImGui.End(); return; }
        ImGui.TextUnformatted("目标成就 · " + (string.IsNullOrEmpty(currentRegion) ? "未知地区" : currentRegion));
        foreach (var goal in config.Goals)
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
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(goal.Name) ? "未选择成就" : goal.Name);
                ImGui.TextDisabled("待匹配成就：请在模块设置中选择原生成就");
                if (!string.IsNullOrWhiteSpace(goal.Note)) ImGui.TextDisabled(goal.Note);
                continue;
            }
            progress.TryGetValue((uint)goal.Id, out var state);
            var completed = state.Max > 0 && state.Current >= state.Max;
            if (!goal.ShowCompleted && completed) continue;
            var row = DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>().GetRowOrDefault((uint)goal.Id);
            ImGui.Separator();
            ImGui.TextUnformatted(row.HasValue ? row.Value.Name.ToString() : (string.IsNullOrWhiteSpace(goal.Name) ? $"无效成就 ID: {goal.Id}" : goal.Name));
            if (!string.IsNullOrWhiteSpace(goal.Note))
                ImGui.TextDisabled(goal.Note);
            var ratio = state.Max == 0 ? 0 : Math.Clamp(state.Current / (float)state.Max, 0, 1);
            ImGui.ProgressBar(ratio, new System.Numerics.Vector2(220, 0), state.Max == 0 ? "读取中…" : $"{state.Current} / {state.Max} ({ratio:P0})");
            ImGui.TextColored(completed ? new System.Numerics.Vector4(0.3f, 1, 0.6f, 1) : new System.Numerics.Vector4(1, .75f, .3f, 1), completed ? "已完成" : "进行中");
        }
        ImGui.End();
    }

    private void QueueProgressRequests()
    {
        requests.Clear();
        var ids = config.Goals.Where(x => x.Enabled && x.Id > 0).Select(x => (uint)x.Id);
        if (draftGoal.Enabled && draftGoal.Id > 0)
            ids = ids.Append((uint)draftGoal.Id);
        foreach (var id in ids.Distinct())
            requests.Enqueue(id);
    }

    private bool AddToWatchlist(AchievementListModule* list, ushort achievementId)
    {
        var result = addToWatchlistHook!.Original(list, achievementId);
        var isOnWatchlist = result;
        if (!isOnWatchlist && list != null)
        {
            var count = Math.Min((int)list->WatchlistCount, list->WatchList.Length);
            isOnWatchlist = list->WatchList[..count].Contains(achievementId);
        }
        if (isOnWatchlist && activeContentId != 0 && list != null && list->CharacterContentId == activeContentId)
            pendingNativeAdds.Add(achievementId);
        return result;
    }

    private void AddGoalById(uint achievementId, bool expanded)
    {
        if (achievementId == 0 || activeContentId == 0) return;
        var existing = config.Goals.FirstOrDefault(goal => goal.Id == (int)achievementId);
        if (existing != null)
        {
            existing.Enabled = true;
            existing.Expanded = expanded;
            SaveConfig();
            QueueProgressRequests();
            return;
        }

        config.Goals.Add(new AchievementGoal
        {
            Id = (int)achievementId,
            Name = GetAchievementName(achievementId),
            Enabled = true,
            IgnoreRegion = true,
            Region = "",
            Expanded = expanded,
        });
        SaveConfig();
        QueueProgressRequests();
    }

    private unsafe void ImportNativeWatchlist()
    {
        var list = AchievementListModule.Instance();
        if (list == null || list->CharacterContentId != activeContentId) return;

        var count = Math.Min((int)list->WatchlistCount, list->WatchList.Length);
        var existingIds = config.Goals.Select(goal => (uint)goal.Id).ToHashSet();
        var imported = 0;
        for (var i = 0; i < count; i++)
        {
            var achievementId = list->WatchList[i];
            if (achievementId == 0 || !existingIds.Add(achievementId)) continue;
            config.Goals.Add(new AchievementGoal
            {
                Id = achievementId,
                Name = GetAchievementName(achievementId),
                Enabled = true,
                IgnoreRegion = true,
                Expanded = false,
            });
            imported++;
        }

        nativeWatchlistImported = true;
        if (imported > 0)
        {
            SaveConfig();
            QueueProgressRequests();
        }
        DalamudServices.PluginLog.Information(
            $"成就追踪：角色目标列表初始化完成，角色={activeContentId:X16}, 原生目标={count}, 新同步={imported}");
    }

    private static string GetAchievementName(uint achievementId)
    {
        return DalamudServices.DataManager.GetExcelSheet<LuminaAchievement>()
            .GetRowOrDefault(achievementId)?.Name.ToString() ?? "";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        EnsureCharacterProfile();
        if (activeContentId != 0 && !nativeWatchlistImported && Environment.TickCount64 >= nativeWatchlistImportAfter)
            ImportNativeWatchlist();

        if (pendingNativeAdds.Count > 0)
        {
            foreach (var achievementId in pendingNativeAdds.ToArray())
            {
                AddGoalById(achievementId, expanded: true);
                DalamudServices.PluginLog.Information(
                    $"成就追踪：原生加入目标列表已触发，角色={activeContentId:X16}, 成就ID={achievementId}");
            }
            pendingNativeAdds.Clear();
        }

        var achievement = FfxivAchievement.Instance();
        if (achievement != null && achievement->ProgressAchievementId != 0
            && achievement->ProgressRequestState == FfxivAchievement.AchievementState.Loaded)
        {
            progress[achievement->ProgressAchievementId] = new(achievement->ProgressCurrent, achievement->ProgressMax);
        }

        if (!DService.Instance().ClientState.IsLoggedIn || requests.Count == 0 || achievement == null) return;
        var id = requests.Dequeue();
        achievement->RequestAchievementProgress(id);
    }

    private void ReceiveAchievementProgress(FfxivAchievement* achievement, uint id, uint current, uint max)
    {
        progressHook!.Original(achievement, id, current, max);
        progress[id] = new(current, max);
    }

    private void OnTerritoryChanged(uint territoryType) => UpdateTerritory();

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
        achievementSearch.Clear();
        territorySearch.Clear();
        draftGoal = new AchievementGoal();
        pendingNativeAdds.Clear();
        nativeWatchlistImported = false;
        nativeWatchlistImportAfter = 0;
        legacyMigrationPending = false;

        if (contentId == 0) return;

        activeContentId = contentId;
        if (File.Exists(ProfileConfigPath))
        {
            LoadConfigFile(ProfileConfigPath);
        }
        else if (!File.Exists(LegacyMigrationMarkerPath) && File.Exists(LegacyConfigPath))
        {
            LoadConfigFile(LegacyConfigPath);
            legacyMigrationPending = true;
            SaveConfig();
        }

        nativeWatchlistImportAfter = Environment.TickCount64 + 2000;
        QueueProgressRequests();
    }

    private void LoadConfigFile(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<AchievementTrackerConfig>(File.ReadAllText(path), JsonOptions);
            if (saved == null) return;
            config.ShowOverlay = saved.ShowOverlay;
            config.ShowCompleted = saved.ShowCompleted;
            config.Opacity = saved.Opacity;
            config.Goals.Clear();
            config.Goals.AddRange(saved.Goals ?? new List<AchievementGoal>());
            config.Goals.RemoveAll(goal => goal == null || (goal.Id <= 0 && string.IsNullOrWhiteSpace(goal.Name)));
            foreach (var goal in config.Goals)
                MigrateLegacyRegion(goal);
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, $"读取成就追踪配置失败: {path}"); }
    }

    private void ResetCharacterConfig()
    {
        config.Goals.Clear();
        config.ShowOverlay = true;
        config.ShowCompleted = true;
        config.Opacity = 0.92f;
    }

    private void SaveConfig()
    {
        if (activeContentId == 0) return;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ProfileConfigPath, JsonSerializer.Serialize(config, JsonOptions));
            if (legacyMigrationPending)
            {
                File.WriteAllText(LegacyMigrationMarkerPath, activeContentId.ToString());
                legacyMigrationPending = false;
            }
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存成就追踪角色配置失败"); }
    }
}

[Serializable]
public sealed class AchievementTrackerConfig
{
    public List<AchievementGoal> Goals = new();
    public bool ShowOverlay = true;
    public bool ShowCompleted = true;
    public float Opacity = 0.92f;
}

[Serializable]
public sealed class AchievementGoal
{
    public int Id;
    public string Name = "";
    public string Region = "";
    public string RegionGroup = "";
    public int RegionId;
    public List<AchievementRegion> Regions = new();
    public string Note = "";
    public bool Enabled = true;
    public bool IgnoreRegion = true;
    public bool ShowCompleted = true;
    public bool Expanded = true;
}

[Serializable]
public sealed class AchievementRegion
{
    public int Id;
    public string Group = "";
    public string Name = "";

    public AchievementRegion Clone() => new()
    {
        Id = Id,
        Group = Group,
        Name = Name,
    };
}

public readonly record struct AchievementProgress(uint Current, uint Max);
