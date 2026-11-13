using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using OmniToolbox.SuperHotbarInterop;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private int workshopNameAction, workshopExportScope;
    private string workshopEditName = "", workshopOperationId = "";
    private ulong workshopOperationOwner;
    private WorkshopProfile? workshopImport;
    private bool workshopReplaceExtensions, workshopMacroConfirm, workshopReadOverwrite;

    private void ClearWorkshopOperations()
    {
        workshopNameAction = 0; workshopImport = null; workshopMacroConfirm = false;
        workshopReadOverwrite = false;
        workshopOperationId = ""; workshopOperationOwner = 0;
        workshopReplaceExtensions = importGlobalSettings = false;
        superAppearanceConfirm = false;
        ClearWorkshopGameOperation();
    }

    private void MarkWorkshopOperation()
    { workshopOperationId = workshopLibrary!.SelectedId; workshopOperationOwner = owner; }
    private bool WorkshopOperationValid => owner != 0 && owner == workshopOperationOwner
        && workshopLibrary?.SelectedId == workshopOperationId && !readOnly && !superReadOnly;

    private void DrawWorkshopArchiveSettings()
    {
        if (owner == 0) { ImGui.TextWrapped("请先登录角色。本机所有角色使用当前选中的配置。"); return; }
        if (workshopLibrary is null)
        {
            ImGui.TextWrapped(workshopSaveError.Length == 0 ? "正在读取本机配置库。" : workshopSaveError);
            if (ImGui.Button("重试读取配置库")) { workshopLoadAttempted = false; EnsureWorkshopLibrary(); }
            return;
        }
        ImGui.BeginDisabled(readOnly || superReadOnly);
        DrawWorkshopProfileToolbar();
        ImGui.Spacing();
        ImGui.TextWrapped("本机登录其他角色时继续使用当前选择。设置自动保存；游戏设置模板仅在确认应用或开启自动应用后写入游戏。");
        if (workshopLibrary.Current.Takeover is null)
        {
            if (ImGui.Button("读取游戏配置…")) BeginSuperNativeRead();
        }
        else if (ImGui.Button("保存"))
        {
            workshopDirty = true; SaveWorkshop(true);
            superStatus = workshopSaveError.Length == 0 ? "当前配置已保存，自定义指令和布局已保留。" : "保存未完成，请重试：" + workshopSaveError;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(super is null);
        if (ImGui.Button("更新用户宏备份…"))
        { ClearSuperImport(); workshopMacroConfirm = true; MarkWorkshopOperation(); }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("导入配置…")) BeginWorkshopImport();
        ImGui.SameLine();
        if (ImGui.Button("导出配置…"))
        { workshopExportScope = 0; ImGui.OpenPopup("WorkshopExport"); }
        DrawWorkshopExport();
        DrawWorkshopReadPreview();
        DrawWorkshopMacroBackup();
        DrawWorkshopImportPreview();
        if (workshopLibrary.Current.Takeover is not null && ImGui.TreeNodeEx("重新读取游戏配置"))
        {
            ImGui.TextWrapped("仅在需要用游戏内容重建接管档案时使用。会覆盖工坊中编辑的自定义指令、栏位、布局和快捷键；日常修改会自动保存，无需重新读取。");
            if (ImGui.Button("重新读取游戏配置…")) BeginSuperNativeRead();
            ImGui.TreePop();
        }
        ImGui.Separator();
        DrawWorkshopBackupInfo();
        ImGui.Separator();
        DrawWorkshopGameSettings();
        DrawSuperResetSettings();
        ImGui.EndDisabled();
        if (workshopSaveError.Length > 0)
        {
            ImGui.TextWrapped("保存失败，修改仍在内存：" + workshopSaveError);
            if (ImGui.Button("重试保存")) { workshopDirty = true; SaveWorkshop(true); }
        }
        else ImGui.TextDisabled(dirty || superDirty || workshopDirty ? "正在自动保存…"
            : "已自动保存到本机。显隐状态单独保存。");
    }

    private void DrawWorkshopProfileToolbar()
    {
        if (workshopLibrary is null) return;
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("当前配置"); ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().X - 240, 160, 420));
        if (BeginStyledCombo("##WorkshopProfile", workshopLibrary.Current.Name))
        {
            foreach (var entry in workshopLibrary.Profiles.ToArray())
                if (ImGui.Selectable(entry.Name + "##" + entry.Id, entry.Id == workshopLibrary.SelectedId))
                {
                    var id = entry.Id;
                    if (id != workshopLibrary.SelectedId) CommitWorkshopEdit(library => library.SelectedId = id);
                    break;
                }
            EndStyledCombo();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(workshopLibrary.Profiles.Count >= WorkshopProfiles.MaximumProfiles);
        if (ImGui.Button("＋##WorkshopNew")) ImGui.OpenPopup("WorkshopNew");
        if (ImGui.BeginPopup("WorkshopNew"))
        {
            if (ImGui.Selectable("新建空配置")) BeginWorkshopNameEdit(1);
            if (ImGui.Selectable("复制当前配置")) BeginWorkshopNameEdit(2);
            ImGui.EndPopup();
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("重命名")) BeginWorkshopNameEdit(3);
        ImGui.SameLine(); ImGui.BeginDisabled(workshopLibrary.Profiles.Count <= 1);
        if (ImGui.Button("删除配置…")) BeginWorkshopNameEdit(4);
        ImGui.EndDisabled();
        if (workshopNameAction == 0) return;
        var action = workshopNameAction;
        if (action == 4) ImGui.TextWrapped("删除当前选中的配置？其他配置和游戏设置保留，至少保留一套配置。");
        else
        {
            ImGui.SetNextItemWidth(360);
            ImGui.InputText("配置名称", ref workshopEditName, 128);
        }
        ImGui.BeginDisabled(!WorkshopOperationValid);
        if (ImGui.Button(action == 4 ? "确认删除配置" : "确认名称"))
        {
            var name = workshopEditName.Trim();
            CommitWorkshopEdit(library =>
            {
                if (action is 1 or 2)
                    WorkshopProfiles.Add(library, action == 1 ? new WorkshopProfile() : library.Current, name);
                else if (action == 3)
                {
                    if (string.IsNullOrWhiteSpace(name)) throw new System.IO.InvalidDataException("配置名称不能为空。");
                    library.Current.Name = name;
                }
                else
                {
                    if (library.Profiles.Count <= 1) throw new System.IO.InvalidDataException("至少保留一套配置。");
                    var previous = library.Current; var index = library.Profiles.IndexOf(previous);
                    library.Profiles.Remove(previous); library.SelectedId = library.Profiles[Math.Min(index, library.Profiles.Count - 1)].Id;
                }
            });
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("取消##WorkshopName")) workshopNameAction = 0;
    }

    private void BeginWorkshopNameEdit(int action)
    {
        ClearSuperImport(); workshopNameAction = action; MarkWorkshopOperation();
        workshopEditName = action == 1 ? WorkshopProfiles.UniqueName(workshopLibrary!, "新配置")
            : action == 2 ? WorkshopProfiles.UniqueName(workshopLibrary!, workshopLibrary!.Current.Name + " 副本")
            : workshopLibrary!.Current.Name;
    }

    private void DrawWorkshopReadPreview()
    {
        if (superImport is null) return;
        ImGui.Separator();
        ImGui.TextWrapped($"读取预览：热键栏、{superImport.Bindings.Count} 组快捷键、{superImport.Macros.Count} 份宏。确认后更新当前配置的接管档案，扩展栏保留，不写回游戏。");
        DrawSuperBindingWarnings(superImport);
        var replacing = workshopLibrary?.Current.Takeover is not null;
        if (replacing)
        {
            ImGui.TextWrapped("重新读取会覆盖接管档案及工坊自定义指令。保存现有修改请取消，再点“保存”。");
            ImGui.Checkbox("我确认用游戏配置覆盖现有接管档案和自定义指令", ref workshopReadOverwrite);
        }
        ImGui.BeginDisabled(!WorkshopOperationValid || replacing && !workshopReadOverwrite);
        if (ImGui.Button("确认保存读取结果") && WorkshopOperationValid && (!replacing || workshopReadOverwrite))
        {
            var imported = superImport;
            imported.Enabled = false;
            SuperHotbarRules.UpgradePresentation(imported, SuperHotbarKeyMapRuntime.Current);
            if (CommitWorkshopEdit(library => library.Current.Takeover = WorkshopProfiles.Portable(imported)))
                superStatus = "读取结果已保存。检查布局与快捷键后，可到“接管”启用。";
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("取消读取")) ClearSuperImport();
    }

    private void DrawWorkshopMacroBackup()
    {
        if (!workshopMacroConfirm) return;
        ImGui.Separator();
        ImGui.TextWrapped("用当前游戏中的个人宏和共通宏更新这套配置的宏备份？栏位、布局、快捷键和扩展栏不变，不会写回游戏。");
        ImGui.BeginDisabled(!WorkshopOperationValid || super is null);
        if (ImGui.Button("确认更新用户宏备份"))
        {
            try
            {
                var macros = SuperNativeArchive.CaptureMacros(owner);
                var source = owner; var name = CurrentGameCharacterName(); var at = DateTimeOffset.UtcNow.ToString("O");
                if (CommitWorkshopEdit(library => WorkshopProfiles.UpdateMacroBackup(library.Current.Takeover!, macros, source, name, at)))
                    superStatus = "用户宏备份已更新，其他设置保留。日常执行始终使用游戏中的当前用户宏。";
            }
            catch (Exception error) { superStatus = "用户宏备份未更新：" + error.Message; }
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("取消##MacroBackup")) workshopMacroConfirm = false;
    }

    private void BeginWorkshopImport()
    {
        ClearSuperImport();
        try
        {
            var imported = WorkshopProfileTransfer.Decode(ImGui.GetClipboardText(), profile, config.ShowCommandMessages);
            workshopImport = imported.Profile; pendingImport = imported.Extensions;
            workshopEditName = WorkshopProfiles.UniqueName(workshopLibrary!, imported.Profile?.Name ?? "导入配置");
            importOwner = owner; MarkWorkshopOperation();
        }
        catch (Exception error) { superStatus = "导入读取失败：" + error.Message; }
    }

    private void DrawWorkshopImportPreview()
    {
        if (workshopImport is not null)
        {
            ImGui.Separator();
            ImGui.TextWrapped($"配置预览：{workshopImport.Extensions.Bars.Count} 条扩展栏"
                + (workshopImport.Takeover is { } data ? $"，{data.Bindings.Count} 组快捷键、{data.Macros.Count} 份宏备份。" : "。")
                + "将新增为一套配置，现有配置保留，不写回游戏。");
            ImGui.SetNextItemWidth(360); ImGui.InputText("导入后的名称", ref workshopEditName, 128);
            ImGui.BeginDisabled(!WorkshopOperationValid);
            if (ImGui.Button("确认导入为新配置"))
            {
                var imported = workshopImport; var name = workshopEditName;
                if (CommitWorkshopEdit(library =>
                {
                    var added = WorkshopProfiles.Add(library, imported, name);
                    if (added.Takeover is not null) added.Takeover.Enabled = false;
                })) superStatus = "已导入并选中新配置。接管尚未启用，检查后可手动启用。";
            }
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("取消导入##Workshop")) ClearSuperImport();
        }
        if (pendingImport is null) return;
        ImGui.Separator();
        ImGui.TextWrapped($"扩展栏导入预览：{pendingImport.Bars.Count} 条栏位，默认追加到当前配置。");
        foreach (var bar in pendingImport.Bars) ImGui.TextUnformatted($"{bar.Name} · {bar.Columns} × {bar.Rows}");
        ImGui.Checkbox("替换当前配置的全部扩展栏", ref workshopReplaceExtensions);
        if (pendingImport.ShowAllBars.HasValue || pendingImport.ShowCommandMessages.HasValue)
            ImGui.Checkbox("同时导入显示和提示设置", ref importGlobalSettings);
        var exceeds = !workshopReplaceExtensions && profile.Bars.Count + pendingImport.Bars.Count > HotbarRules.MaxBars;
        if (exceeds) ImGui.TextWrapped("追加后超过 12 条，请选择替换，或先删除不需要的栏。");
        ImGui.BeginDisabled(!WorkshopOperationValid || importOwner != owner || exceeds);
        if (ImGui.Button("确认导入扩展栏"))
        {
            var imported = pendingImport; var replace = workshopReplaceExtensions; var global = importGlobalSettings; var first = 0;
            if (CommitWorkshopEdit(library => first = WorkshopProfiles.ImportExtensions(library.Current, imported, replace, global)))
            { selected = first; sharedSettingsExpanded = true; superStatus = "扩展栏已导入当前配置。"; }
        }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("取消导入##Extensions")) ClearSuperImport();
    }

    private void DrawWorkshopExport()
    {
        if (!ImGui.BeginPopup("WorkshopExport")) return;
        try
        {
            string[] scopes = ["完整配置（包含扩展栏）", "仅接管档案", "全部扩展栏", "单条扩展栏"];
            ImGui.SetNextItemWidth(300);
            if (BeginStyledCombo("导出范围", scopes[workshopExportScope]))
            {
                for (var i = 0; i < scopes.Length; i++) if (ImGui.Selectable(scopes[i], workshopExportScope == i)) workshopExportScope = i;
                EndStyledCombo();
            }
            if (workshopExportScope == 3 && profile.Bars.Count > 0)
            {
                selected = Math.Clamp(selected, 0, profile.Bars.Count - 1); ImGui.SetNextItemWidth(300);
                if (BeginStyledCombo("选择栏位", BarLabel(profile.Bars[selected])))
                {
                    for (var i = 0; i < profile.Bars.Count; i++) if (ImGui.Selectable(BarLabel(profile.Bars[i]), selected == i)) selected = i;
                    EndStyledCombo();
                }
            }
            if (workshopExportScope < 2)
            {
                ImGui.TextUnformatted("导出使用已保存的宏备份。");
                ImGui.TextUnformatted("需要更新时，先点“更新用户宏备份”。");
            }
            var unavailable = workshopExportScope == 1 && super is null || workshopExportScope >= 2 && profile.Bars.Count == 0;
            ImGui.BeginDisabled(unavailable);
            if (ImGui.Button("复制到剪贴板"))
            {
                try
                {
                    SaveWorkshop(true);
                    if (workshopSaveError.Length > 0) throw new System.IO.IOException("请先重试保存当前配置。");
                    var text = workshopExportScope switch {
                        0 => WorkshopProfileTransfer.Encode(workshopLibrary!.Current),
                        1 => SuperHotbarStore.Encode(super!, owner),
                        _ => HotbarTransfer.Export(profile, workshopExportScope == 3 ? selected : null, config.ShowCommandMessages, Version),
                    };
                    ImGui.SetClipboardText(text); superStatus = "已复制导出内容，可粘贴到文本文件另存。"; ImGui.CloseCurrentPopup();
                }
                catch (Exception error) { superStatus = "导出失败：" + error.Message; }
            }
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("取消##WorkshopExport")) ImGui.CloseCurrentPopup();
        }
        finally { ImGui.EndPopup(); }
    }

    private void DrawWorkshopBackupInfo()
    {
        if (super is null) return;
        if (DateTimeOffset.TryParse(super.CapturedAt, out var read)) ImGui.TextDisabled($"上次读取游戏配置：{read.ToLocalTime():yyyy-MM-dd HH:mm}");
        var source = super.MacroBackupName.Length > 0 ? super.MacroBackupName
            : super.MacroBackupCharacter.Length > 0 ? "角色 " + super.MacroBackupCharacter : "旧档案，来源未记录";
        var timestamp = DateTimeOffset.TryParse(super.MacroBackupAt, out var at) ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "未记录";
        ImGui.TextWrapped($"用户宏备份：{source} · {timestamp}");
        ImGui.TextWrapped("接管栏和扩展栏都执行当前角色的用户宏；备份正文只用于主动恢复。更新宏备份不改动其他设置。");
    }
}
