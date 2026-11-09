using System;
using System.Collections.Generic;

namespace OmniToolbox.KeyRepeaterOnline;

internal sealed class KeyRepeatKeyMap
{
    private readonly Dictionary<int, int> windows = new();
    public KeyRepeatKeyMap(IEnumerable<(int Windows, int Native)> pairs)
    {
        foreach (var (win, native) in pairs)
            if (Supported(win) && native is >= 1 and <= 255) windows.TryAdd(native, win);
    }
    internal static bool Supported(int key) => key is >= 4 and <= 6
        || key is >= 8 and <= 254 && key is not (16 or 17 or 18 or 91 or 92 or >= 160 and <= 165);

    public bool TryChord(int native, int modifiers, out int chord)
    {
        chord = 0;
        if (modifiers is < 0 or > 7) return false;
        // Same game PAD_MMB/PAD_MB4/PAD_MB5 encoding as Hotbar Workshop.
        // Never treat native mouse codes as left/right modifier Windows VK codes.
        var key = native switch
        {
            161 => 4, 163 => 5, 164 => 6,
            >= 4 and <= 6 => native, // readable legacy mouse aliases
            0 or 160 or 162 or >= 165 => 0, // unbound, ordinary mouse clicks and gamepad-only codes
            _ => windows.GetValueOrDefault(native)
        };
        if (!Supported(key)) return false;
        chord = key | modifiers << 8;
        return true;
    }
}

internal readonly record struct KeyRepeatMouseMessage(uint Id, nuint WParam);

internal static class KeyRepeatMouseInput
{
    public static KeyRepeatMouseMessage[] Messages(int key, Func<int, bool> down)
    {
        if (key is not (4 or 5 or 6)) throw new ArgumentOutOfRangeException(nameof(key));
        // Low word: current mouse/Shift/Ctrl flags. Alt is physical keyboard context.
        uint flags = (down(1) ? 1u : 0) | (down(2) ? 2u : 0) | (down(16) ? 4u : 0)
            | (down(17) ? 8u : 0) | (down(4) ? 16u : 0) | (down(5) ? 32u : 0) | (down(6) ? 64u : 0);
        uint bit = key == 4 ? 16u : key == 5 ? 32u : 64u;
        uint high = key == 4 ? 0 : (key == 5 ? 1u : 2u) << 16;
        return [new(key == 4 ? 0x207u : 0x20Bu, high | flags | bit),
            new(key == 4 ? 0x208u : 0x20Cu, high | (flags & ~bit))];
    }
}
