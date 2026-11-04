using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;
using OmniToolbox.Host;
using GameAction = Lumina.Excel.Sheets.Action;

namespace OmniToolbox.NativeDragHotbarLocal;

internal readonly record struct MacroIconTarget(byte Type, uint Id, uint Icon);

internal sealed class NativeHotbarMacroIcons
{
    private readonly Dictionary<MacroIconDirective, MacroIconTarget> targets = [];
    private readonly Dictionary<string, MacroIconDirective?> directives = new(StringComparer.Ordinal);

    public uint Resolve(string command, NativeHotbarBridge? bridge)
    {
        if (string.IsNullOrWhiteSpace(command)) return 0;
        if (!directives.TryGetValue(command, out var parsed))
        {
            parsed = HotbarPresentation.TryIconDirective(command, out var found) ? found : null;
            if (directives.Count >= 2048) directives.Clear();
            directives[command] = parsed;
        }
        if (parsed is not { } directive) return 0;
        if (!targets.TryGetValue(directive, out var target))
        {
            target = Find(directive);
            if (targets.Count >= 2048) targets.Clear();
            targets[directive] = target;
        }
        if (target.Type != 0 && bridge is not null)
        {
            var dynamic = bridge.CurrentIcon(new() { NativeType = target.Type, NativeId = target.Id, IconId = target.Icon });
            if (dynamic > 0) return dynamic;
        }
        return target.Icon;
    }

    private static MacroIconTarget Find(MacroIconDirective spec)
    {
        bool Match(string name, uint id) => string.Equals(name, spec.Name, StringComparison.OrdinalIgnoreCase)
            || uint.TryParse(spec.Name, out var number) && number == id;
        switch (spec.Category)
        {
            case "emote": case "情感动作": case "情感動作":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<Emote>())
                    if (row.RowId > 0 && Match(row.Name.ToString(), row.RowId)) return new(6, row.RowId, row.Icon);
                break;
            case "action": case "pvpaction": case "blueaction": case "技能":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<GameAction>())
                    if (row.RowId > 0 && row.Icon > 0 && Match(row.Name.ToString(), row.RowId)) return new(1, row.RowId, row.Icon);
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<CraftAction>())
                    if (row.RowId > 0 && Match(row.Name.ToString(), row.RowId)) return new(9, row.RowId, row.Icon);
                break;
            case "general": case "generalaction": case "通用技能": case "通用":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<GeneralAction>())
                    if (row.RowId > 0 && Match(row.Name.ToString(), row.RowId)) return new(10, row.RowId, (uint)Math.Max(0, row.Icon));
                break;
            case "item": case "道具": case "物品":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<Item>())
                    if (row.RowId > 0 && Match(row.Name.ToString(), row.RowId)) return new(2, row.RowId, row.Icon);
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<EventItem>())
                    if (row.RowId > 0 && Match(row.Name.ToString(), row.RowId)) return new(4, row.RowId, row.Icon);
                break;
            case "mount": case "坐骑": case "坐騎":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<Mount>())
                    if (row.RowId > 0 && Match(row.Singular.ToString(), row.RowId)) return new(17, row.RowId, row.Icon);
                break;
            case "minion": case "companion": case "宠物": case "寵物":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<Companion>())
                    if (row.RowId > 0 && Match(row.Singular.ToString(), row.RowId)) return new(13, row.RowId, row.Icon);
                break;
            case "pet": case "petaction": case "召唤兽": case "召喚獸":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<PetAction>())
                    if (row.RowId > 0 && Match(row.Name.ToString(), row.RowId)) return new(16, row.RowId, (uint)Math.Max(0, row.Icon));
                break;
            case "classjob": case "职业": case "職業":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<ClassJob>())
                    if (row.RowId > 0 && Match(row.Name.ToString(), row.RowId)) return new(19, row.RowId, 0);
                break;
            case "marking": case "enemysign": case "标记": case "標記":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<Marker>())
                    if (Match(row.Name.ToString(), row.RowId)) return new(0, 0, (uint)Math.Max(0, row.Icon));
                break;
            case "gearset": case "套装": case "套裝":
                if (uint.TryParse(spec.Name, out var set) && set is > 0 and <= 100) return new(15, set - 1, 0);
                break;
            case "waymark": case "地面标记": case "地面標記":
                var mark = spec.Name.ToUpperInvariant();
                if (mark.Length == 1 && mark[0] is >= 'A' and <= 'D') return new(18, (uint)(mark[0] - 'A'), 0);
                if (uint.TryParse(mark, out var digit) && digit is >= 1 and <= 4) return new(18, digit + 3, 0);
                break;
            case "ornament": case "时尚配饰": case "時尚配飾":
                foreach (var row in DalamudServices.DataManager.GetExcelSheet<Ornament>())
                    if (row.RowId > 0 && Match(row.Singular.ToString(), row.RowId)) return new(0, 0, row.Icon);
                break;
        }
        return default;
    }
}
