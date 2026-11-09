using System;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmniToolbox.Host;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed unsafe class NativeHotbarDetailTooltips : IDisposable
{
    private AtkResNode* anchor;
    private Utf8String* label;
    private AtkTooltipManager.AtkTooltipArgs* args;
    private bool nodeInitialized, textInitialized, faulted;
    private string previousLabel = "";
    private NativeHotbarDetail? previousDetail;
    private readonly NativeHotbarTooltipOwnership ownership = new();
    private readonly Func<nint> stageAddress;
    private readonly Func<int, nint> parentAddress;
    private readonly Func<bool> uiVisible;

    internal NativeHotbarDetailTooltips() : this(() => (nint)AtkStage.Instance(),
        bar => (nint)SuperNativeArchive.Addon(bar), NativeHotbarUiContext.Visible) { }

    internal NativeHotbarDetailTooltips(Func<nint> stage, Func<int, nint> parent, Func<bool> visible)
    { stageAddress = stage; parentAddress = parent; uiVisible = visible; }

    internal bool Ready => anchor != null && !faulted;
    internal bool OwnsCurrent
    {
        get
        {
            if (!ownership.Active) return false;
            var stage = (AtkStage*)stageAddress();
            return stage != null && ownership.Owns((nint)(&stage->TooltipManager),
                (nint)stage->TooltipManager.TargetNode, stage->TooltipManager.ParentAddonId);
        }
    }

    internal void Start()
    {
        Dispose(); faulted = false;
        try
        {
            if (AtkTooltipManager.MemberFunctionPointers.ShowTooltip == null
                || AtkTooltipManager.MemberFunctionPointers.HideTooltip == null
                || AtkTooltipManager.AtkTooltipArgs.MemberFunctionPointers.Ctor == null
                || AtkResNode.MemberFunctionPointers.Ctor == null)
                throw new InvalidOperationException("原生详情提示接口尚未就绪");
            anchor = (AtkResNode*)NativeMemory.AllocZeroed((nuint)sizeof(AtkResNode));
            label = (Utf8String*)NativeMemory.AllocZeroed((nuint)sizeof(Utf8String));
            args = (AtkTooltipManager.AtkTooltipArgs*)NativeMemory.AllocZeroed((nuint)sizeof(AtkTooltipManager.AtkTooltipArgs));
            anchor->Ctor(); nodeInitialized = true;
            label->Ctor(); textInitialized = true;
            anchor->NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
            // An unattached, module-owned anchor: no native slot, HUD node or UI tree is edited.
            anchor->ScaleX = anchor->ScaleY = 1;
            anchor->Transform.M11 = anchor->Transform.M22 = 1;
        }
        catch (Exception error) { Fail(error); Dispose(); }
    }

    internal void BeginFrame() => ownership.BeginFrame();
    internal void EndFrame() { if (ownership.EndFrameNeedsRelease) Release(); }
    internal void Tick(long now) { if (!uiVisible() || ownership.Expired(now)) Release(); }

    internal bool Show(NativeHotbarDetail detail, string text, int nativeBar, Vector2 min, Vector2 max, long now)
    {
        if (!Ready || !NativeHotbarDetail.ValidBounds(min, max)) return false;
        try
        {
            var stage = (AtkStage*)stageAddress();
            var parent = (AtkUnitBase*)parentAddress(nativeBar is >= 0 and < 10 ? nativeBar : 0);
            if (stage == null || !uiVisible() || parent == null
                || !parent->IsReady || parent->RootNode == null || parent->Id == 0) { Release(); return false; }
            if (previousDetail != detail || ownership.Active && ownership.Parent != parent->Id) Release();
            anchor->X = anchor->ScreenX = MathF.Round(min.X);
            anchor->Y = anchor->ScreenY = MathF.Round(min.Y);
            anchor->Width = (ushort)Math.Clamp((int)MathF.Round(max.X - min.X), 1, 1024);
            anchor->Height = (ushort)Math.Clamp((int)MathF.Round(max.Y - min.Y), 1, 1024);
            if (previousLabel != text) { label->SetString(text); previousLabel = text; }
            BuildArgs(args, label, detail);
            // Same call/default flags as AddonActionBarBase.ShowTooltip. The native enabled mask,
            // detail interfaces and timers own help visibility, fixed/mouse positioning and scale.
            stage->TooltipManager.ShowTooltip((AtkTooltipType)detail.Mask, parent->Id, anchor, args, null, false, true);
            ownership.Mark((nint)(&stage->TooltipManager), (nint)anchor, parent->Id, now);
            previousDetail = detail;
            return true;
        }
        catch (Exception error) { Release(); Fail(error); return false; }
    }

    internal static void BuildArgs(AtkTooltipManager.AtkTooltipArgs* target, Utf8String* text, NativeHotbarDetail detail)
    {
        // Ctor sets BuyQuantity=-1 and the native positioning byte at offset 23 to 1.
        // Zero-initializing instead would silently lose native tooltip positioning behavior.
        target->Ctor();
        target->TextArgs.Text = text->StringPtr;
        if ((detail.Mask & (byte)AtkTooltipType.Item) != 0)
        { target->ItemArgs.Kind = (DetailKind)detail.Kind; target->ItemArgs.ItemId = (int)detail.Id; }
        else
        { target->ActionArgs.Kind = (DetailKind)detail.Kind; target->ActionArgs.Id = (int)detail.Id; target->ActionArgs.Flags = detail.Flags; }
    }

    internal void Release()
    {
        try
        {
            var stage = ownership.Active ? (AtkStage*)stageAddress() : null;
            if (stage != null && ownership.Owns((nint)(&stage->TooltipManager),
                (nint)stage->TooltipManager.TargetNode, stage->TooltipManager.ParentAddonId))
                stage->TooltipManager.HideTooltip(ownership.Parent, true);
        }
        finally { ownership.Clear(); previousDetail = null; }
    }

    private void Fail(Exception error)
    {
        if (!faulted) DalamudServices.PluginLog.Warning(error, "热键栏工坊：原生详情暂不可用，保留文字提示");
        faulted = true;
    }

    public void Dispose()
    {
        Release();
        if (textInitialized) { label->Dtor(); textInitialized = false; }
        if (nodeInitialized) { anchor->Destroy(false); nodeInitialized = false; }
        NativeMemory.Free(args); args = null;
        NativeMemory.Free(label); label = null;
        NativeMemory.Free(anchor); anchor = null;
        previousLabel = "";
    }
}

public sealed partial class NativeDragHotbar
{
    private NativeHotbarDetailTooltips? nativeDetails;
    private bool macroDetailWarning;

    private void StartNativeDetails(TreeTools.Lifecycle.FeatureLifetime activation)
    {
        macroDetailWarning = false;
        nativeDetails = new(); nativeDetails.Start();
        activation.OnStopping(StopNativeDetails);
    }

    private void StopNativeDetails() { nativeDetails?.Dispose(); nativeDetails = null; }

    private bool TryNativeSlotTooltip(DragHotbar bar, HotbarEntry entry, Vector2 min, Vector2 max)
    {
        if (nativeDetails is not { Ready: true } || bridge is not { Ready: true }
            || !NativeHotbarBridge.IsGameForeground()) return false;
        try
        {
            if (!TrySlotDetail(bar, entry, out var detail, out var name)) return false;
            var viewport = Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Pos;
            return nativeDetails.Show(detail, NativeHotbarNames.Plain(name), SuperBarIndex(bar),
                min - viewport, max - viewport, Environment.TickCount64);
        }
        catch (Exception error)
        {
            if (!macroDetailWarning) DalamudServices.PluginLog.Warning(error, "热键栏工坊：内容详情解析失败，保留文字提示");
            macroDetailWarning = true;
            return false;
        }
    }

    private bool TrySlotDetail(DragHotbar bar, HotbarEntry entry, out NativeHotbarDetail detail, out string name)
    {
        detail = default; name = entry.Name;
        if (entry.HasNative)
        {
            if (entry.NativeType == 7)
            {
                if (!NativeHotbarBridge.TryReadMacro(entry.NativeId, out var text, out name)) return false;
                if (bridge!.TryMacroDetail(text, out detail)) return true;
            }
            else if (bridge!.TryDetail(entry, out detail)) return true;
        }
        return entry.Mode == HotbarEntryMode.Command && bridge!.TryMacroDetail(entry.Command, out detail);
    }
}
