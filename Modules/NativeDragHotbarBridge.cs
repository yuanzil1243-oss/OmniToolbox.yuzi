using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools;
using OmenTools.Extensions;
using OmniToolbox.Host;
using TreeTools.Lifecycle;
using NativeSlot = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using NativeType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed unsafe class NativeHotbarBridge : IDisposable
{
    private readonly Func<HotbarEntry, bool> receive;
    private readonly Func<bool> overTarget;
    private readonly NativeHotbarDragSession drag = new();
    private Hook<AtkDragDropManager.Delegates.StartDragDrop>? startHook;
    private Hook<AtkDragDropManager.Delegates.Drop>? dropHook;
    private Hook<AtkDragDropManager.Delegates.CancelDragDrop>? cancelHook;
    private NativeSlot* scratch;
    private delegate* unmanaged<RaptureHotbarModule*, NativeSlot*, byte> executeSlot;
    private delegate* unmanaged<uint*, short*, int, int, void> resolveInventory;
    private bool handling;
    private bool initialized;
    public string LastDrag { get; private set; } = "尚未检测到原生拖动";
    public bool Ready { get; private set; }
    public bool IsDragging => AtkStage.Instance() != null && AtkStage.Instance()->DragDropManager.IsDragging;

    public NativeHotbarBridge(Func<HotbarEntry, bool> receive, Func<bool> overTarget)
    { this.receive = receive; this.overTarget = overTarget; }

    public void Start(FeatureLifetime lifetime)
    {
        var wrapper = (byte*)RaptureHotbarModule.MemberFunctionPointers.ExecuteSlotById;
        if (wrapper == null) throw new InvalidOperationException("原生热键执行入口未就绪。");
        var target = NativeHotbarExecution.Resolve(new ReadOnlySpan<byte>(wrapper, 93), (long)wrapper);
        // Confirm that the callee reads CommandType from its second argument (HotbarSlot*).
        var inner = (byte*)target;
        if (!new ReadOnlySpan<byte>(inner + 18, 7).SequenceEqual(new byte[] { 0x0F, 0xB6, 0x82, 0xC7, 0, 0, 0 }))
            throw new InvalidOperationException("原生热键格执行函数布局不匹配。");
        executeSlot = (delegate* unmanaged<RaptureHotbarModule*, NativeSlot*, byte>)inner;
        try
        {
            var site = (byte*)DService.Instance().SigScanner.ScanText(NativeHotbarInventory.Signature);
            var converter = (byte*)NativeHotbarInventory.Resolve(new ReadOnlySpan<byte>(site, 38), (long)site);
            if (!new ReadOnlySpan<byte>(converter, 14).SequenceEqual(new byte[] {0x40,0x53,0x55,0x56,0x57,0x41,0x56,0x48,0x81,0xEC,0x80,0,0,0}))
                throw new InvalidOperationException("背包位置转换函数布局不匹配。");
            resolveInventory = (delegate* unmanaged<uint*, short*, int, int, void>)converter;
        }
        catch (Exception error)
        {
            DalamudServices.PluginLog.Warning(error, "拖放热键栏：背包位置转换不可用，其他拖动来源仍可使用");
        }
        // Allocate our own working slot. Never borrow, overwrite, or save a real game hotbar slot.
        scratch = (NativeSlot*)NativeMemory.AllocZeroed((nuint)sizeof(NativeSlot));
        lifetime.Add(this);
        scratch->Initialize();
        initialized = true;
        startHook = DService.Instance().Hook.HookFromMemberFunction<AtkDragDropManager.Delegates.StartDragDrop>(
            typeof(AtkDragDropManager.MemberFunctionPointers), "StartDragDrop", OnStartDrag);
        lifetime.Add(startHook);
        lifetime.OnStopping(startHook.Disable);
        dropHook = DService.Instance().Hook.HookFromMemberFunction<AtkDragDropManager.Delegates.Drop>(
            typeof(AtkDragDropManager.MemberFunctionPointers), "Drop", OnDrop);
        lifetime.Add(dropHook);
        lifetime.OnStopping(dropHook.Disable);
        cancelHook = DService.Instance().Hook.HookFromMemberFunction<AtkDragDropManager.Delegates.CancelDragDrop>(
            typeof(AtkDragDropManager.MemberFunctionPointers), "CancelDragDrop", OnCancel);
        lifetime.Add(cancelHook);
        lifetime.OnStopping(cancelHook.Disable);
        startHook.Enable();
        dropHook.Enable();
        cancelHook.Enable();
        Ready = true;
    }

    private void OnStartDrag(AtkDragDropManager* manager, AtkDragDropInterface* source, short x, short y)
    {
        startHook!.Original(manager, source, x, y);
        drag.Begin((nint)source);
        Capture(manager);
    }

    // These callbacks run before the game's drop handler can clear a source hotbar slot.
    private void OnDrop(AtkDragDropManager* manager, AtkEventData.AtkDragDropData* data,
        AtkComponentNode* node, DragDropType accepted, bool cannotAccept)
    {
        if (!handling && TryReceive(manager))
        {
            handling = true;
            try { cancelHook!.Original(manager, true, true); }
            finally { handling = false; drag.End(); }
            return;
        }
        drag.End();
        dropHook!.Original(manager, data, node, accepted, cannotAccept);
    }

    private void OnCancel(AtkDragDropManager* manager, bool sound, bool suppressFlyBack)
    {
        if (!handling && TryReceive(manager)) suppressFlyBack = true;
        drag.End();
        cancelHook!.Original(manager, sound, suppressFlyBack);
    }

    // ImGui can consume the release before the game dispatches Drop/CancelDragDrop.
    // Poll after this frame's destination rectangles have been registered.
    public void PollRelease()
    {
        var stage = AtkStage.Instance();
        if (!Ready || stage == null || !stage->DragDropManager.IsDragging) { drag.End(); return; }
        var manager = &stage->DragDropManager;
        if (!TryReceive(manager)) return;
        handling = true;
        try { cancelHook!.Original(manager, true, true); }
        finally { handling = false; drag.End(); }
    }

    private bool Capture(AtkDragDropManager* manager)
    {
        try
        {
            if (!Ready || manager == null || !manager->IsDragging || manager->DragDrop1 == null) return false;
            var source = (nint)manager->DragDrop1;
            if (!drag.Active || drag.Source != source) drag.Begin(source);
            var supported = TryRead(manager, out var entry);
            drag.Observe(IsKeyDown(0x01), IsKeyDown(0x1B) || IsKeyDown(0x02), IsGameForeground(), supported ? entry : null);
            return supported;
        }
        catch (Exception error)
        {
            LastDrag = "无法识别此来源：" + error.Message;
            return false;
        }
    }

    private bool TryReceive(AtkDragDropManager* manager)
    {
        try
        {
            if (!Ready || manager == null || !manager->IsDragging) return false;
            Capture(manager);
            var accepted = drag.Release(IsKeyDown(0x01), IsKeyDown(0x1B) || IsKeyDown(0x02),
                IsGameForeground(), overTarget(), receive);
            if (accepted) LastDrag += " · 已接收（复制）";
            return accepted;
        }
        catch (Exception error)
        {
            DalamudServices.PluginLog.Error(error, "拖放热键栏：接收原生拖放失败，交回游戏处理");
            return false;
        }
    }

    public bool Preview()
    {
        var stage = AtkStage.Instance();
        if (!Ready || stage == null || !stage->DragDropManager.IsDragging) return false;
        return Capture(&stage->DragDropManager);
    }

    private bool TryRead(AtkDragDropManager* manager, out HotbarEntry entry)
    {
        entry = new();
        var source = manager->DragDrop1;
        if (source == null) return false;
        var payload = source->GetPayloadContainer();
        if (payload == null) return false;
        var dragType = source->DragDropType;
        LastDrag = $"{dragType} · 数据 {payload->Int1} / {payload->Int2}";
        // Action-bar payloads may contain source location/context rather than a plain action ID.
        // Resolve the exact source component to the working native slot before interpreting payload integers.
        if (TryReadHotbarSource(source, out entry)) return true;
        // Use the client's own type mapping before handling transient inventory locations.
        var type = UIGlobals.GetHotbarSlotTypeFromDragDropType(dragType);
        LastDrag += $" · 原生类型 {(byte)type}";
        if (type == NativeType.InventoryItem)
        {
            if (resolveInventory == null) { LastDrag += " · 背包位置转换不可用"; return false; }
            uint itemId = 9999;
            short resolvedType = -1;
            resolveInventory(&itemId, &resolvedType, payload->Int1, payload->Int2);
            if (!NativeHotbarInventory.TryNormalizeResult(itemId, resolvedType, out var normalizedType, out var normalizedId))
            { LastDrag += $" · 物品位置转换失败（{itemId}/{resolvedType}）"; return false; }
            LastDrag += $" · 物品 {normalizedId} / 类型 {normalizedType}";
            return Describe((NativeType)normalizedType, normalizedId, out entry);
        }
        if (type is NativeType.KeyItem or NativeType.Crystal)
        {
            if (payload->Int2 < 0 || payload->Int2 > short.MaxValue) return false;
            return Describe(type, (uint)payload->Int2, out entry);
        }
        // Keep the established non-inventory source handling unchanged.
        if (dragType.ToString().StartsWith("ActionBar", StringComparison.Ordinal)) return false;
        if (dragType is DragDropType.Inventory_Item or DragDropType.Inventory_Crystal
            or DragDropType.RemoteInventory_Item or DragDropType.Macro_Text
            or DragDropType.EventItem or DragDropType.Crystal or DragDropType.EquipmentSlot) return false;
        if (!NativeHotbarPayload.TryDecode((byte)type, payload->Int1, payload->Int2, out var id)) return false;
        return Describe(type, id, out entry);
    }

    private bool TryReadHotbarSource(AtkDragDropInterface* source, out HotbarEntry entry)
    {
        entry = new();
        var node = source->GetComponentNode();
        if (node == null) return false;
        var addon = node->GetOwnerAddon();
        if (addon == null) return false;
        var name = addon->NameString;
        // Double-cross bars remap their slot indices and are intentionally excluded in this first release.
        if (!name.StartsWith("_ActionBar", StringComparison.Ordinal)
            && !name.StartsWith("_ActionCross", StringComparison.Ordinal)) return false;
        var bar = (AddonActionBarBase*)addon;
        var slots = bar->ActionBarSlotVector;
        if (slots.LongCount is < 1 or > 32) return false;
        var module = RaptureHotbarModule.Instance();
        if (module == null) return false;
        for (var i = 0; i < slots.LongCount; i++)
        {
            var slot = slots[i];
            if (slot.ComponentDragDrop == null || &slot.ComponentDragDrop->AtkDragDropInterface != source) continue;
            // ActionBarSlot.ActionId is the displayed action ID, not the index within the bar.
            // The vector position is the actual slot index; RaptureHotbarId also tracks bar paging.
            if (bar->RaptureHotbarId >= 18 || i >= 16) return false;
            var native = module->GetSlotById(bar->RaptureHotbarId, (uint)i);
            if (native == null || !HotbarRules.IsSupportedType((byte)native->CommandType)
                || !HotbarRules.IsValidId((byte)native->CommandType, native->CommandId)) return false;
            LastDrag = $"{name} · 原生栏 {bar->RaptureHotbarId + 1} / 格 {i + 1} · {native->CommandType}:{native->CommandId}";
            return Describe(native->CommandType, native->CommandId, out entry);
        }
        return false;
    }

    private bool Describe(NativeType type, uint id, out HotbarEntry entry)
    {
        entry = new();
        if (scratch == null || UIModule.Instance() == null) return false;
        scratch->Clear();
        scratch->Set(type, id);
        if (!NativeHotbarInventory.IsNormalized((byte)type, id, (byte)scratch->CommandType, scratch->CommandId))
        { LastDrag += " · 原生动作解析失败"; return false; }
        scratch->LoadIconId();
        var name = scratch->GetDisplayNameForSlot(scratch->ApparentSlotType, scratch->ApparentActionId).ToString();
        if (scratch->IconId == 0 || string.IsNullOrWhiteSpace(name)) { LastDrag += " · 无可用名称或图标"; return false; }
        entry = new() { NativeType = (byte)scratch->CommandType, NativeId = scratch->CommandId, IconId = scratch->IconId,
            Name = name, Tooltip = name, Mode = HotbarEntryMode.Native };
        return true;
    }

    public void Execute(HotbarEntry entry)
    {
        if (!entry.HasNative || scratch == null) return;
        var module = RaptureHotbarModule.Instance();
        if (module == null || UIModule.Instance() == null) return;
        scratch->Set((NativeType)entry.NativeType, entry.NativeId);
        executeSlot(module, scratch);
    }

    public uint CurrentIcon(HotbarEntry entry)
    {
        if (!entry.HasNative || scratch == null || UIModule.Instance() == null) return entry.IconId;
        scratch->Set((NativeType)entry.NativeType, entry.NativeId);
        scratch->LoadIconId();
        return scratch->IconId > 0 ? scratch->IconId : entry.IconId;
    }

    public void Dispose()
    {
        Ready = false;
        drag.End();
        if (scratch == null) return;
        try { if (initialized) scratch->PopUpHelp.Dtor(); }
        finally { NativeMemory.Free(scratch); scratch = null; initialized = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    internal static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool IsLeftReleased() => !IsKeyDown(0x01);
    internal static bool IsGameForeground()
    { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return pid == Environment.ProcessId; }
    internal static bool TryGetGameClientOrigin(out Point point)
    {
        point = default;
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var pid);
        return pid == Environment.ProcessId && ClientToScreen(window, ref point);
    }
}
