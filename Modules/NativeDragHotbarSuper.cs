using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools;
using OmniToolbox.Host;
using TreeTools.Lifecycle;
using OmniToolbox.SuperHotbarInterop;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private SuperHotbarDocument? super;
    private SuperHotbarDocument? superImport;
    private readonly SuperKeyEdges superEdges = new();
    private readonly NativeHotbarIconVisual superVisuals = new();
    private readonly NativeHotbarGameFonts superFonts = new();
    private bool superNativeDrawSuppressed;
    private readonly NativeHotbarMouseSuppression superNativeMouse = new();
    private static readonly string[] SuperNativeAddonNames = Enumerable.Range(0, 10).Select(SuperNativeArchive.AddonName).ToArray();
    private ulong superOwner;
    private int superGroup = -1, superSelectedBar, superBindingSlot, superRecording = -1;
    private bool superReady, superFault, superReadOnly, superDirty;
    private string superStatus = "尚未读取原生配置。", superBindingMessage = "";
    private long superSaveAt;
    private long superLastDraw;
    private long superLastSettings, superKeyAttempts, superNativeDrawBlocks;
    private string superLastInput = "无";
    private bool restoreContents = true, restoreBindings = true, restoreOptions, restoreMacros, restoreCross, restoreConfirm;
    private SuperShortcut? superBindingDraft;
    private int superRecordingInput;
    private bool superRecordArmed;
    private int superPage;

    private string SuperPath => Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), $"SuperHotbar-{superOwner:X16}.json");
    private bool SuperActive => super is { Enabled: true } && superReady && !superFault && !superReadOnly
        && owner != 0 && owner == superOwner && bridge?.Ready == true;
    private bool ContainsBar(DragHotbar bar) => !readOnly && profile.Bars.Contains(bar)
        || super?.Appearance.Contains(bar) == true && SuperActive;
    private int SuperBarIndex(DragHotbar bar) => super?.Appearance.IndexOf(bar) ?? -1;

    private void StartSuper(FeatureLifetime activation)
    {
        try { superFonts.StartOwned(DalamudServices.PluginInterface.UiBuilder.CreateFontAtlas(
            Dalamud.Interface.ManagedFontAtlas.FontAtlasAutoRebuildMode.Async, false, "SuperHotbar native pixels")); }
        catch (Exception error) { DalamudServices.PluginLog.Warning(error, "热键栏工坊：游戏字体图集暂不可用，保留备用文字"); }
        activation.OnStopping(superFonts.Dispose);
        var names = SuperNativeAddonNames;
        DalamudServices.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, names, OnSuperNativeDraw);
        activation.OnStopping(() => DalamudServices.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, names, OnSuperNativeDraw));
        foreach (var lifecycle in new[] { AddonEvent.PostUpdate, AddonEvent.PreFinalize })
        {
            DalamudServices.AddonLifecycle.RegisterListener(lifecycle, names, OnSuperNativeMouse);
            activation.OnStopping(() => DalamudServices.AddonLifecycle.UnregisterListener(lifecycle, names, OnSuperNativeMouse));
        }
        StartSuperRepeatBridge(activation);
        DalamudServices.Framework.Update += UpdateSuper;
        activation.OnStopping(() => DalamudServices.Framework.Update -= UpdateSuper);
        activation.OnStopping(StopSuper);
    }

    private void OnSuperNativeDraw(AddonEvent type, AddonArgs args)
    {
        if (!SuperActive || !super!.HideNative || !NativeHotbarUiContext.Visible() || NativeBarGeometryReader.HudEditing()) { RestoreSuperVisibility(); return; }
        // This host event runs at the actual native Draw call. Root visibility alone can be
        // reset by the game's update and therefore is insufficient to replace a native bar.
        args.PreventOriginal();
        superNativeDrawSuppressed = true;
        superNativeDrawBlocks++;
        OnSuperNativeMouse(type, args);
    }
    private void OnSuperNativeMouse(AddonEvent type, AddonArgs args)
    {
        try
        {
            var index = Array.IndexOf(SuperNativeAddonNames, args.AddonName);
            if (index < 0) return;
            if (type == AddonEvent.PreFinalize || !SuperActive || !super!.HideNative || !NativeHotbarUiContext.Visible() || NativeBarGeometryReader.HudEditing())
                superNativeMouse.Release(index);
            else superNativeMouse.Suppress(index);
        }
        catch (Exception error) { PauseSuper(error); }
    }
    private void StopSuper()
    {
        builtInRepeat.Clear(); superResetConfirm = 0;
        if (dirty && super is not null) superDirty = true;
        SaveSuper(true); superReady = false; RestoreSuperVisibility();
        superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); superRecording = -1;
    }

    private void ExitSuperTakeover()
    {
        nativeDetails?.Release();
        builtInRepeat.Clear(); superResetConfirm = 0;
        // Release execution and draw suppression before saving the disabled state.
        superReady = false;
        if (super is not null) { super.Enabled = false; superDirty = true; }
        superGroup = -1; ClearSuperImport(); restoreConfirm = false;
        superVisuals.Clear();
        CancelSuperBindingEdit(); CloseEditor(); replacing = null; received.Clear();
        localDragBar = null; movingBar = null;
        RestoreSuperVisibility(); SaveSuper(true);
        superStatus = superDirty ? "已退出接管，但设置保存失败，请重试保存。"
            : "已退出接管，正在使用游戏热键栏。模块配置已保留。";
    }
    private void PauseSuper(Exception error)
    {
        nativeDetails?.Release();
        superFault = true; superReady = false; superRecording = -1;
        superStatus = "接管暂停：" + error.Message;
        RestoreSuperVisibility();
    }
    private void EnsureSuper()
    {
        if (superOwner == owner && workshopRuntimeId == (workshopLibrary?.SelectedId ?? "")) return;
        SaveSuper(true);
        BindWorkshopCharacter();
    }

    private void SaveSuper(bool force)
    {
        SaveWorkshop(force);
    }

    private unsafe void UpdateSuper(IFramework framework)
    {
        try
        {
            nativeDetails?.Tick(Environment.TickCount64);
            EnsureProfile(); EnsureSuper();
            UpdateWorkshopGameTemplate();
            UpdateSuperRestoreSaveWatch();
            if (super is not null && SuperHotbarRules.UpgradePresentation(super, SuperHotbarKeyMapRuntime.Current))
            { superDirty = true; SaveSuper(true); }
            if (super is null || !super.Enabled || superFault || superReadOnly || owner == 0)
            {
                superReady = false; RestoreSuperVisibility(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); return;
            }
            if (bridge?.Ready != true)
            {
                superReady = false; RestoreSuperVisibility(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
                superStatus = "原生桥接不可用，已停止模块按键与拦截；请停用后重新启用模块并检查日志。"; return;
            }
            if (!NativeHotbarUiContext.Visible()) { SuspendHotbarUi(); return; }
            var native = SuperNativeArchive.Require(owner);
            UpdateSuperHudContext();
            UpgradeSuperGeometry();
            if (Environment.TickCount64 - superLastSettings > 500)
            { superRecording = -1; superBindingDraft = null; restoreConfirm = false; ClearSuperImport(); superResetConfirm = 0; }
            // A pet may exist without replacing the main bar. Inspect the displayed bar identity.
            var main = (AddonActionBarBase*)SuperNativeArchive.Addon(0);
            if (main != null && NativeHotbarSafety.IsSpecialMainBar(main->RaptureHotbarId))
            {
                superReady = false; RestoreSuperVisibility(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
                superStatus = "游戏正在使用特殊／宠物替换栏，接管暂退回原生；普通状态恢复后自动继续。"; return;
            }
            var group = SuperNativeArchive.CurrentGroup(owner);
            if (group != superGroup)
            {
                SaveSuper(true); superGroup = group; superPage = super.FirstPage;
                superVisuals.Clear();
                CloseEditor(); replacing = null; received.Clear(); localDragBar = null; movingBar = null;
                superRecording = -1; superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
                for (var b = 0; b < 10; b++)
                    super.Appearance[b].Slots = SuperHotbarRules.Resolve(super, NativeHotbarIdentity.LogicalBar(b, superPage), group, bridge!.DescribeSaved);
                superDirty = true;
            }
            var atk = RaptureAtkModule.Instance();
            if (atk == null || !atk->IsUiVisible || Environment.TickCount64 - superLastDraw > 500)
            {
                superReady = false; RestoreSuperVisibility(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); return;
            }
            superReady = true;
            if (super.HideNative && !superHudEditing)
                for (var index = 0; index < 10; index++) superNativeMouse.Suppress(index);
            else RestoreSuperVisibility();
            var allowed = SuperInputAllowed();
            if (superRecording >= 0) { RecordSuperKey(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); }
            else { PollSuperKeys(allowed); PollBuiltInRepeat(allowed); }
            SaveSuper(false);
        }
        catch (InvalidOperationException error)
        {
            // Loading screens and job changes are transient: release native suppression, then retry next frame.
            superReady = false; RestoreSuperVisibility(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
            superStatus = error.Message;
        }
        catch (Exception error) { PauseSuper(error); DalamudServices.PluginLog.Error(error, "热键栏工坊运行暂停"); }
    }

    private void PollSuperKeys(bool allowed)
    {
        var down = NativeHotbarBridge.IsKeyDown;
        var modifiers = (down(16) ? 1 : 0) | (down(17) ? 2 : 0) | (down(18) ? 4 : 0);
        allowed &= !down(91) && !down(92);
        for (var key = 4; key <= 254; key++)
        {
            if (!superEdges.Press(key, down(key), allowed, modifiers)) continue;
            // Match the first binding once; identical imported bindings cannot execute multiple slots.
            var binding = SuperHotbarRules.FirstBinding(super!, key, modifiers);
            if (binding is null) continue;
            var id = binding.InputId;
            if (id is >= 45 and <= 56)
            {
                superPage = id == 45 ? (superPage + 1) % 10 : id == 46 ? (superPage + 9) % 10 : id - 47;
                super!.FirstPage = superPage; superDirty = true;
                superVisuals.Clear();
                super.Appearance[0].Slots = SuperHotbarRules.Resolve(super, superPage, superGroup, bridge!.DescribeSaved);
                CloseEditor(); replacing = null; received.Clear(); localDragBar = null;
                Notify($"主栏已切换至第 {superPage + 1} 页"); continue;
            }
            if (NativeHotbarIdentity.TryInput(id, out var bar, out var slot)) ExecuteSuperPosition(bar, slot);
        }
    }

    private void ExecuteSuperPosition(int displayBar, int slot)
    {
        var logicalBar = NativeHotbarIdentity.LogicalBar(displayBar, superPage);
        var entries = SuperHotbarRules.Resolve(super!, logicalBar, superGroup, bridge!.DescribeSaved);
        superVisuals.Trigger(super!.Appearance[displayBar].Id, slot, VisualIdentity(entries[slot]), Environment.TickCount64 / 1000.0);
        Execute(entries[slot], true);
        superKeyAttempts++; superLastInput = $"显示第 {displayBar + 1} 栏"
            + (displayBar == 0 ? $"（内容页 {logicalBar + 1}）" : "") + $" / 格 {slot + 1} · {entries[slot].Name}";
    }

    private unsafe void DrawSuperBars(bool nativeDragging, bool supported)
    {
        superLastDraw = Environment.TickCount64;
        if (!SuperActive) { RestoreSuperVisibility(); return; }
        if (!NativeHotbarUiContext.Visible()) { SuspendHotbarUi(); return; }
        UpdateSuperHudContext();
        if (super!.Show && !takeoverViewport.Hidden)
            for (var index = 0; index < 10; index++)
                if (super.Appearance[index].Visible)
                    DrawSuperBar(super.Appearance[index], index, nativeDragging, supported);
        if (!super.HideNative || superHudEditing) RestoreSuperVisibility();
    }
    private void RestoreSuperVisibility()
    {
        superNativeMouse.ReleaseAll();
        // Root visibility and saved HUD flags remain game-owned.
        superNativeDrawSuppressed = false;
    }

    private void DrawSuperSlotOverlay(ImDrawListPtr list, DragHotbar bar, int slot, Vector2 min, Vector2 max)
    {
        var index = SuperBarIndex(bar); if (index < 0 || !SuperActive) return;
        var entry = bar.Slots[slot];
        var state = entry.HasNative ? bridge!.State(entry) : NativeSlotState.Neutral;
        var binding = super!.Bindings.FirstOrDefault(b => b.InputId == NativeHotbarIdentity.InputId(index, slot));
        var label = binding is null ? "" : binding.Primary.Key != 0 ? binding.Primary.Compact()
            : binding.Secondary.Key != 0 ? binding.Secondary.Compact()
            : binding.Primary.UnmappedNativeKey != 0 ? binding.Primary.Compact() : binding.Secondary.Compact();
        superVisuals.DrawStyled(list, bar.Id, slot,
            VisualIdentity(entry), state, label, min, max, Environment.TickCount64 / 1000.0,
            ImGui.GetStyle().Alpha, superFonts,
            superFonts.Started ? NativeHotbarFontReader.Read(index, slot) : NativeHotbarTextStyles.Default, true);
    }

    private static ulong VisualIdentity(HotbarEntry entry)
    {
        // Include commands/manual icon changes so a replaced slot cannot inherit an old pulse.
        var hash = ((ulong)entry.NativeType << 32) | entry.NativeId;
        hash = unchecked(hash * 1099511628211UL ^ entry.IconId);
        foreach (var character in entry.Command) hash = unchecked((hash ^ character) * 1099511628211UL);
        return hash;
    }

}
