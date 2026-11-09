using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using OmniToolbox.Host;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private bool workshopGameLayout = true, workshopGameCharacter = true, workshopGameBindings = true, workshopGameSystem;
    private WorkshopGameTemplate? workshopGameDraft, workshopGameBefore;
    private List<WorkshopGameDifference> workshopGameDifferences = [];
    private int workshopGameOperation;
    private readonly HashSet<string> workshopGameAttempted = new(StringComparer.Ordinal);
    private long workshopGameReadyAt;
    private (ulong Owner, long Started, WorkshopGameScope Scope)? workshopGameSaveWatch;
    private string workshopGameStatus = "", workshopGameBackupPath = "";

    private void ClearWorkshopGameOperation()
    { workshopGameDraft = workshopGameBefore = null; workshopGameDifferences.Clear(); workshopGameOperation = 0; }

    private void CancelWorkshopGameOperation()
    { ClearWorkshopGameOperation(); workshopGameStatus = ""; }

    private void DrawWorkshopGameSettings()
    {
        if (workshopLibrary is null || !ImGui.CollapsingHeader("跨角色游戏设置模板")) return;
        var current = workshopLibrary.Current;
        ImGui.TextWrapped("保存这套配置的游戏设置，供本机其他角色使用。应用前会备份当前角色的设置。");
        if (workshopGameOperation != 0)
        {
            DrawWorkshopGameOperation();
            DrawWorkshopGameStatus();
            return;
        }
        if (current.GameSettings is { } template)
        {
            var date = DateTimeOffset.TryParse(template.CapturedAt, out var at) ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            ImGui.TextWrapped($"已保存：{template.SourceName} · {date}");
            if (ImGui.Button("应用到当前角色…")) BeginWorkshopGameApply(template, 2);
            ImGui.SameLine();
            if (ImGui.Button("恢复应用前设置…")) BeginWorkshopGameRestore();
            // A button can open a confirmation mid-frame; do not draw competing actions below it.
            if (workshopGameOperation != 0) return;
            var automatic = current.AutoApplyGameSettings;
            if (ImGui.Checkbox("其他角色首次使用这套配置时自动应用模板", ref automatic))
            {
                if (CommitWorkshopEdit(library => library.Current.AutoApplyGameSettings = automatic))
                    workshopGameStatus = automatic ? "已开启。角色界面就绪后应用一次；此后在该角色的手动调整会保留。" : "已关闭自动应用，可随时手动预览并应用。";
            }
            ImGui.TextWrapped("每个角色只自动应用一次；需要重套时可手动应用。");
        }
        else
        {
            ImGui.TextUnformatted("尚未保存模板。先在设置好的角色上读取。");
            if (ImGui.Button("恢复应用前设置…")) BeginWorkshopGameRestore();
            if (workshopGameOperation != 0) return;
        }
        ImGui.Spacing();
        if (ImGui.TreeNodeEx("读取／更新模板"))
        {
            ImGui.TextWrapped("先保存并关闭游戏的界面设置，再选择要读取的内容。");
            ImGui.Checkbox("界面与窗口布局", ref workshopGameLayout); ImGui.SameLine();
            ImGui.Checkbox("角色操作与显示", ref workshopGameCharacter);
            ImGui.Checkbox("其他快捷键", ref workshopGameBindings); ImGui.SameLine();
            ImGui.Checkbox("系统画面与声音", ref workshopGameSystem);
            ImGui.BeginDisabled(!workshopGameLayout && !workshopGameCharacter && !workshopGameBindings && !workshopGameSystem);
            if (ImGui.Button("读取游戏设置模板…")) BeginWorkshopGameCapture();
            ImGui.EndDisabled();
            ImGui.TreePop();
        }
        if (workshopGameOperation != 0) return;
        if (ImGui.TreeNodeEx("模板详情与管理"))
        {
            if (current.GameSettings is { } saved) ImGui.TextWrapped("模板内容：" + WorkshopGameRules.Summary(saved) + "。");
            ImGui.TextWrapped("热键栏和用户宏使用本页上方的备份功能。模板不会自动覆盖用户宏。");
            ImGui.TextWrapped("重新保存模板后，其他角色下次使用时会自动应用新版（需开启自动应用）。");
            ImGui.TextWrapped("系统设置仅包含支持的画面、声音和辅助功能，不复制登录信息、分辨率或设备选择。");
            if (workshopGameBackupPath.Length > 0) ImGui.TextWrapped("最近读取或生成的应用前备份：" + workshopGameBackupPath);
            if (current.GameSettings is not null && ImGui.Button("删除游戏设置模板…"))
            { ClearSuperImport(); MarkWorkshopOperation(); workshopGameStatus = ""; workshopGameOperation = 4; }
            ImGui.TreePop();
        }
        if (workshopGameOperation == 0) DrawWorkshopGameStatus();
    }
    private void DrawWorkshopGameStatus()
    {
        if (workshopGameStatus.Length == 0) return;
        ImGui.Spacing(); ImGui.TextWrapped(workshopGameStatus);
    }
    private void BeginWorkshopGameCapture()
    {
        ClearSuperImport(); MarkWorkshopOperation(); workshopGameStatus = "";
        try
        {
            workshopGameDraft = WorkshopGameNative.Capture(owner, new(workshopGameLayout, workshopGameCharacter, workshopGameBindings, workshopGameSystem), false);
            workshopGameOperation = 1;
        }
        catch (Exception error) { workshopGameStatus = "读取未完成：" + error.Message; }
    }
    private void BeginWorkshopGameRestore()
    {
        try
        {
            var folder = Path.Combine(WorkshopDirectory, "HotbarWorkshopGameSettingsBackups");
            var path = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, owner.ToString("X16") + "-*.json")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault() : null;
            if (path is null) throw new IOException("当前角色还没有应用前备份。");
            var backup = WorkshopGameBackupStore.Load(path, owner.ToString("X16"));
            BeginWorkshopGameApply(backup, 3); workshopGameBackupPath = path;
        }
        catch (Exception error) { workshopGameStatus = "备份读取失败：" + error.Message; }
    }
    private void BeginWorkshopGameApply(WorkshopGameTemplate template, int operation)
    {
        // A local defensive copy also survives clearing any previous operation state.
        var target = WorkshopProfiles.Copy(template);
        ClearSuperImport(); MarkWorkshopOperation(); workshopGameStatus = "";
        try
        {
            WorkshopGameNative.Preflight(target, owner);
            var before = WorkshopGameNative.Capture(owner, target.Scope);
            workshopGameDifferences = WorkshopGameRules.Compare(before, target);
            workshopGameDraft = target; workshopGameBefore = before; workshopGameOperation = operation;
        }
        catch (Exception error) { workshopGameStatus = "应用预检未通过：" + error.Message; }
    }
    private void DrawWorkshopGameOperation()
    {
        if (workshopGameOperation == 0) return;
        ImGui.Separator();
        if (workshopGameOperation == 4)
            ImGui.TextWrapped("删除当前配置的游戏设置模板并关闭自动应用？游戏当前设置和已留存的应用前备份会保留。");
        else if (workshopGameDraft is { } draft)
        {
            if (workshopGameOperation == 1)
            {
                ImGui.TextUnformatted("确认保存模板");
                ImGui.TextWrapped("已读取：" + WorkshopGameRules.Summary(draft) + "。");
                ImGui.TextWrapped("保存后替换当前配置的模板，游戏当前设置保持原样。");
            }
            else
            {
                ImGui.TextUnformatted(workshopGameOperation == 3 ? "确认恢复设置" : "确认应用模板");
                ImGui.TextWrapped($"将{(workshopGameOperation == 3 ? "恢复" : "应用")} {workshopGameDifferences.Count} 项差异到当前角色。确认后写入游戏；部分窗口需重新打开后显示新位置。");
                if (workshopGameDifferences.Count > 0 && ImGui.BeginTable("##GameSettingsDiff", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 180)))
                {
                    try
                    {
                        foreach (var label in new[] { "范围", "项目", "当前", "模板" }) ImGui.TableSetupColumn(label);
                        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                        foreach (var row in workshopGameDifferences)
                        {
                            ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); ImGui.TextWrapped(row.Category);
                            ImGui.TableSetColumnIndex(1); ImGui.TextWrapped(row.Name);
                            ImGui.TableSetColumnIndex(2); ImGui.TextWrapped(row.Before);
                            ImGui.TableSetColumnIndex(3); ImGui.TextWrapped(row.After);
                        }
                    }
                    finally { ImGui.EndTable(); }
                }
            }
        }
        ImGui.BeginDisabled(!WorkshopOperationValid);
        var operation = workshopGameOperation;
        if (ImGui.Button(operation == 1 ? "确认保存游戏设置模板" : operation == 4 ? "确认删除模板" : "确认应用到游戏"))
        {
            if (operation == 4)
            {
                if (CommitWorkshopEdit(library => { library.Current.GameSettings = null; library.Current.AutoApplyGameSettings = false; library.Current.AppliedGameSettings.Clear(); }))
                    workshopGameStatus = "游戏设置模板已删除。";
            }
            else if (operation == 1 && workshopGameDraft is { } template)
            {
                var source = template.Source;
                if (CommitWorkshopEdit(library => { library.Current.GameSettings = template;
                    library.Current.AppliedGameSettings.Clear(); library.Current.AppliedGameSettings[source] = template.Id; }))
                    workshopGameStatus = "游戏设置模板已保存。其他角色可手动应用，也可勾选自动应用。";
            }
            else if (workshopGameDraft is { } target && workshopGameBefore is { } previous)
            {
                try
                {
                    var fresh = WorkshopGameNative.Capture(owner, target.Scope);
                    if (WorkshopGameRules.Fingerprint(fresh) != WorkshopGameRules.Fingerprint(previous))
                    { workshopGameBefore = fresh; workshopGameDifferences = WorkshopGameRules.Compare(fresh, target); workshopGameStatus = "游戏设置已变化，预览已刷新。本次未写入，请重新确认。"; }
                    else ApplyWorkshopGameTemplate(target, fresh, operation == 2);
                }
                catch (Exception error) { workshopGameStatus = "应用未完成：" + error.Message; ClearWorkshopGameOperation(); }
            }
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("取消##GameSettings")) CancelWorkshopGameOperation();
    }
    private void ApplyWorkshopGameTemplate(WorkshopGameTemplate target, WorkshopGameTemplate before, bool markApplied)
    {
        if (workshopLibrary is null) return;
        var completed = workshopLibrary.Current.AppliedGameSettings;
        if (markApplied && completed.Count >= 1024 && !completed.ContainsKey(owner.ToString("X16")))
            throw new IOException("模板应用记录已满，请关闭自动应用并整理配置。");
        SaveWorkshop(true);
        if (workshopSaveError.Length > 0) throw new IOException("请先重试保存当前模块配置。");
        WorkshopGameNative.Preflight(target, owner);
        var differences = WorkshopGameRules.Compare(before, target);
        if (differences.Count > 0)
        {
            workshopGameBackupPath = WorkshopGameBackupStore.Save(WorkshopDirectory, before);
            superReady = false; RestoreSuperVisibility(); nativeDetails?.Release();
            try { WorkshopGameNative.Apply(target, before, owner); }
            finally { superGroup = -1; superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); }
            workshopGameSaveWatch = (owner, Environment.TickCount64, target.Scope);
        }
        if (markApplied)
        {
            var applied = workshopLibrary.Current.AppliedGameSettings;
            applied[owner.ToString("X16")] = target.Id; workshopDirty = true; SaveWorkshop(true);
        }
        workshopGameStatus = differences.Count == 0 ? "当前角色与模板一致，没有重复写入。"
            : $"已应用 {differences.Count} 项差异并读回核对，正在检查游戏保存队列。请在下次登录时核对持久保存。";
        if (workshopSaveError.Length > 0) workshopGameStatus += " 模板应用记录保存失败，请重试保存；游戏设置已应用。";
        ClearWorkshopGameOperation();
    }
    private void UpdateWorkshopGameTemplate()
    {
        if (workshopGameSaveWatch is { } watch)
        {
            if (owner != watch.Owner) workshopGameSaveWatch = null;
            else
            {
                try
                {
                    var pending = WorkshopGameNative.SavePending(owner, watch.Scope);
                    if (!pending || Environment.TickCount64 - watch.Started >= 10_000)
                    { workshopGameStatus = pending ? "游戏设置读回核对已通过，但保存队列仍在等待。请稍后重新登录核对。"
                        : "游戏设置读回核对已通过，界面与按键保存队列已处理；角色／系统选项保存已请求。请在下次登录时核对。"; workshopGameSaveWatch = null; }
                }
                catch (InvalidOperationException) { if (Environment.TickCount64 - watch.Started >= 10_000)
                    { workshopGameStatus = "游戏设置已请求保存，当前无法继续检查队列；请下次登录核对。"; workshopGameSaveWatch = null; } }
            }
        }
        if (owner == 0 || workshopLibrary is null || readOnly || superReadOnly || workshopGameOperation != 0
            || superImport is not null || pendingImport is not null || restoreConfirm || superAppearanceConfirm || workshopNameAction != 0
            || editorOpen || replacing is not null || bridge?.IsDragging == true
            || workshopLibrary.Current is not { AutoApplyGameSettings: true, GameSettings: { } template } current)
        { workshopGameReadyAt = 0; return; }
        if (!WorkshopGameRules.NeedsAutomaticApply(current, owner)) return;
        var actor = owner.ToString("X16"); var token = current.Id + ":" + actor + ":" + template.Id;
        if (workshopGameAttempted.Contains(token)) return;
        try { WorkshopGameNative.Require(owner); }
        catch (InvalidOperationException) { workshopGameReadyAt = 0; return; }
        if (workshopGameReadyAt == 0) { workshopGameReadyAt = Environment.TickCount64; return; }
        if (Environment.TickCount64 - workshopGameReadyAt < 3000) return;
        workshopGameAttempted.Add(token); workshopGameReadyAt = 0;
        try
        {
            WorkshopGameNative.Preflight(template, owner);
            var before = WorkshopGameNative.Capture(owner, template.Scope);
            ApplyWorkshopGameTemplate(template, before, true);
            superStatus = "已自动应用当前配置的游戏设置模板。" + workshopGameStatus;
        }
        catch (Exception error)
        {
            workshopGameStatus = "自动应用未完成，已暂停本次重试，请到“备份与导入”检查后手动应用：" + error.Message;
            superStatus = workshopGameStatus;
            DalamudServices.PluginLog.Error(error, "热键栏工坊：游戏设置模板自动应用失败");
        }
    }
}
