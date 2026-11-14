using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools;
using OmenTools.Extensions;
using OmniToolbox.Host;
using TreeTools.Lifecycle;
using NativeSlot = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using NativeType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed unsafe class NativeHotbarBridge : IDisposable
{
    private readonly Func<HotbarEntry, bool> receive;
    private readonly Func<bool> overTarget;
    private readonly NativeHotbarDragSession drag = new();
    private Hook<AtkDragDropManager.Delegates.StartDragDrop>? startHook;
    private Hook<AtkDragDropManager.Delegates.Drop>? dropHook;
    private Hook<AtkDragDropManager.Delegates.CancelDragDrop>? cancelHook;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ExecuteSlotDelegate(RaptureHotbarModule* module, NativeSlot* slot);
    private Hook<ExecuteSlotDelegate>? executeSlotHook;
    internal Func<bool>? SuppressStandardExecution { get; set; }
    private NativeSlot* scratch;

    private readonly NativeHotbarMacroDetails macroDetails = new();
    private delegate* unmanaged<RaptureHotbarModule*, NativeSlot*, byte> executeSlot;
    private delegate* unmanaged<uint*, short*, int, int, void> resolveInventory;
    private bool handling;
    private bool initialized;
    public string LastDrag { get; private set; } = "尚未检测到原生拖动";
    public bool Ready { get; private set; }
    public long SuppressedExecutions { get; private set; }
    public bool IsDragging => AtkStage.Instance() != null && AtkStage.Instance()->DragDropManager.IsDragging;

    public NativeHotbarBridge(Func<HotbarEntry, bool> receive, Func<bool> overTarget)
    { this.receive = receive; this.overTarget = overTarget; }

    public void Start(FeatureLifetime lifetime)
    {
        var wrapper = (byte*)RaptureHotbarModule.MemberFunctionPointers.ExecuteSlotById;
        if (wrapper == null) throw new InvalidOperationException("原生热键执行入口未就绪。");
        var target = NativeHotbarExecution.Resolve(ReadCodeSnapshot(wrapper, 93), (long)wrapper);
        var inner = (byte*)target;
        NativeHotbarExecution.ValidateSlotExecutor(ReadCodeSnapshot(inner, 25));
        executeSlot = (delegate* unmanaged<RaptureHotbarModule*, NativeSlot*, byte>)inner;
        try
        {
            var site = (byte*)DService.Instance().SigScanner.ScanText(NativeHotbarInventory.Signature);
            var converter = (byte*)NativeHotbarInventory.Resolve(ReadCodeSnapshot(site, 38), (long)site);
            if (!ReadCodeSnapshot(converter, 14).SequenceEqual(new byte[] {0x40,0x53,0x55,0x56,0x57,0x41,0x56,0x48,0x81,0xEC,0x80,0,0,0}))
                throw new InvalidOperationException("背包位置转换函数布局不匹配。");
            resolveInventory = (delegate* unmanaged<uint*, short*, int, int, void>)converter;
        }
        catch (Exception error)
        {
            DalamudServices.PluginLog.Warning(error, "热键栏工坊：背包位置转换不可用，其他拖动来源仍可使用");
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
        // Hook the verified pointer executor, covering indexed calls and direct native slot calls.
        // The ClientStructs alias for the indexed wrapper has conflicting ABI verification metadata.
        executeSlotHook = DService.Instance().Hook.HookFromAddress<ExecuteSlotDelegate>((nint)inner, OnExecuteSlot);
        lifetime.Add(executeSlotHook);
        lifetime.OnStopping(executeSlotHook.Disable);
        executeSlotHook.Enable();
        Ready = true;
    }

    private static ReadOnlySpan<byte> ReadCodeSnapshot(byte* live, int length)
    {
        // Other plugins legitimately detour live entrypoints. Validate the original game text
        // copied by the host before hooks, then install through the host's normal hook chain.
        // Relative branches are still resolved against the LIVE address, never the copy address.
        var scanner = DService.Instance().SigScanner;
        var address = NativeHotbarExecution.SnapshotAddress((long)live, (long)scanner.Module.BaseAddress,
            (long)scanner.SearchBase, scanner.TextSectionOffset, scanner.TextSectionSize, length);
        return new ReadOnlySpan<byte>((void*)address, length);
    }

    private byte OnExecuteSlot(RaptureHotbarModule* module, NativeSlot* slot)
    {
        try
        {
            if (module != null && slot != null && SuppressStandardExecution?.Invoke() == true
                && module == RaptureHotbarModule.Instance())
            {
                var first = (NativeSlot*)Unsafe.AsPointer(ref module->Hotbars[0].Slots[0]);
                if (NativeHotbarSafety.IsOrdinarySlot((long)slot, (long)first, sizeof(NativeSlot)))
                { SuppressedExecutions++; return 0; }
            }
        }
        catch (Exception error)
        {
            SuppressStandardExecution = null;
            Ready = false;
            DalamudServices.PluginLog.Error(error, "热键栏工坊：原生执行拦截失败，已交回游戏");
        }
        return executeSlotHook!.Original(module, slot);
    }

    public HotbarEntry DescribeSaved(SuperSavedSlot saved)
    {
        if (saved.Type == 0) return new();
        if (HotbarRules.IsSupportedType(saved.Type) && HotbarRules.IsValidId(saved.Type, saved.Id))
        {
            if (Describe((NativeType)saved.Type, saved.Id, out var result))
            { if (saved.Type == 7) result.IconOverride = true; return result; }
            return new() { NativeType = saved.Type, NativeId = saved.Id, IconId = 234001,
                Name = $"原生类型 {saved.Type} / {saved.Id}", Tooltip = "当前角色暂时无法解析此动作的图标或名称，身份已保留。" };
        }
        return new() { PreservedNativeType = saved.Type, PreservedNativeId = saved.Id, IconId = 234001,
            Name = $"已备份类型 {saved.Type} / {saved.Id}", Tooltip = "此类型暂未支持执行，原始数据会在还原时保留。" };
    }

    public NativeSlotState State(HotbarEntry entry)
    {
        if (!entry.HasNative || scratch == null || UIModule.Instance() == null) return NativeSlotState.Neutral;
        if (entry.NativeType == (byte)NativeType.GeneralAction) return GeneralState(entry);
        if (entry.NativeType == (byte)NativeType.Item) return ItemState(entry);
        if (entry.NativeType != 1) return AvailabilityState(entry);
        var actions = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (!NativeHotbarSafety.CanReadCombatState(entry.NativeType, entry.NativeId,
            actions.GetRowOrDefault(entry.NativeId) is not null)) return NativeSlotState.Neutral;
        PrepareAppearance(entry);
        if (!NativeHotbarSafety.CanReadCombatState((byte)scratch->ApparentSlotType, scratch->ApparentActionId,
            actions.GetRowOrDefault(scratch->ApparentActionId) is not null)) return NativeSlotState.Neutral;
        var seconds = 0;
        var percent = scratch->GetSlotActionCooldownPercentage(&seconds, 0);
        var level = DService.Instance().ObjectTable.LocalPlayer?.Level ?? 0;
        var maxCharges = level == 0 ? 1u : Math.Max(1u, ActionManager.GetMaxCharges(scratch->ApparentActionId, level));
        var charges = scratch->GetApparentIconRecastCharges();
        var manager = ActionManager.Instance();
        if (maxCharges > 1)
        {
            float total = 0, elapsed = 0;
            if (manager != null)
            {
                var group = manager->GetRecastGroup((int)ActionType.Action, scratch->ApparentActionId);
                // Current client ActionManager has exactly 80 RecastDetail entries.
                if (group is >= 0 and < 80)
                {
                    total = manager->GetRecastTimeForGroup(group);
                    elapsed = manager->GetRecastTimeElapsed(ActionType.Action, scratch->ApparentActionId);
                }
            }
            (percent, seconds) = NativeIconVisualRules.ChargeRecast(total, elapsed, maxCharges, charges, percent, seconds);
        }
        return new(Math.Clamp(percent, 0, 100), Math.Max(seconds, 0), charges, maxCharges,
            scratch->IsActionHighlighted(scratch->ApparentSlotType, scratch->ApparentActionId),
            scratch->IsSlotUsable(scratch->ApparentSlotType, scratch->ApparentActionId));
    }

    private NativeSlotState GeneralState(HotbarEntry entry)
    {
        // GeneralAction IDs are NOT Action IDs. In particular, never pass them into charge getters.
        var row = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>().GetRowOrDefault(entry.NativeId);
        if (!NativeHotbarSafety.CanReadGeneralState(entry.NativeType, entry.NativeId, row is not null)) return NativeSlotState.Neutral;
        PrepareAppearance(entry);
        // The native slot reader converts GeneralAction to its own recast group (e.g. Sprint 56,
        // Return 57). It also handles commands without an Action row. No charge getter is used.
        var seconds = 0;
        var percent = scratch->GetSlotActionCooldownPercentage(&seconds, 0);
        return new(Math.Clamp(percent, 0, 100), Math.Max(seconds, 0), 0, 1, false,
            scratch->IsSlotUsable(scratch->ApparentSlotType, scratch->ApparentActionId));
    }

    private NativeSlotState ItemState(HotbarEntry entry)
    {
        if (!NativeHotbarItems.TryItem(entry.NativeId, out var baseId, out _)) return NativeSlotState.Neutral;
        var row = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(baseId);
        if (!NativeHotbarSafety.CanReadItemState(entry.NativeType, entry.NativeId, row is not null)) return NativeSlotState.Neutral;
        PrepareAppearance(entry);
        // The native slot recast getter dispatches Item separately and includes shared potion recasts.
        // Never send an item ID into combat-action or charge getters.
        var seconds = 0;
        var percent = scratch->GetSlotActionCooldownPercentage(&seconds, 0);
        var quantity = scratch->GetCostValueForSlot(NativeType.Item, entry.NativeId);
        return NativeHotbarItems.State(percent, seconds, quantity,
            scratch->IsSlotUsable(scratch->ApparentSlotType, scratch->ApparentActionId));
    }

    private NativeSlotState AvailabilityState(HotbarEntry entry)
    {
        // The native usability dispatcher covers mounts, companions, event/duty items and commands.
        // It does not call the combat-only charge getter. Unknown/preserved entries remain neutral.
        if (!entry.HasNative) return NativeSlotState.Neutral;
        PrepareAppearance(entry);
        if (scratch->ApparentSlotType == NativeType.Empty) return NativeSlotState.Neutral;
        return NativeSlotState.Neutral with {
            Usable = scratch->IsSlotUsable(scratch->ApparentSlotType, scratch->ApparentActionId) };
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
            DalamudServices.PluginLog.Error(error, "热键栏工坊：接收原生拖放失败，交回游戏处理");
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
            uint container = uint.MaxValue;
            short slot = -1;
            resolveInventory(&container, &slot, payload->Int1, payload->Int2);
            if (!NativeHotbarInventory.TryPackPosition(container, slot, out var packed)
                || !Enum.IsDefined((InventoryType)container)) return false;
            var inventory = InventoryManager.Instance();
            if (inventory == null) return false;
            var bag = inventory->GetInventoryContainer((InventoryType)container);
            if (bag == null || !bag->IsLoaded || slot >= bag->Size) return false;
            var item = bag->GetInventorySlot(slot);
            if (item == null || item->GetBaseItemId() == 0) return false;
            var expected = item->GetItemId();
            LastDrag += $" · 容器 {container} / 格 {slot} / 物品 {expected}";
            // Set(InventoryItem, packed position) is the SAME normalization used by the native receiver.
            if (!Describe(NativeType.InventoryItem, packed, out entry)
                || !NativeHotbarInventory.MatchesItem(expected, entry.NativeType, entry.NativeId))
            { entry = new(); LastDrag += " · 物品身份核对失败，未接收"; return false; }
            return true;
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
        PrepareCurrentAppearance();
        scratch->LoadIconId();
        // Native display names are SeStrings (links, colours and icons), not plain UTF-8.
        // Item rows supply the exact name without link/HQ decoration; keep identity unchanged.
        var name = scratch->CommandType == NativeType.Item ? NativeHotbarNames.ItemName(scratch->CommandId) : null;
        if (string.IsNullOrWhiteSpace(name))
            name = scratch->GetDisplayNameForSlot(scratch->ApparentSlotType, scratch->ApparentActionId).ExtractText();
        if (string.IsNullOrWhiteSpace(name)) name = $"原生类型 {(byte)scratch->CommandType} / {scratch->CommandId}";
        var icon = ResolveCurrentIcon(scratch->IconId);
        entry = new() { NativeType = (byte)scratch->CommandType, NativeId = scratch->CommandId, IconId = icon > 0 ? icon : 234001,
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

    private void PrepareAppearance(HotbarEntry entry)
    {
        scratch->Clear(); scratch->Set((NativeType)entry.NativeType, entry.NativeId);
        PrepareCurrentAppearance();
    }

    internal bool TryDetail(HotbarEntry entry, out NativeHotbarDetail detail)
    {
        detail = default;
        if (!Ready || scratch == null || UIModule.Instance() == null
            || !entry.HasNative || !NativeHotbarDetail.TryCreate(entry.NativeType, entry.NativeId, out _)) return false;
        PrepareAppearance(entry);
        var type = (byte)scratch->ApparentSlotType;
        var id = scratch->ApparentActionId;
        if (!NativeHotbarDetail.TryCreate(type, id, out detail)) return false;
        // DetailKind is a separate namespace from HotbarSlotType/ActionType. Validate the
        // corresponding row before entering native detail generators, especially Item/HQ IDs.
        var data = DalamudServices.DataManager;
        return type switch
        {
            1 => data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id) is not null,
            2 => NativeHotbarItems.TryItem(id, out var baseId, out _)
                && data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(baseId) is not null,
            4 => data.GetExcelSheet<Lumina.Excel.Sheets.EventItem>().GetRowOrDefault(id) is not null,
            9 => data.GetExcelSheet<Lumina.Excel.Sheets.CraftAction>().GetRowOrDefault(id) is not null,
            10 => data.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>().GetRowOrDefault(id) is not null,
            13 => data.GetExcelSheet<Lumina.Excel.Sheets.Companion>().GetRowOrDefault(id) is not null,
            17 => data.GetExcelSheet<Lumina.Excel.Sheets.Mount>().GetRowOrDefault(id) is not null,
            _ => false,
        };
    }

    internal bool TryMacroDetail(string text, out NativeHotbarDetail detail)
    {
        detail = default;
        return macroDetails.TryResolve(text, out var target)
            && TryDetail(new() { NativeType = target.Type, NativeId = target.Id }, out detail);
    }

    internal static bool TryReadMacro(uint id, out string text, out string name)
    {
        text = name = "";
        if (!NativeHotbarMacroDetailRules.TryMacroAddress(id, out var set, out var index)) return false;
        var module = RaptureMacroModule.Instance();
        if (module == null) return false;
        var macro = module->GetMacro(set, index);
        if (macro == null) return false;
        var lines = new string[15];
        for (var i = 0; i < lines.Length; i++) lines[i] = macro->Lines[i].ToString();
        text = string.Join('\n', lines);
        name = NativeHotbarNames.Plain(macro->Name.ToString());
        return true;
    }

    private void PrepareCurrentAppearance()
    {
        var module = RaptureHotbarModule.Instance();
        if (module == null) return;
        var type = scratch->CommandType; var id = scratch->CommandId; ushort parameter = 0;
        RaptureHotbarModule.GetSlotAppearance(&type, &id, &parameter, module, scratch);
        scratch->ApparentSlotType = type; scratch->ApparentActionId = id; scratch->ApparentActionModeParam = parameter;
    }

    public uint CurrentIcon(HotbarEntry entry)
    {
        if (!entry.HasNative || scratch == null || UIModule.Instance() == null) return entry.IconId;
        PrepareAppearance(entry);
        scratch->LoadIconId();
        var icon = ResolveCurrentIcon(scratch->IconId);
        return icon > 0 ? icon : entry.IconId;
    }

    private uint ResolveCurrentIcon(uint icon)
    {
        if (scratch->CommandType != NativeType.Item || !NativeHotbarItems.TryItem(scratch->CommandId, out var baseId, out _)) return icon;
        var row = DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(baseId);
        return row is { } item ? NativeHotbarItems.SelectIcon(scratch->CommandId, icon, item.Icon) : icon;
    }

    public void Dispose()
    {
        SuppressStandardExecution = null;
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

