using System;
using System.Text;
using Dalamud.Bindings.ImGui;
using Lumina.Text.ReadOnly;
using OmniToolbox.Host;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarNames
{
    internal static string? ItemName(uint id)
    {
        if (!NativeHotbarItems.TryItem(id, out var baseId, out _)) return null;
        return DalamudServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            .GetRowOrDefault(baseId)?.Name.ExtractText();
    }

    internal static string Plain(string text)
        => text.Contains('\u0002') ? new ReadOnlySeString(Encoding.UTF8.GetBytes(text)).ExtractText() : text;

    internal static string Tooltip(HotbarEntry entry)
    {
        var text = entry.Tooltip.Length > 0 ? entry.Tooltip : entry.Name;
        // Old extension-bar archives can still contain raw native payloads. Recover the item
        // name by ID rather than guessing which visible punctuation belongs to the payload.
        if (entry.HasNative && entry.NativeType == 2 && text.Contains('\u0002'))
            return ItemName(entry.NativeId) ?? Plain(text);
        return Plain(text);
    }
}

public sealed partial class NativeDragHotbar
{
    private void DrawSlotTooltip(HotbarEntry entry)
    {
        using var font = superFonts.PushTooltip();
        using var theme = new OmniGlassStyleScope();
        ImGui.BeginTooltip();
        try
        {
            // Cancel inherited plugin/window font shrinking only in this tooltip. The owned
            // atlas already rasterizes at integer pixels, so normal drawing stays 1:1 and crisp.
            ImGui.SetWindowFontScale(1);
            var pixels = NativeHotbarGameFonts.TooltipStyle.Pixels();
            ImGui.SetWindowFontScale(pixels / ImGui.GetFontSize());
            ImGui.TextUnformatted(entry.IsEmpty ? L("Empty") : NativeHotbarNames.Tooltip(entry));
            if (entry.Mode == HotbarEntryMode.Command && entry.Command.Length > 0)
                ImGui.TextUnformatted(entry.Command);
        }
        finally { ImGui.SetWindowFontScale(1); ImGui.EndTooltip(); }
    }
}
