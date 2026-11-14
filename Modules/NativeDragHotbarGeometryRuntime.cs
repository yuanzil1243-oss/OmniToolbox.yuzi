using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static unsafe class NativeBarGeometryReader
{
    public static bool HudEditing()
    {
        var agent = AgentHUDLayout.Instance();
        return agent != null && agent->IsAgentActive();
    }

    public static NativeBarGeometry Read(int index)
    {
        var addon = (AddonActionBarBase*)SuperNativeArchive.Addon(index);
        if (addon == null || addon->RootNode == null || addon->ActionBarSlotVector.LongCount != 12)
            throw new InvalidOperationException("等待原生栏节点就绪。");
        var root = addon->RootNode;
        var frame = addon->ActionBarSlotVector[0].IconFrame;
        Span<NativeNodeTransform> chain = stackalloc NativeNodeTransform[24];
        var reachedRoot = false; var depth = 0;
        // Hidden addons can retain stale ScreenX/ScreenY. Compose local transforms instead.
        for (var node = frame; node != null; node = node->ParentNode)
        {
            if (depth >= chain.Length) break;
            var factor = new Vector2(node->ScaleX, node->ScaleY);
            if (!float.IsFinite(factor.X) || !float.IsFinite(factor.Y) || factor.X <= 0 || factor.Y <= 0
                || Math.Abs(node->Rotation) > 0.001f) break;
            chain[depth++] = new(new(node->X, node->Y), factor, new(node->OriginX, node->OriginY));
            if (node != root) continue;
            reachedRoot = true; break;
        }
        if (!reachedRoot || frame->Width != 48 || frame->Height != 48)
            throw new InvalidOperationException("原生图标节点变换未通过校验。");
        var (position, scale) = NativeNodeGeometry.Compose(chain[..depth]);
        if (Math.Abs(scale.X - scale.Y) > 0.001f) throw new InvalidOperationException("原生栏使用了非等比缩放。");
        NativeHotbarLayout.TryNativeShape(SuperNativeArchive.ReadLayout(index), out var columns, out var rows);
        var result = new NativeBarGeometry(position, scale.X, columns, rows);
        result.Validate(); return result;
    }
}

public sealed partial class NativeDragHotbar
{
    private bool superHudEditing;

    private void UpdateSuperHudContext()
    {
        var editing = NativeBarGeometryReader.HudEditing();
        if (editing) RestoreSuperVisibility();
        if (editing && !superHudEditing)
        {
            CancelSuperBindingEdit();
            if (editBar is not null && SuperBarIndex(editBar) >= 0) CloseEditor();
            if (replacing is not null && SuperBarIndex(replacing.Bar) >= 0) replacing = null;
            if (localDragBar is not null && SuperBarIndex(localDragBar) >= 0) { localDragBar = null; localDragIndex = -1; }
            if (movingBar is not null && SuperBarIndex(movingBar) >= 0) movingBar = null;
            superVisuals.Clear();
            superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
        }
        superHudEditing = editing;
    }

    private void UpgradeSuperGeometry()
    {
        if (super is null || super.NativeGeometryVersion != 0) return;
        NativeBarGeometryMigration.Upgrade(super); superDirty = true;
        superStatus = "已转换旧档案中保存的栏位坐标；位置沿用原档案，内容与快捷键保留。";
    }

    private void SetSuperLocked(bool locked)
    {
        if (super is null || super.Locked == locked) return;
        super.Locked = locked; superDirty = true;
        if (locked)
        {
            if (editBar is not null && SuperBarIndex(editBar) >= 0) CloseEditor();
            if (replacing is not null && SuperBarIndex(replacing.Bar) >= 0) replacing = null;
            if (localDragBar is not null && SuperBarIndex(localDragBar) >= 0) { localDragBar = null; localDragIndex = -1; }
            if (movingBar is not null && SuperBarIndex(movingBar) >= 0) movingBar = null;
            targets.RemoveAll(t => SuperBarIndex(t.Bar) >= 0);
        }
        SaveSuper(true);
    }

    private void DrawSuperBar(DragHotbar bar, int index, bool nativeDragging, bool supported)
    {
        var view = ImGui.GetMainViewport();
        var input = NativeBarInteraction.Input(true, superHudEditing) && !NativeWindowAtMouse;
        if (input && !super!.Locked && ReferenceEquals(movingBar, bar) && !nativeDragging
            && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            bar.X += ImGui.GetIO().MouseDelta.X; bar.Y += ImGui.GetIO().MouseDelta.Y;
            superDirty = true;
        }
        var geometry = NativeBarGeometry.FromSaved(bar).OnViewport(view.Pos);
        var revealEmpty = nativeDragging || localDragBar is not null;
        var hasSurface = input && NativeBarInteraction.HasSurface(bar, index, revealEmpty);
        var mouseInput = hasSurface && NativeBarInteraction.Hit(geometry, bar, index, ImGui.GetIO().MousePos, revealEmpty);
        // Effects are drawn on the viewport background, so the input window needs no
        // transparent effects margin. Keep its mouse eligibility stable when the pointer
        // moves: ImGui resolves hover before this callback using the previous frame flags.
        var left = index == 0 ? new Vector2(34 * geometry.Scale, 0) : Vector2.Zero;
        var windowMin = geometry.FirstFrame - left;
        // Takeover surfaces belong to the game client. Without an explicit viewport,
        // shrinking the client can promote an off-screen carrier to a new OS window.
        // Background pixels would still target the main viewport while input migrated.
        var windowMax = geometry.FirstFrame + geometry.Size;
        if (!TakeoverViewportRules.Intersects(windowMin, windowMax, view.Pos, view.Size)) return;
        ImGui.SetNextWindowViewport(view.ID);
        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(geometry.Size + left, ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!hasSurface) flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * NativeBarInteraction.Alpha(true, superHudEditing));
        var contents = ImGui.Begin(HotbarInputWindowName(bar), flags);
        PlaceHotbarInputWindow(mouseInput);
        try
        {
            if (!contents) return;
            if (input && !super!.Locked && replacing is null && !nativeDragging && ImGui.GetIO().KeyAlt
                && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            { movingBar = bar; superDirty = true; }
            var list = HotbarDrawList(true);
            DrawBelowNative(list, new(geometry.FirstFrame, geometry.FirstFrame + geometry.Size), () =>
            {
                if (bar.Opacity > 0) list.AddRectFilled(geometry.FirstFrame, geometry.FirstFrame + geometry.Size,
                    ImGui.GetColorU32(new Vector4(0, 0, 0, bar.Opacity)), 3);
            });
            var hideEmpty = bar.HideEmpty;
            for (var slot = 0; slot < 12; slot++)
            {
                if (hideEmpty && !nativeDragging && localDragBar is null && bar.Slots[slot].IsEmpty) continue;
                ImGui.SetCursorScreenPos(geometry.Frame(slot));
                DrawSlot(bar, slot, 48 * geometry.Scale, nativeDragging, supported, true, input);
            }
            if (index == 0)
            {
                ImGui.SetCursorScreenPos(geometry.Lock);
                var clicked = ImGui.InvisibleButton("##SuperHotbarLock", new Vector2(20, 24) * geometry.Scale) && input;
                DrawBelowNative(list, new(geometry.Lock, geometry.Lock + new Vector2(20, 24) * geometry.Scale), () =>
                    superVisuals.DrawLock(list, super!.Locked, geometry.Lock, geometry.Scale, ImGui.GetStyle().Alpha));
                if (clicked && !nativeDragging && movingBar is null) SetSuperLocked(!super!.Locked);
            }
        }
        finally { ImGui.End(); ImGui.PopStyleVar(4); }
    }
}
