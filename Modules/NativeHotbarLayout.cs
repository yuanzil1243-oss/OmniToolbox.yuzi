using System;
using System.Numerics;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarLayout
{
    // AddonActionBarX.ActionBarLayout: order matches the game's HUD layout selector.
    public static ReadOnlySpan<int> NativeColumns => [12, 6, 4, 3, 2, 1];

    public static bool TryNativeShape(int layout, out int columns, out int rows)
    {
        columns = rows = 0;
        if (layout < 0 || layout >= NativeColumns.Length) return false;
        columns = NativeColumns[layout]; rows = 12 / columns;
        return true;
    }

    public static void ApplyNativeShape(DragHotbar bar, int layout)
    {
        if (!TryNativeShape(layout, out var columns, out var rows))
            throw new System.IO.InvalidDataException("无法识别原生热键栏的格数样式，原布局已保留。");
        // Exactly twelve slots in every native shape. Never resize/reorder/delete contents here.
        bar.Columns = columns; bar.Rows = rows;
        bar.AnchorX = null; bar.AnchorY = null;
    }

    public static void ApplyNativeAppearance(DragHotbar target, DragHotbar source)
    {
        if (source.SlotCount != 12 || !NativeColumns.Contains(source.Columns) || source.Rows != 12 / source.Columns
            || !float.IsFinite(source.X) || !float.IsFinite(source.Y) || !float.IsFinite(source.Scale)
            || source.Scale is < 0.5f or > 2.5f)
            throw new System.IO.InvalidDataException("原生外观数据无效，未修改栏位。");
        target.Columns = source.Columns; target.Rows = source.Rows;
        target.X = source.X; target.Y = source.Y; target.Scale = source.Scale; target.Visible = source.Visible;
        target.AnchorX = target.AnchorY = null;
    }

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
