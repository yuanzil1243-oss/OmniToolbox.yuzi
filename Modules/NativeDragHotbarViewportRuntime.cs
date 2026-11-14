using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private readonly TakeoverViewportState takeoverViewport = new();
    private void UpdateTakeoverViewport(Vector2 size)
    {
        if (super is null) { takeoverViewport.Reset(); return; }
        if (super.WindowReferenceWidth == 0 && size.X >= 640 && size.Y >= 480)
        {
            super.WindowReferenceWidth = size.X; super.WindowReferenceHeight = size.Y;
            superDirty = true; // Initialize once; resizing never rewrites the reference or saved bars.
        }
        var wasHidden = takeoverViewport.Hidden;
        takeoverViewport.Update(super.HideInSmallWindow, super.SmallWindowPercent,
            new(super.WindowReferenceWidth, super.WindowReferenceHeight), size, Environment.TickCount64);
        if (!wasHidden && takeoverViewport.Hidden)
        {
            if (movingBar is not null && SuperBarIndex(movingBar) >= 0) movingBar = null;
            if (localDragBar is not null && SuperBarIndex(localDragBar) >= 0)
            { localDragBar = null; localDragIndex = -1; }
            targets.RemoveAll(t => SuperBarIndex(t.Bar) >= 0 && !t.Editing);
        }
    }

    private void DrawTakeoverViewportSettings()
    {
        if (super is null) return;
        var enabled = super.HideInSmallWindow;
        if (ImGui.Checkbox("小窗口时隐藏接管栏", ref enabled))
        { super.HideInSmallWindow = enabled; superDirty = true; takeoverViewport.Reset(); }
        if (!enabled) return;
        var percent = super.SmallWindowPercent;
        if (ImGui.SliderInt("隐藏比例", ref percent, 30, 95, "%d%%"))
        { super.SmallWindowPercent = percent; superDirty = true; takeoverViewport.Reset(); }
        ImGui.TextWrapped("窗口宽度或高度低于正常大小的设定比例时，暂时隐藏接管栏；恢复大小后重新显示。快捷键照常使用，扩展栏不受影响。");
        ImGui.TextDisabled($"正常大小：{super.WindowReferenceWidth:0} × {super.WindowReferenceHeight:0}");
        if (ImGui.Button("以当前窗口作为正常大小"))
        {
            var size = ImGui.GetMainViewport().Size;
            if (size.X >= 640 && size.Y >= 480)
            { super.WindowReferenceWidth = size.X; super.WindowReferenceHeight = size.Y; superDirty = true; takeoverViewport.Reset(); }
        }
        if (takeoverViewport.Hidden) ImGui.TextWrapped("当前窗口较小，接管栏已临时隐藏。原有位置和显示开关保留。");
    }
}
