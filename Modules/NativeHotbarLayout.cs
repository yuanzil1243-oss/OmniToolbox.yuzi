using System;
using System.Numerics;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarLayout
{
    public static Vector2 ResolvePosition(float x, float y, float? anchorX, float? anchorY,
        Vector2 workPosition, Vector2 workSize, Vector2 barSize, out float resolvedAnchorX, out float resolvedAnchorY)
    {
        var range = Vector2.Max(Vector2.Zero, workSize - barSize);
        if (anchorX.HasValue && anchorY.HasValue)
        {
            resolvedAnchorX = Math.Clamp(anchorX.Value, 0, 1);
            resolvedAnchorY = Math.Clamp(anchorY.Value, 0, 1);
            return workPosition + range * new Vector2(resolvedAnchorX, resolvedAnchorY);
        }

        var position = Vector2.Clamp(new(x, y), workPosition, workPosition + range);
        resolvedAnchorX = range.X > 0 ? (position.X - workPosition.X) / range.X : 0.5f;
        resolvedAnchorY = range.Y > 0 ? (position.Y - workPosition.Y) / range.Y : 0.5f;
        return position;
    }

    public static Vector2 SetPosition(Vector2 position, Vector2 workPosition, Vector2 workSize, Vector2 barSize,
        out float anchorX, out float anchorY)
    {
        var range = Vector2.Max(Vector2.Zero, workSize - barSize);
        position = Vector2.Clamp(position, workPosition, workPosition + range);
        anchorX = range.X > 0 ? (position.X - workPosition.X) / range.X : 0.5f;
        anchorY = range.Y > 0 ? (position.Y - workPosition.Y) / range.Y : 0.5f;
        return position;
    }
}
