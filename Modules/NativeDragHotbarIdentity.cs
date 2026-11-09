using System;
using System.IO;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarIdentity
{
    public const int Count = 10, SlotsPerBar = 12, FirstSlotInput = 57;
    public static string AddonName(int displayBar)
    {
        if (displayBar is < 0 or >= Count) throw new InvalidDataException("普通热键栏编号必须为 1～10。");
        // The primary bar has no suffix; the SECOND bar starts at 01, not 02.
        return displayBar == 0 ? "_ActionBar" : "_ActionBar" + displayBar.ToString("D2");
    }
    public static bool Matches(int displayBar, uint nativeId) => displayBar is >= 0 and < Count
        && (displayBar == 0 ? nativeId < Count : nativeId == displayBar);
    public static int LogicalBar(int displayBar, int firstPage)
    {
        if (displayBar is < 0 or >= Count || firstPage is < 0 or >= Count) throw new InvalidDataException("热键栏或主栏内容页编号无效。");
        return displayBar == 0 ? firstPage : displayBar;
    }
    public static int InputId(int displayBar, int slot)
    {
        if (displayBar is < 0 or >= Count || slot is < 0 or >= SlotsPerBar) throw new InvalidDataException("热键栏快捷键位置无效。");
        return FirstSlotInput + displayBar * SlotsPerBar + slot;
    }
    public static bool TryInput(int inputId, out int displayBar, out int slot)
    {
        displayBar = slot = -1;
        if (inputId < FirstSlotInput || inputId >= FirstSlotInput + Count * SlotsPerBar) return false;
        displayBar = (inputId - FirstSlotInput) / SlotsPerBar; slot = (inputId - FirstSlotInput) % SlotsPerBar;
        return true;
    }
}
