namespace OmniToolbox.SuperHotbarInterop;

// Shared source, compiled into each module. IPC payloads contain only framework types.
internal static class SuperHotbarRepeatProtocol
{
    public const string Active = "OmniToolbox.Yuzi.SuperHotbar.v1.Active";
    public const string Bindings = "OmniToolbox.Yuzi.SuperHotbar.v1.Bindings";
    public const string Repeat = "OmniToolbox.Yuzi.SuperHotbar.v1.Repeat";
    public static bool TryChord(int encoded, out int key, out int modifiers)
    {
        key = encoded & 255; modifiers = encoded >> 8;
        return modifiers is >= 0 and <= 7 && key is >= 8 and <= 254
            && key is not (16 or 17 or 18 or 91 or 92 or >= 160 and <= 165);
    }
}
