using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using OmenTools;
using OmenTools.OmenService;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.Lifecycle;

namespace OmniToolbox.QuickTransferOnline;

public sealed unsafe class QuickTransfer : ModuleBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private static readonly InventoryType[] DefaultPriority =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
        InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal
    };
    private static readonly InventoryType[] ArmoryTypes =
    {
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal
    };
    private readonly QuickTransferConfig config = new();
    private AddonEventRegistry? addonEvents;
    private Hook<AgentInventoryContext.Delegates.OpenForItemSlot>? contextHook;
    private bool pendingTransfer;
    private bool dispatching;

    public override ModuleInfo Info { get; } = new()
    {
        Title = "快捷转移", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "按住修饰键左键单击物品，将其转移到另一个已打开的物品界面。"
    };
    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        LoadConfig();
        try
        {
            contextHook = DService.Instance().Hook.HookFromAddress<AgentInventoryContext.Delegates.OpenForItemSlot>(
                AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot, OnOpenContext);
            contextHook.Enable();
            addonEvents = new AddonEventRegistry(DalamudServices.AddonLifecycle);
            foreach (var name in GridAddonNames)
                addonEvents.Register(AddonEvent.PreReceiveEvent, name, OnReceiveEvent);
        }
        catch
        {
            addonEvents?.Dispose();
            addonEvents = null;
            contextHook?.Dispose();
            contextHook = null;
            throw;
        }
    }

    protected override void OnDisable()
    {
        addonEvents?.Dispose();
        addonEvents = null;
        contextHook?.Dispose();
        contextHook = null;
        pendingTransfer = false;
    }

    private static readonly string[] GridAddonNames =
    {
        "InventoryGrid", "InventoryGrid0", "InventoryGrid1", "InventoryGrid0E", "InventoryGrid1E",
        "InventoryGrid2E", "InventoryGrid3E", "InventoryBuddy", "ArmouryBoard"
    };

    public override bool DrawSettings()
    {
        var changed = ImGui.Checkbox("Alt", ref config.Alt);
        changed |= ImGui.Checkbox("Ctrl", ref config.Ctrl);
        changed |= ImGui.Checkbox("Shift", ref config.Shift);
        ImGui.TextUnformatted("目标界面优先级：物品栏、雇员物品栏、陆行鸟包、兵装库");
        for (var i = 0; i < config.Priority.Length; i++)
        {
            ImGui.PushID(i);
            ImGui.TextUnformatted($"{i + 1}. {GroupLabel(config.Priority[i])}");
            ImGui.SameLine();
            if (ImGui.SmallButton("↑") && i > 0)
            {
                (config.Priority[i - 1], config.Priority[i]) = (config.Priority[i], config.Priority[i - 1]);
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("↓") && i < config.Priority.Length - 1)
            {
                (config.Priority[i + 1], config.Priority[i]) = (config.Priority[i], config.Priority[i + 1]);
                changed = true;
            }
            ImGui.PopID();
        }
        if (!config.Alt && !config.Ctrl && !config.Shift)
            ImGui.TextUnformatted("请至少选择一个修饰键；当前不会触发转移。");
        if (changed) SaveConfig();
        return changed;
    }

    private static string GroupLabel(InventoryType type) => type switch
    {
        InventoryType.Inventory1 => "物品栏", InventoryType.RetainerPage1 => "雇员物品栏",
        InventoryType.SaddleBag1 => "陆行鸟包", InventoryType.ArmoryMainHand => "兵装库", _ => type.ToString()
    };

    private static string ConfigPath => Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "QuickTransfer.json");

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var saved = JsonSerializer.Deserialize<QuickTransferConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            if (saved == null) return;
            config.Alt = saved.Alt; config.Ctrl = saved.Ctrl; config.Shift = saved.Shift;
            var groups = saved.GroupPriority?.Select(GroupFor).Distinct().ToArray();
            if (groups?.Length == 4) config.Priority = groups;
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取快捷转移配置失败"); }
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            config.GroupPriority = config.Priority;
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存快捷转移配置失败"); }
    }

    private static InventoryType GroupFor(InventoryType type) => type switch
    {
        InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4 => InventoryType.Inventory1,
        >= InventoryType.RetainerPage1 and <= InventoryType.RetainerPage7 => InventoryType.RetainerPage1,
        InventoryType.SaddleBag1 or InventoryType.SaddleBag2 or InventoryType.PremiumSaddleBag1 or InventoryType.PremiumSaddleBag2 => InventoryType.SaddleBag1,
        _ => InventoryType.ArmoryMainHand
    };

    private bool Held() => (config.Alt || config.Ctrl || config.Shift)
        && (!config.Alt || DalamudServices.KeyState[VirtualKey.MENU])
        && (!config.Ctrl || DalamudServices.KeyState[VirtualKey.CONTROL])
        && (!config.Shift || DalamudServices.KeyState[VirtualKey.SHIFT]);

    private static bool Visible(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible;
    }

    private static InventoryType? SourceType(AtkUnitBase* addon)
    {
        if (addon == null) return null;
        var name = addon->NameString;
        if (name == "InventoryBuddy")
        {
            var buddy = (AddonInventoryBuddy*)addon;
            return buddy->TabIndex == 0 ? InventoryType.SaddleBag1 : InventoryType.SaddleBag2;
        }
        if (name == "ArmouryBoard")
        {
            var armoury = (AddonArmouryBoard*)addon;
            return armoury->TabIndex >= 0 && armoury->TabIndex < ArmoryTypes.Length
                ? ArmoryTypes[armoury->TabIndex]
                : null;
        }

        var parent = RaptureAtkUnitManager.Instance()->GetAddonById(addon->ParentId);
        if (parent == null || !parent->IsVisible) return null;
        var parentName = parent->NameString;
        if (parentName == "InventoryRetainer" || parentName == "InventoryRetainerLarge")
        {
            var page = parentName == "InventoryRetainer"
                ? ((AddonInventoryRetainer*)parent)->TabIndex
                : ((AddonInventoryRetainerLarge*)parent)->TabIndex;
            return page is >= 0 and <= 6 ? (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page) : null;
        }
        if (parentName == "Inventory" || parentName == "InventoryLarge" || parentName == "InventoryExpansion")
        {
            var page = parentName switch
            {
                "Inventory" => ((AddonInventory*)parent)->TabIndex,
                "InventoryLarge" => ((AddonInventoryLarge*)parent)->TabIndex,
                _ => ((AddonInventoryExpansion*)parent)->TabIndex
            };
            return page is >= 0 and <= 3 ? (InventoryType)((uint)InventoryType.Inventory1 + (uint)page) : null;
        }
        return null;
    }

    private static bool IsGroupOpen(InventoryType group) => group switch
    {
        InventoryType.Inventory1 => Visible("Inventory") || Visible("InventoryLarge") || Visible("InventoryExpansion"),
        InventoryType.RetainerPage1 => Visible("InventoryRetainer") || Visible("InventoryRetainerLarge"),
        InventoryType.SaddleBag1 => Visible("InventoryBuddy"),
        _ => Visible("ArmouryBoard")
    };

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receive || receive.AtkEventType != AddonEventType.MouseClick || !Held()) return;
        var eventData = (AtkEventData*)receive.AtkEventData;
        var atkEvent = (AtkEvent*)receive.AtkEvent;
        if (eventData == null || atkEvent == null || eventData->MouseData.ButtonId != 0) return;
        receive.PreventOriginal();

        var addon = receive.GetAddonPointer<AtkUnitBase>();
        var sourceType = SourceType(addon);
        if (!sourceType.HasValue)
        {
            NotifyHelper.SystemWarning();
            return;
        }
        var sourceSlot = FindSlot(addon, atkEvent->Node);
        if (sourceSlot < 0)
        {
            NotifyHelper.SystemWarning();
            return;
        }

        var inventory = InventoryManager.Instance();
        var source = inventory->GetInventorySlot(sourceType.Value, sourceSlot);
        if (source == null || source->ItemId == 0 || source->Quantity <= 0)
        {
            NotifyHelper.SystemWarning();
            return;
        }

        var context = AgentInventoryContext.Instance();
        if (context == null || contextHook == null || pendingTransfer)
        {
            NotifyHelper.SystemWarning();
            return;
        }

        pendingTransfer = true;
        try
        {
            context->OpenForItemSlot(sourceType.Value, sourceSlot, 0, addon->Id);
        }
        catch (Exception ex)
        {
            pendingTransfer = false;
            DalamudServices.PluginLog.Error(ex, "快捷转移未能打开游戏原生物品菜单");
            NotifyHelper.SystemWarning();
        }
    }

    private void OnOpenContext(AgentInventoryContext* context, InventoryType inventory, int slot, int flags, uint owner)
    {
        contextHook!.Original(context, inventory, slot, flags, owner);
        if (!pendingTransfer || context == null) return;

        pendingTransfer = false;
        if (dispatching) return;
        try
        {
            dispatching = true;
            ExecuteNativeTransfer(context, inventory);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "快捷转移原生菜单命令执行失败");
            NotifyHelper.SystemWarning();
        }
        finally
        {
            dispatching = false;
        }
    }

    private void ExecuteNativeTransfer(AgentInventoryContext* context, InventoryType sourceType)
    {
        var menu = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)context->GetAddonId());
        if (menu == null || !menu->IsVisible)
        {
            NotifyHelper.SystemWarning();
            return;
        }

        for (var i = 0; i < context->ContextItemCount && i < 32; i++)
        {
            var index = context->ContexItemStartIndex + i;
            if (index < 0 || index >= context->EventParams.Length || context->IsContextItemDisabled(i)) continue;
            ref var value = ref context->EventParams[index];
            if (value.Type != AtkValueType.String) continue;

            var target = TargetForNativeCommand(value.String.ToString(), sourceType);
            if (!target.HasValue || target.Value == GroupFor(sourceType) || !IsGroupOpen(target.Value)) continue;
            if (Array.IndexOf(config.Priority, target.Value) < 0) continue;

            var priorityIndex = Array.IndexOf(config.Priority, target.Value);
            if (priorityIndex != GetBestAvailablePriority(context, sourceType)) continue;

            AtkValue* callbackArgs = stackalloc AtkValue[5];
            callbackArgs[0].SetInt(0);
            callbackArgs[1].SetInt(i);
            callbackArgs[2].SetUInt(0);
            callbackArgs[3].SetInt(0);
            callbackArgs[4].SetInt(0);
            menu->FireCallback(5, callbackArgs);
            context->Hide();
            menu->Close(false);
            NotifyHelper.SystemWarning();
            return;
        }

        context->Hide();
        menu->Close(false);
        NotifyHelper.SystemWarning();
    }

    private int GetBestAvailablePriority(AgentInventoryContext* context, InventoryType sourceType)
    {
        var best = int.MaxValue;
        for (var i = 0; i < context->ContextItemCount && i < 32; i++)
        {
            var index = context->ContexItemStartIndex + i;
            if (index < 0 || index >= context->EventParams.Length || context->IsContextItemDisabled(i)) continue;
            ref var value = ref context->EventParams[index];
            if (value.Type != AtkValueType.String) continue;
            var target = TargetForNativeCommand(value.String.ToString(), sourceType);
            if (!target.HasValue || target.Value == GroupFor(sourceType) || !IsGroupOpen(target.Value)) continue;
            var priority = Array.IndexOf(config.Priority, target.Value);
            if (priority >= 0 && priority < best) best = priority;
        }
        return best;
    }

    private static InventoryType? TargetForNativeCommand(string label, InventoryType sourceType)
    {
        var normalized = label.Trim().TrimEnd('.', '…', ' ');
        return normalized switch
        {
            "放入背包" or "Put in Inventory" when GroupFor(sourceType) == InventoryType.ArmoryMainHand => InventoryType.Inventory1,
            "从陆行鸟鞍囊中取回" or "Retrieve from Saddlebag" when GroupFor(sourceType) == InventoryType.SaddleBag1 => InventoryType.Inventory1,
            "从雇员处取回" or "Retrieve from Retainer" when GroupFor(sourceType) == InventoryType.RetainerPage1 => InventoryType.Inventory1,
            "交给雇员保管" or "Entrust to Retainer" when GroupFor(sourceType) != InventoryType.RetainerPage1 => InventoryType.RetainerPage1,
            "放入陆行鸟鞍囊" or "Put in Saddlebag" when GroupFor(sourceType) != InventoryType.SaddleBag1 => InventoryType.SaddleBag1,
            "放入兵装库" or "Put in Armoury Chest" or "Put in Armory Chest" when GroupFor(sourceType) == InventoryType.Inventory1 => InventoryType.ArmoryMainHand,
            _ => null
        };
    }

    private static int FindSlot(AtkUnitBase* addon, AtkResNode* eventNode)
    {
        if (addon == null || eventNode == null) return -1;
        Span<Pointer<AtkComponentDragDrop>> slots = addon->NameString switch
        {
            "InventoryBuddy" => ((AddonInventoryBuddy*)addon)->Slots,
            "ArmouryBoard" => ((AddonArmouryBoard*)addon)->Slots,
            _ => ((AddonInventoryGrid*)addon)->Slots
        };
        for (var i = 0; i < slots.Length; i++)
        {
            var component = slots[i].Value;
            if (component == null) continue;
            var node = eventNode;
            for (var depth = 0; node != null && depth < 8; node = node->ParentNode, depth++)
            {
                if (node == component->AtkResNode || node == (AtkResNode*)component->OwnerNode) return i;
            }
        }
        return -1;
    }

}

[Serializable]
public sealed class QuickTransferConfig
{
    public bool Alt;
    public bool Ctrl;
    public bool Shift = true;
    public InventoryType[] Priority = { InventoryType.Inventory1, InventoryType.RetainerPage1, InventoryType.SaddleBag1, InventoryType.ArmoryMainHand };
    public InventoryType[] GroupPriority = { InventoryType.Inventory1, InventoryType.RetainerPage1, InventoryType.SaddleBag1, InventoryType.ArmoryMainHand };
}
