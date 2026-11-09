using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OmniToolbox.NativeDragHotbarLocal;

// Client-pixel coordinates, independent of ImGui font/global scaling and work-area anchors.
internal readonly record struct NativeBarGeometry(Vector2 FirstFrame, float Scale, int Columns, int Rows)
{
    public Vector2 Size => new Vector2((Columns - 1) * 45 + 48, (Rows - 1) * 49 + 48) * Scale;
    public Vector2 Frame(int slot) => FirstFrame + new Vector2(slot % Columns * 45, slot / Columns * 49) * Scale;
    public Vector2 Image(int slot) => Frame(slot) + new Vector2(4, 3) * Scale;
    public Vector2 ImageSize => new Vector2(40 * Scale);
    public Vector2 Root => FirstFrame - new Vector2(46, 14) * Scale;
    public Vector2 Lock => FirstFrame + new Vector2(-30, (Rows - 1) * 49 + 15) * Scale;
    public NativeBarGeometry OnViewport(Vector2 position) => this with { FirstFrame = FirstFrame + position };
    public static NativeBarGeometry FromSaved(DragHotbar bar) => new(new(bar.X, bar.Y), bar.Scale, bar.Columns, bar.Rows);
    public static NativeBarGeometry FromRoot(Vector2 position, float scale, int columns, int rows)
        => new(position + new Vector2(46, 14) * scale, scale, columns, rows);
    public void Validate()
    {
        if (!float.IsFinite(FirstFrame.X) || !float.IsFinite(FirstFrame.Y) || !float.IsFinite(Scale)
            || Scale is < 0.5f or > 2.5f || !NativeHotbarLayout.NativeColumns.Contains(Columns) || Rows != 12 / Columns)
            throw new InvalidDataException("原生栏几何数据无效；保留模块已保存的位置。");
    }
    public bool Store(DragHotbar bar)
    {
        Validate();
        var changed = Math.Abs(bar.X - FirstFrame.X) > 0.01f || Math.Abs(bar.Y - FirstFrame.Y) > 0.01f
            || Math.Abs(bar.Scale - Scale) > 0.0001f || bar.Columns != Columns || bar.Rows != Rows
            || bar.AnchorX.HasValue || bar.AnchorY.HasValue;
        if (!changed) return false;
        bar.X = FirstFrame.X; bar.Y = FirstFrame.Y; bar.Scale = Scale; bar.Columns = Columns; bar.Rows = Rows;
        bar.AnchorX = bar.AnchorY = null;
        return true;
    }
}

internal static class NativeBarInteraction
{
    public static bool Edit(bool takeover, bool locked, bool hudEditing) => !locked && (!takeover || !hudEditing);
    public static bool Input(bool takeover, bool hudEditing) => !takeover || !hudEditing;
    public static float Alpha(bool takeover, bool hudEditing) => takeover && hudEditing ? 0.4f : 1;

    public static bool HasSurface(DragHotbar bar, int index, bool revealEmpty)
        => index == 0 || !bar.HideEmpty || revealEmpty || bar.Slots.Any(slot => !slot.IsEmpty);

    public static bool Hit(NativeBarGeometry geometry, DragHotbar bar, int index, Vector2 mouse, bool revealEmpty)
    {
        // The window has extra room for icon effects. That transparent margin is not a hit
        // target, nor is a slot hidden by HideEmpty. It must not cover a neighbouring bar.
        for (var slot = 0; slot < 12; slot++)
            if ((!bar.HideEmpty || revealEmpty || !bar.Slots[slot].IsEmpty)
                && new HotbarRect(geometry.Frame(slot), geometry.Frame(slot) + new Vector2(48 * geometry.Scale)).Contains(mouse))
                return true;
        return index == 0 && new HotbarRect(geometry.Lock, geometry.Lock + new Vector2(20, 24) * geometry.Scale).Contains(mouse);
    }
}

internal readonly record struct NativeNodeTransform(Vector2 Position, Vector2 Scale, Vector2 Origin);
internal static class NativeNodeGeometry
{
    public static (Vector2 Position, Vector2 Scale) Compose(ReadOnlySpan<NativeNodeTransform> chain)
    {
        if (chain.Length is < 1 or > 24) throw new InvalidDataException("原生节点父链长度无效。");
        var position = Vector2.Zero; var scale = Vector2.One;
        foreach (var node in chain)
        {
            if (!float.IsFinite(node.Position.X) || !float.IsFinite(node.Position.Y)
                || !float.IsFinite(node.Scale.X) || !float.IsFinite(node.Scale.Y)
                || !float.IsFinite(node.Origin.X) || !float.IsFinite(node.Origin.Y)
                || node.Scale.X <= 0 || node.Scale.Y <= 0) throw new InvalidDataException("原生节点变换无效。");
            position = node.Position + node.Origin + (position - node.Origin) * node.Scale;
            scale *= node.Scale;
        }
        return (position, scale);
    }
}

internal static class NativeBarGeometryMigration
{
    public static bool Upgrade(SuperHotbarDocument data)
    {
        if (data.NativeGeometryVersion != 0) return false;
        if (data.Appearance.Count != 10)
            throw new InvalidDataException("保存的布局必须包含全部 10 条栏位。");
        // Old archives stored addon-root coordinates. Convert those saved coordinates,
        // never recapture another character's native layout during a runtime upgrade.
        var snapshots = data.Appearance.Select(bar => NativeBarGeometry.FromRoot(
            new(bar.X, bar.Y), bar.Scale, bar.Columns, bar.Rows)).ToArray();
        foreach (var snapshot in snapshots) snapshot.Validate();
        for (var b = 0; b < 10; b++)
            snapshots[b].Store(data.Appearance[b]);
        data.NativeGeometryVersion = 1;
        return true;
    }
}
