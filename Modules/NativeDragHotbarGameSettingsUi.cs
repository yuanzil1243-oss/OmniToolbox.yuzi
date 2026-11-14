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
    private bool workshopGameLayout = true, workshopGameCharacter, workshopGameBindings, workshopGameSystem;
    private WorkshopGameTemplate? workshopGameDraft, workshopGameBefore;
    private List<WorkshopGameDifference> workshopGameDifferences = [];
    private int workshopGameOperation;
    private readonly HashSet<string> workshopGameAttempted = new(StringComparer.Ordinal);
    private long workshopGameReadyAt;
    private (ulong Owner, long Started, WorkshopGameScope Scope)? workshopGameSaveWatch;
    private WorkshopLiveHudCheck? workshopGameLiveCheck;
    private string workshopGameStatus = "", workshopGameBackupPath = "";

    private void ClearWorkshopGameOperation()
    { workshopGameDraft = workshopGameBefore = null; workshopGameDifferences.Clear(); workshopGameOperation = 0; }

    private void CancelWorkshopGameOperation()
    { ClearWorkshopGameOperation(); workshopGameStatus = ""; }

    private void DrawWorkshopGameCaptureSettings()
    {
        ImGui.TextWrapped("先保存并关闭游戏的「界面设置」，再读取需要备份的内容。只更新勾选项，保留工坊栏内容、自定义指令和未勾选的备份。");
        ImGui.Checkbox("界面与窗口布局", ref workshopGameLayout); ImGui.SameLine();
        ImGui.Checkbox("角色操作与显示", ref workshopGameCharacter);
        ImGui.Checkbox("其他快捷键", ref workshopGameBindings); ImGui.SameLine();
        ImGui.Checkbox("系统画面与声音", ref workshopGameSystem);
        ImGui.BeginDisabled(!workshopGameLayout && !workshopGameCharacter && !workshopGameBindings && !workshopGameSystem);
        if (ImGui.Button("读取所选内容…")) BeginWorkshopGameCapture();
        ImGui.EndDisabled();
    }
    private void DrawWorkshopGameBackupDetails()
    {
        if (workshopLibrary is null) return;
        var current = workshopLibrary.Current;
        if (current.GameSettings is { } saved)
        {
            var date = DateTimeOffset.TryParse(saved.CapturedAt, out var at) ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            ImGui.Separator();
            ImGui.TextWrapped($"界面与操作备份：{saved.SourceName} · {date}");
            ImGui.TextWrapped(WorkshopGameRules.Summary(saved) + "。");
            var automatic = current.AutoApplyGameSettings;
            if (ImGui.Checkbox("其他角色首次使用时自动写入界面与操作设置", ref automatic))
                if (CommitWorkshopEdit(library => library.Current.AutoApplyGameSettings = automatic))
                    workshopGameStatus = automatic ? "已开启。每个角色写入一次；更新备份后可再次自动写入。" : "已关闭。可到「还原到游戏」手动写入。";
            ImGui.TextWrapped("自动写入只使用这里保存的界面与操作设置，不覆盖用户宏。手动重写请到「还原到游戏」。");
            if (ImGui.Button("删除界面与操作备份…"))
            { ClearSuperImport(); MarkWorkshopOperation(); workshopGameStatus = ""; workshopGameOperation = 4; }
        }
        else ImGui.TextDisabled("尚未备份界面与操作设置。");
        ImGui.TextWrapped("系统设置只备份支持的画面、声音和辅助选项，不含登录信息、分辨率或设备选择。");
        if (workshopGameBackupPath.Length > 0) ImGui.TextWrapped("最近写入前的备份：" + workshopGameBackupPath);
    }
    private void DrawWorkshopGameWriteActions()
    {
        if (workshopLibrary is null || !ImGui.CollapsingHeader("其他游戏设置与撤回")) return;
        ImGui.TextWrapped("需要写入角色操作、其他快捷键或系统设置时，使用已保存的整套界面与操作备份。此操作继续接管。");
        ImGui.BeginDisabled(workshopLibrary.Current.GameSettings is null);
        if (ImGui.Button("预览整套界面与操作设置…") && workshopLibrary.Current.GameSettings is { } saved)
        { restoreConfirm = false; superRestorePreview = null; BeginWorkshopGameApply(saved, 2); }
        ImGui.EndDisabled();
        if (workshopGameOperation == 0 && ImGui.Button("撤回上次界面与操作写入…"))
        { restoreConfirm = false; superRestorePreview = null; BeginWorkshopGameRestore(); }
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
            ImGui.TextWrapped("删除当前配置的界面与操作备份并关闭自动写入？游戏当前设置和写入前备份会保留。");
        else if (workshopGameDraft is { } draft)
        {
            if (workshopGameOperation == 1)
            {
                ImGui.TextUnformatted("确认保存读取结果");
                ImGui.TextWrapped("已读取：" + WorkshopGameRules.Summary(draft) + "。");
                ImGui.TextWrapped("只更新以上备份内容；未勾选的备份和工坊自定义指令保留，不写入游戏。");
            }
            else
            {
                ImGui.TextUnformatted(workshopGameOperation == 3 ? "确认撤回上次写入" : "确认写入界面与操作设置");
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
        if (ImGui.Button(operation == 1 ? "确认保存读取结果##GameSettings" : operation == 4 ? "确认删除备份" : "确认写入游戏##GameSettings"))
        {
            if (operation == 4)
            {
                if (CommitWorkshopEdit(library => { library.Current.GameSettings = null; library.Current.AutoApplyGameSettings = false; library.Current.AppliedGameSettings.Clear(); }))
                { workshopGameStatus = "界面与操作备份已删除。"; ClearWorkshopGameOperation(); }
            }
            else if (operation == 1 && workshopGameDraft is { } template)
            {
                if (CommitWorkshopEdit(library => {
                    library.Current.GameSettings = WorkshopGameRules.UpdateSelected(library.Current.GameSettings, template);
                    library.Current.AppliedGameSettings.Clear();
                }))
                {
                    workshopGameStatus = "所选内容已保存。工坊栏内容、自定义指令和其他备份已保留。";
                    ClearWorkshopGameOperation();
                }
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
        if (differences.Count > 0 || target.Scope.Layout)
        {
            workshopGameBackupPath = WorkshopGameBackupStore.Save(WorkshopDirectory, before);
            var liveCheck = target.Scope.Layout ? new WorkshopLiveHudCheck(target,before,WorkshopGameNative.LiveHud(target)) : null;
            superReady = false; RestoreSuperVisibility(); nativeDetails?.Release();
            try { WorkshopGameNative.Apply(target, before, owner); }
            finally { superGroup = -1; superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); }
            workshopGameLiveCheck = liveCheck;
            workshopGameSaveWatch = (owner, Environment.TickCount64, target.Scope);
        }
        if (markApplied)
        {
            var applied = workshopLibrary.Current.AppliedGameSettings;
            applied[owner.ToString("X16")] = target.Id; workshopDirty = true; SaveWorkshop(true);
        }
        workshopGameStatus = differences.Count == 0 && !target.Scope.Layout ? "当前角色与模板一致，没有重复写入。"
            : $"已应用 {differences.Count} 项配置差异并重新加载界面布局，配置读回核对通过，正在检查游戏保存队列。请核对实际界面位置，并在下次登录时确认保存。";
        if (workshopSaveError.Length > 0) workshopGameStatus += " 模板应用记录保存失败，请重试保存；游戏设置已应用。";
        ClearWorkshopGameOperation();
    }
    private void UpdateWorkshopGameTemplate()
    {
        if (workshopGameSaveWatch is { } watch)
        {
            if (owner != watch.Owner) { workshopGameSaveWatch = null; workshopGameLiveCheck = null; }
            else
            {
                try
                {
                    var pending = WorkshopGameNative.SavePending(owner, watch.Scope);
                    if (Environment.TickCount64 - watch.Started >= 250 && (!pending || Environment.TickCount64 - watch.Started >= 10_000))
                    { workshopGameStatus = pending ? "游戏设置读回核对已通过，但保存队列仍在等待。请稍后重新登录核对。"
                        : "游戏设置读回核对已通过，界面与按键保存队列已处理；角色／系统选项保存已请求。请在下次登录时核对。";
                        if (workshopGameLiveCheck is { } live)
                            workshopGameStatus += " " + WorkshopLiveHudRules.Report(live,WorkshopGameNative.LiveHud(live.Target));
                        workshopGameSaveWatch = null; workshopGameLiveCheck = null; }
                }
                catch (InvalidOperationException) { if (Environment.TickCount64 - watch.Started >= 10_000)
                    { workshopGameStatus = "游戏设置已请求保存，当前无法继续检查队列；请下次登录核对。"; workshopGameSaveWatch = null; workshopGameLiveCheck = null; } }
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
