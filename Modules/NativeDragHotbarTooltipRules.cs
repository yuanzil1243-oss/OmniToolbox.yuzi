using System;
using System.Numerics;

namespace OmniToolbox.NativeDragHotbarLocal;

internal readonly record struct NativeHotbarDetail(byte Mask, byte Kind, uint Id, short Flags)
{
    // Values verified against the CURRENT client's DetailKind/AtkTooltipType, not ActionType.
    internal static bool TryCreate(byte type, uint id, out NativeHotbarDetail detail)
    {
        detail = default;
        if (!HotbarRules.IsSupportedType(type) || !HotbarRules.IsValidId(type, id)) return false;
        byte kind = type switch { 1 => 30, 2 => 11, 4 => 8, 9 => 31, 10 => 32, 13 => 36, 17 => 41, _ => 0 };
        if (kind == 0 || type == 2 && !NativeHotbarItems.TryItem(id, out _, out _)) return false;
        detail = new((byte)(type is 2 or 4 ? 3 : 5), kind, id, 0);
        return true;
    }

    internal static bool ValidBounds(Vector2 min, Vector2 max)
        => float.IsFinite(min.X) && float.IsFinite(min.Y) && float.IsFinite(max.X) && float.IsFinite(max.Y)
            && max.X > min.X && max.Y > min.Y && max.X - min.X <= 1024 && max.Y - min.Y <= 1024;
}

internal sealed class NativeHotbarTooltipOwnership
{
    internal nint Manager { get; private set; }
    internal nint Anchor { get; private set; }
    internal ushort Parent { get; private set; }
    internal long LastSeen { get; private set; }
    private bool seen;
    internal bool Active => Anchor != 0;
    internal void BeginFrame() => seen = false;
    internal void Mark(nint manager, nint anchor, ushort parent, long now)
    { Manager = manager; Anchor = anchor; Parent = parent; LastSeen = now; seen = true; }
    internal bool Owns(nint manager, nint target, ushort parent)
        => Active && manager == Manager && target == Anchor && parent == Parent;
    internal bool EndFrameNeedsRelease => Active && !seen;
    internal bool Expired(long now) => Active && (now < LastSeen || now - LastSeen > 150);
    internal void Clear() { Manager = Anchor = 0; Parent = 0; LastSeen = 0; seen = false; }
}
