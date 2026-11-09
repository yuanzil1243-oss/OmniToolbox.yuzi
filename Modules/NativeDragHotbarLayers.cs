using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static unsafe class NativeHotbarWindowLayers
{
    public static List<HotbarRect> Read(Vector2 viewport, List<HotbarRect>? ownTooltipInput = null)
    {
        var result = new List<HotbarRect>();
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return result;
        var manager = &stage->RaptureAtkUnitManager->AtkUnitManager;
        var units = manager->AllLoadedUnitsList;
        var count = Math.Min((int)units.Count, units.Entries.Length);
        for (var i = 0; i < count; i++)
        {
            var addon = units.Entries[i].Value;
            if (addon == null || !addon->IsVisible || !addon->IsReady || addon->Alpha == 0 || addon->RootNode == null
                || !addon->RootNode->IsVisible()) continue;
            // Window components distinguish real game windows from HUD elements. Collision-only
            // popups (menus/tooltips) also cover the bar; ordinary HUD collision nodes do not.
            var name = addon->NameString;
            if (name.StartsWith('_')) continue;
            AtkResNode* node = addon->WindowNode != null ? &addon->WindowNode->AtkResNode : null;
            if (node == null && (name.Contains("Menu", StringComparison.Ordinal) || name.Contains("Select", StringComparison.Ordinal)
                || name.Contains("Tooltip", StringComparison.Ordinal)))
                node = addon->WindowCollisionNode != null ? &addon->WindowCollisionNode->AtkResNode : addon->RootNode;
            if (node == null || !node->IsVisible()) continue;
            if (Bounds(node, addon->RootNode, viewport, out var rect))
            {
                result.Add(rect);
                // Keep drawing below native help, but do not let our own help cancel the slot's
                // hover on the next frame. Other native windows retain full input blocking.
                if (name is "Tooltip" or "ActionDetail" or "ItemDetail") ownTooltipInput?.Add(rect);
            }
        }
        return result;
    }

    private static bool Bounds(AtkResNode* node, AtkResNode* root, Vector2 viewport, out HotbarRect rect)
    {
        rect = default;
        Span<NativeNodeTransform> chain = stackalloc NativeNodeTransform[24];
        var count = 0;
        for (var current = node; current != null && count < chain.Length; current = current->ParentNode)
        {
            if (!float.IsFinite(current->X) || !float.IsFinite(current->Y)
                || !float.IsFinite(current->OriginX) || !float.IsFinite(current->OriginY)
                || !float.IsFinite(current->ScaleX) || !float.IsFinite(current->ScaleY) || !float.IsFinite(current->Rotation)
                || current->ScaleX <= 0 || current->ScaleY <= 0 || Math.Abs(current->Rotation) > 0.001f) return false;
            chain[count++] = new(new(current->X, current->Y), new(current->ScaleX, current->ScaleY), new(current->OriginX, current->OriginY));
            if (current != root) continue;
            var (position, scale) = NativeNodeGeometry.Compose(chain[..count]);
            rect = new(viewport + position, viewport + position + new Vector2(node->Width, node->Height) * scale);
            return rect.Valid;
        }
        return false;
    }
}

public sealed partial class NativeDragHotbar
{
    private List<HotbarRect> nativeWindowCovers = [];
    private readonly List<HotbarRect> nativeTooltipPassthrough = [];
    private bool pluginWindowAtMouse;
    private bool NativeWindowAtMouse => pluginWindowAtMouse || nativeWindowCovers.Exists(r => r.Contains(ImGui.GetIO().MousePos)
        && !nativeTooltipPassthrough.Contains(r));
    private static void SendHotbarWindowBack() => ImGuiP.BringWindowToDisplayBack(ImGuiP.GetCurrentWindow());
    // Display-back is relative: a later plugin can also send its overlay back.
    // Keep takeover pixels on the viewport background, below all plugin windows;
    // the transparent window still owns slot input, focus and drag/drop.
    private static ImDrawListPtr HotbarDrawList(bool takeover) => takeover
        ? ImGui.GetBackgroundDrawList(ImGui.GetMainViewport()) : ImGui.GetWindowDrawList();
    private static void PlaceHotbarInputWindow(bool input)
    {
        // Pixels already live on the background canvas. Sending this invisible input
        // window back as well lets empty plugin carrier windows swallow its mouse input.
        // Only lift the hit surface under the mouse after excluding real covering windows.
        if (!input) return;
        var window = ImGuiP.GetCurrentWindow(); var mouse = ImGui.GetIO().MousePos;
        var rect = window.OuterRectClipped;
        if (mouse.X >= rect.Min.X && mouse.X < rect.Max.X && mouse.Y >= rect.Min.Y && mouse.Y < rect.Max.Y)
            ImGuiP.BringWindowToDisplayFront(window);
    }
    private static bool PluginWindowBlocksDrop() => PluginWindowAtMouse(true);
    private static unsafe bool PluginWindowAtMouse(bool allowEditor)
    {
        // HoveredWindow alone is insufficient if another overlay also sends itself
        // back. Do not let our transparent hit window steal its clicks or tooltips.
        var windows = ImGui.GetCurrentContext().Windows;
        var mouse = ImGui.GetIO().MousePos;
        for (var i = windows.Size - 1; i >= 0; i--)
        {
            var window = windows[i];
            if (window.IsNull || (!window.Active && !window.WasActive) || window.Hidden || window.IsFallbackWindow
                || (window.Flags & ImGuiWindowFlags.NoMouseInputs) != 0) continue;
            // A transparent carrier without chrome, drawing OR submitted items has no
            // hit target. Preserve invisible buttons, child content and real plugin windows.
            if ((window.Flags & (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoTitleBar))
                    == (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoTitleBar)
                && window.DrawList.VtxBuffer.Size == 0 && window.ContentSize.X <= 0 && window.ContentSize.Y <= 0
                && window.DC.CursorMaxPos.X <= window.DC.CursorStartPos.X
                && window.DC.CursorMaxPos.Y <= window.DC.CursorStartPos.Y) continue;
            var rect = window.OuterRectClipped;
            if (mouse.X < rect.Min.X || mouse.X >= rect.Max.X || mouse.Y < rect.Min.Y || mouse.Y >= rect.Max.Y) continue;
            // A decorationless transparent carrier often fills the viewport, with only
            // a few controls in one corner. Its unused outer area must not disable bars.
            // Content bounds retain invisible buttons; raw draw-list overlays are checked
            // against their vertices because they need not submit any ImGui items.
            if ((window.Flags & (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize))
                    == (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize)
                && !TransparentWindowContains(window, mouse)) continue;
            var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
            if (name.Contains("###NativeDragHotbar-", StringComparison.Ordinal)
                || allowEditor && name.Contains("###NDHEditor", StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    private static bool TransparentWindowContains(ImGuiWindowPtr window, Vector2 mouse)
    {
        var start = window.DC.CursorStartPos;
        var end = Vector2.Max(start + window.ContentSize, window.DC.CursorMaxPos);
        if (end.X > start.X && end.Y > start.Y && new HotbarRect(start, end).Contains(mouse)) return true;
        var vertices = window.DrawList.VtxBuffer;
        if (vertices.Size == 0) return false;
        var min = new Vector2(float.PositiveInfinity); var max = new Vector2(float.NegativeInfinity);
        for (var i = 0; i < vertices.Size; i++)
        { min = Vector2.Min(min, vertices[i].Pos); max = Vector2.Max(max, vertices[i].Pos); }
        return new HotbarRect(min, max).Contains(mouse);
    }

    private void DrawBelowNative(ImDrawListPtr list, HotbarRect bounds, Action draw)
    {
        if (nativeWindowCovers.Count == 0) { draw(); return; }
        foreach (var part in SuperHotbarVisibility.VisibleParts(bounds, nativeWindowCovers))
        {
            list.PushClipRect(part.Min, part.Max, true);
            try { draw(); } finally { list.PopClipRect(); }
        }
    }

}
