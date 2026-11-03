using System;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarPayload
{
    // The native action-bar receiver reads payload + 4. The first integer is source context.
    // Inventory-position payloads are excluded by the bridge before reaching this decoder.
    public static bool TryDecode(byte type, int context, int actionId, out uint id)
    {
        _ = context;
        id = unchecked((uint)actionId);
        return HotbarRules.IsSupportedType(type) && HotbarRules.IsValidId(type, id);
    }
}

// Retain only a managed snapshot between frames, never a dereferenceable native source pointer.
// Each observed left-button release is consumed once, even if it misses every target.
internal sealed class NativeHotbarDragSession
{
    public nint Source { get; private set; }
    public bool Active { get; private set; }
    public bool Delivered { get; private set; }
    private bool armed, blocked;
    private HotbarEntry? cached;

    public void Begin(nint source)
    {
        Source = source; Active = true; Delivered = false;
        armed = false; blocked = false; cached = null;
    }

    public void Observe(bool leftDown, bool canceled, bool foreground, HotbarEntry? entry)
    {
        if (!Active || Delivered) return;
        if (canceled || !foreground) blocked = true;
        if (leftDown) armed = true;
        if (entry is not null) cached = entry.Copy();
    }

    public bool Release(bool leftDown, bool canceled, bool foreground, bool overTarget, Func<HotbarEntry, bool> receive)
    {
        if (canceled || !foreground) blocked = true;
        if (!Active || Delivered || leftDown || !armed) return false;
        armed = false;
        if (blocked || !overTarget || cached is null) return false;
        // Set before invoking the recipient so a nested callback cannot deliver twice.
        Delivered = true;
        if (receive(cached.Copy())) return true;
        Delivered = false;
        return false;
    }

    public void End()
    {
        Active = false; Source = 0; armed = false; blocked = false; cached = null;
    }
}
