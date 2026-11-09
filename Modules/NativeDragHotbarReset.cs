using System;
using System.IO;
using System.Text;
using Dalamud.Bindings.ImGui;
using OmniToolbox.Host;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private int superResetConfirm;
    private bool superHostResetDirty;
    private void DrawSuperResetSettings()
    {
        if (!ImGui.CollapsingHeader("清理与重置")) return;
        if (ImGui.Button("清空当前配置的接管栏…"))
        { ClearSuperImport(); MarkWorkshopOperation(); superResetConfirm = 1; CancelSuperBindingEdit(); }
        ImGui.SameLine();
        if (ImGui.Button("清空当前配置的全部栏位…"))
        { ClearSuperImport(); MarkWorkshopOperation(); superResetConfirm = 2; CancelSuperBindingEdit(); }
        if (superResetConfirm == 0) return;
        ImGui.TextWrapped(superResetConfirm == 1
            ? "清空当前配置的接管档案、快捷键、布局和宏备份？扩展栏及其他配置保留。"
            : "清空当前配置的接管档案和扩展栏？其他配置保留。");
        ImGui.TextWrapped("操作会退出接管，游戏配置不变；清空前自动另存一份当前配置。旧版角色文件也会保留。");
        ImGui.BeginDisabled(!WorkshopOperationValid);
        if (ImGui.Button("确认清空当前配置")) ClearSuperData(superResetConfirm == 2);
        ImGui.EndDisabled();
        ImGui.SameLine(); if (ImGui.Button("取消清除")) superResetConfirm = 0;
    }

    private void ClearSuperData(bool all)
    {
        if (workshopLibrary is null || !WorkshopOperationValid) return;
        try
        {
            SaveWorkshop(true);
            if (workshopSaveError.Length > 0) throw new IOException("当前配置保存失败，请先重试。");
            var archive = Path.Combine(WorkshopDirectory, "HotbarWorkshop-reset"); Directory.CreateDirectory(archive);
            var path = Path.Combine(archive, workshopLibrary.Current.Id + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, WorkshopProfileTransfer.Encode(workshopLibrary.Current), new UTF8Encoding(false));
            if (CommitWorkshopEdit(library =>
            {
                library.Current.Takeover = null;
                if (all) { library.Current.Extensions = new() { Owner = "0000000000000000", Bars = [] }; library.Current.ShowCommandMessages = true; }
            })) superStatus = "已清空当前配置的" + (all ? "接管栏和扩展栏" : "接管栏") + "，其他配置及游戏设置保留。清空前备份：" + path;
        }
        catch (Exception error)
        {
            superStatus = "清空未完成，当前配置保留：" + error.Message;
            DalamudServices.PluginLog.Error(error, "热键栏工坊：清除数据失败");
        }
    }
}
