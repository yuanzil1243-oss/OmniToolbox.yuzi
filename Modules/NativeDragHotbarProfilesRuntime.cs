using System;
using System.IO;
using OmenTools;
using OmniToolbox.Host;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private WorkshopLibrary? workshopLibrary;
    private bool workshopRecovered, workshopDirty, workshopLoadAttempted;
    private string workshopRuntimeId = "", workshopSaveError = "";
    private string WorkshopDirectory => DalamudServices.PluginInterface.GetPluginConfigDirectory();

    private void EnsureWorkshopLibrary()
    {
        if (workshopLibrary is not null || owner == 0 || workshopLoadAttempted) return;
        workshopLoadAttempted = true;
        try
        {
            var library = WorkshopLibraryStore.Load(WorkshopDirectory, owner, config.ShowCommandMessages,
                out var recovered, out var migrated);
            WorkshopVisibilityStore.Replay(WorkshopDirectory, library);
            workshopLibrary = library; workshopRecovered = recovered;
            profile = library.Current.Extensions; config.ShowCommandMessages = library.Current.ShowCommandMessages;
            profileLoaded = true; readOnly = superReadOnly = false; recovering = false;
            dirty = PrepareEntryIcons(); workshopSaveError = saveError = "";
            if (migrated) superStatus = "已建立本机配置库，旧角色档案和扩展栏文件已保留。";
            else if (recovered) superStatus = "配置库已从备份恢复，损坏文件已另存。";
        }
        catch (Exception error)
        {
            readOnly = superReadOnly = true; profileLoaded = false; workshopSaveError = error.Message;
            superStatus = "本机配置库不可读，已暂停编辑和接管：" + error.Message;
            DalamudServices.PluginLog.Error(error, "热键栏工坊：本机配置库读取失败");
        }
    }

    private void ResetWorkshopRuntime()
    {
        takeoverViewport.Reset();
        nativeDetails?.Release(); superReady = false; RestoreSuperVisibility();
        ClearWorkshopGameOperation(); workshopGameReadyAt = 0; workshopGameSaveWatch = null;
        workshopGameLiveCheck = superRestoreLiveCheck = null;
        superRestoreLayoutTarget = superRestoreLayoutBefore = null; superRestoreLayoutBackupPath = "";
        workshopGameStatus = workshopGameBackupPath = "";
        builtInRepeat.Clear(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
        superGroup = -1; superPage = 0; superRecording = -1; superBindingDraft = null;
        superFault = false; superHudEditing = false;
        restoreConfirm = false; superRestoreResult = superRestoreBackupPath = ""; superRestorePreview = null; superRestoreSaveWatch = null;
        superVisuals.Clear(); targets.Clear(); received.Clear(); replacing = null; CloseEditor();
        movingBar = localDragBar = null; localDragIndex = -1; layoutBarId = ""; selected = 0;
        CollapseSettingsBars(); ClearSuperImport(); superResetConfirm = 0;
    }

    private void BindWorkshopCharacter()
    {
        ResetWorkshopRuntime(); superOwner = owner;
        workshopRuntimeId = workshopLibrary?.SelectedId ?? "";
        super = owner != 0 && workshopLibrary is not null ? WorkshopProfiles.Bind(workshopLibrary.Current, owner) : null;
        if (super is not null) superPage = super.FirstPage;
        superStatus = owner == 0 ? "请先登录角色。" : workshopLibrary is null ? superStatus
            : $"当前配置：{workshopLibrary.Current.Name}。" + (super is null ? "可直接使用扩展栏；接管请先读取游戏配置。" : "本机所有角色共用当前选择。");
    }

    private void SaveWorkshop(bool force)
    {
        if (workshopLibrary is null || readOnly || superReadOnly) return;
        var changed = dirty || superDirty || workshopDirty || workshopRecovered
            || config.ShowCommandMessages != workshopLibrary.Current.ShowCommandMessages;
        if (!changed || !force && Environment.TickCount64 < superSaveAt) return;
        try
        {
            PrepareEntryIcons();
            var current = workshopLibrary.Current;
            current.Extensions = profile; current.ShowCommandMessages = config.ShowCommandMessages;
            if (super is not null && superOwner != 0 && workshopRuntimeId == current.Id)
            {
                SuperHotbarRules.Validate(super, superOwner);
                current.Takeover = WorkshopProfiles.Portable(super);
            }
            WorkshopLibraryStore.Save(WorkshopDirectory, workshopLibrary, workshopRecovered);
            dirty = superDirty = workshopDirty = workshopRecovered = recovering = false;
            workshopSaveError = saveError = ""; superSaveAt = Environment.TickCount64 + 1000;
        }
        catch (Exception error)
        {
            workshopDirty = true; workshopSaveError = saveError = error.Message;
            superStatus = "配置保存失败，修改仍保留在内存：" + error.Message;
            superSaveAt = Environment.TickCount64 + 5000;
            DalamudServices.PluginLog.Error(error, "热键栏工坊：本机配置库保存失败");
        }
    }

    private bool ApplyWorkshopVisibilityCommand(bool takeover, HotbarVisibilityAction action, string selector)
    {
        if (workshopLibrary is null || readOnly || superReadOnly) return true;
        try
        {
            if (!WorkshopVisibilityCommands.Apply(workshopLibrary.Current, super, profile, takeover, action, selector,
                state => WorkshopVisibilityStore.Save(WorkshopDirectory, state), out var index, out var changed))
            { PrintCommandMessage(L("InvalidBar", selector)); return true; }
            var bars = takeover ? super!.Appearance : profile.Bars;
            var show = takeover ? super!.Show : profile.Show;
            PrintCommandMessage(HotbarCommandFeedback.Visibility(takeover, index,
                index < 0 ? "" : bars[index].Name, show && (index < 0 || bars[index].Visible)));
        }
        catch (Exception error)
        {
            superStatus = "显隐状态保存失败，本次显示操作未执行：" + error.Message;
            Notify(superStatus); PrintCommandMessage(superStatus);
            DalamudServices.PluginLog.Error(error, "热键栏工坊：显隐记录保存失败");
        }
        return true;
    }

    private void SetWorkshopVisibility(bool takeover, int index, bool visible)
    {
        if (workshopLibrary is null || readOnly || superReadOnly) return;
        try
        {
            WorkshopVisibilityCommands.Set(workshopLibrary.Current, super, profile, takeover, index, visible,
                state => WorkshopVisibilityStore.Save(WorkshopDirectory, state));
        }
        catch (Exception error)
        {
            superStatus = "显隐状态保存失败，本次显示操作未执行：" + error.Message;
            Notify(superStatus);
            DalamudServices.PluginLog.Error(error, "热键栏工坊：显隐记录保存失败");
        }
    }

    private bool CommitWorkshopEdit(Action<WorkshopLibrary> edit)
    {
        if (workshopLibrary is null || readOnly || superReadOnly) return false;
        SaveWorkshop(true);
        if (dirty || superDirty || workshopDirty || workshopSaveError.Length > 0)
        { superStatus = "当前配置尚未保存成功，请先重试保存；没有切换或覆盖配置。"; return false; }
        try
        {
            var candidate = WorkshopProfiles.Copy(workshopLibrary);
            edit(candidate); WorkshopProfiles.Validate(candidate);
            // Persist the complete candidate before replacing any live references.
            WorkshopLibraryStore.Save(WorkshopDirectory, candidate, false);
            ResetWorkshopRuntime(); workshopLibrary = candidate;
            profile = candidate.Current.Extensions; profileLoaded = true;
            config.ShowCommandMessages = candidate.Current.ShowCommandMessages; superHostResetDirty = true;
            readOnly = superReadOnly = recovering = workshopRecovered = false;
            dirty = superDirty = workshopDirty = false; workshopSaveError = saveError = "";
            BindWorkshopCharacter(); return true;
        }
        catch (Exception error)
        { superStatus = "操作未完成，当前配置已保留：" + error.Message; return false; }
    }

    private static string CurrentGameCharacterName() => DService.Instance().ObjectTable.LocalPlayer?.Name.ToString() ?? "";
}
