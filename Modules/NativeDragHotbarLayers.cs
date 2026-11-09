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
    private bool NativeWindowAtMouse => nativeWindowCovers.Exists(r => r.Contains(ImGui.GetIO().MousePos)
        && !nativeTooltipPassthrough.Contains(r));
    private static void SendHotbarWindowBack() => ImGuiP.BringWindowToDisplayBack(ImGuiP.GetCurrentWindow());
    private static unsafe bool PluginWindowBlocksDrop()
    {
        var window = ImGui.GetCurrentContext().HoveredWindow;
        if (window.IsNull) return false;
        if (!window.RootWindow.IsNull) window = window.RootWindow;
        var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
        return !name.Contains("###NativeDragHotbar-", StringComparison.Ordinal)
            && !name.Contains("###NDHEditor", StringComparison.Ordinal);
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
