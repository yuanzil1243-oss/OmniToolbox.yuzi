namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarSafety
{
    // GetApparentIconRecastCharges unconditionally treats ApparentActionId as ActionType.Action.
    // An item/HQ item/crafting ID must never reach it, even when it is a valid hotbar command.
    internal static bool CanReadCombatState(byte type, uint id, bool actionExists) =>
        type == 1 && id != 0 && actionExists;

    internal static bool CanReadGeneralState(byte type, uint id, bool generalActionExists) =>
        type == 10 && id != 0 && generalActionExists;

    internal static bool CanReadItemState(byte type, uint id, bool itemExists) =>
        type == 2 && NativeHotbarItems.TryItem(id, out _, out _) && itemExists;

    // The indexed native wrapper addresses 16 slots/bar. Only 12 are ordinary keyboard slots.
    // Do not intercept cross/pet/scratch slots or pointers to a different module instance.
    internal static bool IsOrdinarySlot(long slot, long first, int slotSize)
    {
        if (slot <= 0 || first <= 0 || slotSize <= 0 || slot < first) return false;
        var offset = (ulong)slot - (ulong)first;
        return offset < (ulong)slotSize * 16 * 10 && offset % (ulong)slotSize == 0
            && offset / (ulong)slotSize % 16 < 12;
    }

    // PetHotbarMode alone does not say whether the ordinary main bar is replaced.
    internal static bool IsSpecialMainBar(uint hotbarId) => hotbarId >= 10;
}
