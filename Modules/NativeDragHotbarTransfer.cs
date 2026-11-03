using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed class HotbarShareDocument
{
    public string Format { get; set; } = "";
    public int Version { get; set; }
    public string ModuleVersion { get; set; } = "";
    public bool? ShowAllBars { get; set; }
    public bool? ShowHelp { get; set; }
    public List<DragHotbar> Bars { get; set; } = [];
}

internal static class HotbarTransfer
{
    private const string Format = "NativeDragHotbar.Settings";
    private const int MaximumCharacters = 32_000_000;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        MaxDepth = 32,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Export(HotbarProfile profile, int? selected, bool showHelp, string moduleVersion)
    {
        if (selected.HasValue && (selected.Value < 0 || selected.Value >= profile.Bars.Count))
            throw new InvalidDataException("没有选中可导出的热键栏。");
        var document = new HotbarShareDocument
        {
            Format = Format, Version = 1, ModuleVersion = moduleVersion,
            ShowAllBars = selected.HasValue ? null : profile.Show,
            ShowHelp = selected.HasValue ? null : showHelp,
            Bars = (selected.HasValue ? [profile.Bars[selected.Value]] : profile.Bars)
                .Select(bar => CopyBar(bar, false)).ToList(),
        };
        ValidateDocument(document);
        return JsonSerializer.Serialize(document, Json);
    }

    public static HotbarShareDocument Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumCharacters)
            throw new InvalidDataException("导入内容为空或过大。");
        try
        {
            var document = JsonSerializer.Deserialize<HotbarShareDocument>(text, Json)
                ?? throw new InvalidDataException("导入内容为空。");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException error)
        { throw new InvalidDataException("不是有效的拖放热键栏导出文本。", error); }
    }

    public static int Append(HotbarProfile profile, ulong owner, HotbarShareDocument document)
    {
        HotbarRules.Validate(profile, owner);
        ValidateDocument(document);
        if (profile.Bars.Count + document.Bars.Count > HotbarRules.MaxBars)
            throw new InvalidDataException("导入后热键栏会超过 12 条；请先删除不需要的栏。");

        // Validate the complete candidate before mutating the existing character's profile.
        var additions = document.Bars.Select(bar => CopyBar(bar, true)).ToList();
        var names = new HashSet<string>(profile.Bars.Select(bar => bar.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var bar in additions)
        {
            var original = bar.Name;
            for (var n = 2; !names.Add(bar.Name); n++)
            {
                var suffix = $" (导入{n})";
                bar.Name = original[..Math.Min(original.Length, 128 - suffix.Length)] + suffix;
            }
        }
        var candidate = new HotbarProfile { Owner = profile.Owner, Show = profile.Show,
            Bars = [.. profile.Bars, .. additions] };
        HotbarRules.Validate(candidate, owner);
        var first = profile.Bars.Count;
        profile.Bars.AddRange(additions);
        return first;
    }

    private static void ValidateDocument(HotbarShareDocument document)
    {
        if (document.Format != Format || document.Version != 1 || document.ModuleVersion is null
            || document.ModuleVersion.Length > 64 || document.Bars is null
            || document.Bars.Count < 1 || document.Bars.Count > HotbarRules.MaxBars)
            throw new InvalidDataException("导出格式、版本或热键栏数量不受支持。");
        HotbarRules.Validate(new HotbarProfile { Owner = 0UL.ToString("X16"), Bars = document.Bars }, 0);
    }

    private static DragHotbar CopyBar(DragHotbar bar, bool newIdentity) => new()
    {
        Id = newIdentity ? Guid.NewGuid().ToString("N") : bar.Id,
        Name = bar.Name, Visible = bar.Visible, HideEmpty = bar.HideEmpty,
        Columns = bar.Columns, Rows = bar.Rows, Scale = bar.Scale, Opacity = bar.Opacity,
        X = bar.X, Y = bar.Y, AnchorX = bar.AnchorX, AnchorY = bar.AnchorY,
        Slots = bar.Slots.Select(slot => slot.Copy()).ToList(),
    };
}
