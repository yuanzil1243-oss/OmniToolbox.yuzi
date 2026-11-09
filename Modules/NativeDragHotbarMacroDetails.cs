using System;
using System.Collections.Generic;
using System.Globalization;
using Lumina.Excel.Sheets;
using OmniToolbox.Host;
using GameAction = Lumina.Excel.Sheets.Action;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed class NativeHotbarMacroDetails
{
    private readonly Dictionary<string, MacroDetailTarget?> commands = new(StringComparer.Ordinal);
    private readonly Dictionary<MacroIconDirective, MacroDetailTarget?> targets = [];

    internal bool TryResolve(string text, out MacroDetailTarget target)
    {
        if (!commands.TryGetValue(text, out var result))
        {
            result = NativeHotbarMacroDetailRules.TryResolve(text, FindCached, out var found) ? found : null;
            if (commands.Count >= 256) commands.Clear();
            commands[text] = result;
        }
        target = result.GetValueOrDefault();
        return result.HasValue;
    }

    private MacroDetailTarget? FindCached(MacroIconDirective spec)
    {
        if (targets.TryGetValue(spec, out var found)) return found;
        found = Find(spec);
        if (targets.Count >= 2048) targets.Clear();
        targets[spec] = found;
        return found;
    }

    private static MacroDetailTarget? Find(MacroIconDirective spec)
    {
        var text = spec.Category == "item" ? NativeHotbarMacroDetailRules.ItemName(spec.Name, out _) : spec.Name;
        var numeric = uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number);
        bool Match(string name, uint id) => numeric ? number == id : string.Equals(name, text, StringComparison.OrdinalIgnoreCase);
        var data = DalamudServices.DataManager;
        switch (spec.Category)
        {
            case "action": case "blueaction": case "pvpaction":
                foreach (var row in data.GetExcelSheet<GameAction>())
                    if (row.RowId > 0 && row.Icon > 0 && row.IsPvP == (spec.Category == "pvpaction")
                        && Match(row.Name.ExtractText(), row.RowId)) return new(1, row.RowId, row.Name.ExtractText());
                if (spec.Category != "action") break;
                foreach (var row in data.GetExcelSheet<CraftAction>())
                    if (row.RowId > 0 && Match(row.Name.ExtractText(), row.RowId)) return new(9, row.RowId, row.Name.ExtractText());
                foreach (var row in data.GetExcelSheet<GeneralAction>())
                    if (row.RowId > 0 && Match(row.Name.ExtractText(), row.RowId)) return new(10, row.RowId, row.Name.ExtractText());
                break;
            case "general":
                foreach (var row in data.GetExcelSheet<GeneralAction>())
                    if (row.RowId > 0 && Match(row.Name.ExtractText(), row.RowId)) return new(10, row.RowId, row.Name.ExtractText());
                break;
            case "item":
                NativeHotbarMacroDetailRules.ItemName(spec.Name, out var hq);
                if (numeric)
                {
                    if (NativeHotbarItems.TryItem(number, out var baseId, out var encodedHq))
                    {
                        var row = data.GetExcelSheet<Item>().GetRowOrDefault(baseId);
                        if (row is { } item && (!hq && !encodedHq || item.CanBeHq))
                            return new(2, baseId + (hq || encodedHq ? 1_000_000u : 0), item.Name.ExtractText());
                    }
                }
                else
                    foreach (var row in data.GetExcelSheet<Item>())
                        if (row.RowId > 0 && Match(row.Name.ExtractText(), row.RowId) && (!hq || row.CanBeHq))
                            return new(2, row.RowId + (hq ? 1_000_000u : 0), row.Name.ExtractText());
                if (!hq)
                    foreach (var row in data.GetExcelSheet<EventItem>())
                        if (row.RowId > 0 && Match(row.Name.ExtractText(), row.RowId)) return new(4, row.RowId, row.Name.ExtractText());
                break;
            case "mount":
                foreach (var row in data.GetExcelSheet<Mount>())
                    if (row.RowId > 0 && Match(row.Singular.ExtractText(), row.RowId)) return new(17, row.RowId, row.Singular.ExtractText());
                break;
            case "companion":
                foreach (var row in data.GetExcelSheet<Companion>())
                    if (row.RowId > 0 && Match(row.Singular.ExtractText(), row.RowId)) return new(13, row.RowId, row.Singular.ExtractText());
                break;
        }
        return null;
    }
}
