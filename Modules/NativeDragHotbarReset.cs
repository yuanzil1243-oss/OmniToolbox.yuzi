using System;
using Dalamud.Bindings.ImGui;
using OmniToolbox.Host;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private int superResetConfirm;
    private bool superHostResetDirty;
    private void DrawSuperResetSettings()
    {
        ImGui.Separator();
        if (ImGui.Button("清除所有接管…")) { superResetConfirm = 1; CancelSuperBindingEdit(); }
        ImGui.SameLine();
        if (ImGui.Button("清除所有热键栏（包括扩展栏）…")) { superResetConfirm = 2; CancelSuperBindingEdit(); }
        if (superResetConfirm == 0) return;
        ImGui.TextWrapped(superResetConfirm == 1
            ? "确认清除本机所有角色的接管档案、键位、布局、内置连点设置及其自动备份？共享扩展栏保留。"
            : "确认清除本机所有角色的接管数据，以及所有账号共用的扩展栏、旧角色扩展栏与自动备份？清除后扩展栏数量为 0。");
        ImGui.TextWrapped("清除后退出接管，游戏配置不变。旧文件会保留在配置目录的 SuperHotbar-reset 文件夹中，不再自动载入。");
        if (ImGui.Button(superResetConfirm == 1 ? "确认清除全部接管数据" : "确认清除全部热键栏数据")) ClearSuperData(superResetConfirm == 2);
        ImGui.SameLine(); if (ImGui.Button("取消清除")) superResetConfirm = 0;
    }

    private void ClearSuperData(bool all)
    {
        // Release the native game first. Do not write old in-memory data after file reset.
        superReady = false; RestoreSuperVisibility();
        if (super is not null) super.Enabled = false;
        builtInRepeat.Clear(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
        CancelSuperBindingEdit(); CloseEditor(); replacing = null; received.Clear(); targets.Clear();
        movingBar = null; localDragBar = null; localDragIndex = -1; ClearSuperImport(); restoreConfirm = false;
        try
        {
            var archive = SuperHotbarResetStore.Clear(DalamudServices.PluginInterface.GetPluginConfigDirectory(), all);
            super = null; superUnsaved.Clear(); superDirty = false; superRecovered = false; superReadOnly = false;
            superFault = false; superGroup = -1; superPage = 0; superVisuals.Clear(); superResetConfirm = 0;
            superRestoreResult = ""; superRestoreBackupPath = "";
            if (all)
            {
                profile = new() { Owner = "0000000000000000", Bars = [] }; profileLoaded = true;
                dirty = false; readOnly = false; recovering = false; saveError = "";
                config = new(); superHostResetDirty = true;
                selected = 0; pendingImport = null; reposition.Clear(); layoutBarId = "";
            }
            superStatus = "已初始化" + (all ? "全部接管与扩展栏" : "全部角色接管")
                + "；原生游戏设置未修改。需要重新建立接管档案时，请点“读取原生配置”。清除前手动归档：" + archive;
        }
        catch (Exception error)
        {
            // Keep the old archive available after rollback, but never resume suppression on failure.
            superDirty = super is not null;
            superStatus = "清除失败，已停止接管并尝试回滚文件；旧档案仍保留。详情：" + error.Message;
            DalamudServices.PluginLog.Error(error, "热键栏工坊：清除数据失败");
        }
    }
}
