using System;
using System.Numerics;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class TakeoverViewportRules
{
    internal static bool Intersects(Vector2 min, Vector2 max, Vector2 position, Vector2 size)
        => size.X > 0 && size.Y > 0 && min.X < position.X + size.X && min.Y < position.Y + size.Y
            && max.X > position.X && max.Y > position.Y;
}

// Transient presentation state only: never changes a bar, a visibility record or an anchor.
internal sealed class TakeoverViewportState
{
    internal bool Hidden { get; private set; }
    private long? restoreAt;
    internal void Reset() { Hidden = false; restoreAt = null; }
    internal bool Update(bool enabled, int threshold, Vector2 reference, Vector2 size, long now)
    {
        if (!enabled || reference.X <= 0 || reference.Y <= 0) { Reset(); return false; }
        var ratio = 100 * Math.Min(size.X / reference.X, size.Y / reference.Y);
        if (!Hidden && ratio < threshold) { Hidden = true; restoreAt = null; }
        if (Hidden)
        {
            if (ratio < threshold + 3) restoreAt = null;
            else if (restoreAt is null || now < restoreAt.Value) restoreAt = now;
            else if (now - restoreAt.Value >= 250) Reset();
        }
        return Hidden;
    }
}
