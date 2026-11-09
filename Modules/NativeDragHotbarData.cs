using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniToolbox.NativeDragHotbarLocal;

internal enum HotbarEntryMode { Native, Command }

internal sealed class HotbarEntry
{
    public HotbarEntryMode Mode { get; set; }
    public byte NativeType { get; set; }
    public uint NativeId { get; set; }
    public uint IconId { get; set; }
    public bool IconOverride { get; set; }
    public string Name { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public string Command { get; set; } = "";
    // Unrecognized native types remain lossless backups, but are never executed as guessed actions.
    public byte PreservedNativeType { get; set; }
    public uint PreservedNativeId { get; set; }
    [JsonIgnore] public bool HasNative => HotbarRules.IsSupportedType(NativeType) && HotbarRules.IsValidId(NativeType, NativeId);
    [JsonIgnore] public bool IsEmpty => !HasNative && PreservedNativeType == 0 && string.IsNullOrWhiteSpace(Command) && IconId == 0;
    public HotbarEntry Copy() => new() { Mode = Mode, NativeType = NativeType, NativeId = NativeId,
        IconId = IconId, IconOverride = IconOverride, Name = Name, Tooltip = Tooltip, Command = Command,
        PreservedNativeType = PreservedNativeType, PreservedNativeId = PreservedNativeId };
}

internal sealed class DragHotbar
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "扩展栏";
    public bool Visible { get; set; } = true;
    public bool HideEmpty { get; set; }
    public int Columns { get; set; } = 12;
    public int Rows { get; set; } = 1;
    [JsonIgnore] public int SlotCount => Columns * Rows;
    public float Scale { get; set; } = 1f;
    public float Opacity { get; set; } = 0.85f;
    public float X { get; set; } = 400;
    public float Y { get; set; } = 300;
    public float? AnchorX { get; set; }
    public float? AnchorY { get; set; }
    // Only used for takeover bars. Extra bars keep their existing independent layout.
    public bool FollowNativeHud { get; set; } = true;
    public List<HotbarEntry> Slots { get; set; } = Enumerable.Range(0, 12).Select(_ => new HotbarEntry()).ToList();
}

internal sealed class HotbarProfile
{
    public int SchemaVersion { get; set; } = 2;
    [JsonIgnore] public bool NeedsUpgradeSave { get; set; }
    public string Owner { get; set; } = "";
    public bool Show { get; set; } = true;
    public List<DragHotbar> Bars { get; set; } = [new()];
}

internal static class HotbarRules
{
    public const int MaxBars = 12;
    public const int MaxSlots = 120;
    private static readonly int[] LegacyLayouts = [12, 6, 4, 3, 2, 1];

    public static bool IsValidLayout(int columns, int rows) => columns > 0 && rows > 0
        && (long)columns * rows <= MaxSlots;

    public static void Resize(DragHotbar bar, int columns, int rows)
    {
        if (!IsValidLayout(columns, rows)) throw new InvalidDataException("横向和纵向必须为正整数，每栏最多 120 格。");
        // Keep every previously allocated slot so decreasing the visible area never destroys content.
        while (bar.Slots.Count < columns * rows) bar.Slots.Add(new());
        bar.Columns = columns;
        bar.Rows = rows;
    }

    public static void Upgrade(HotbarProfile profile, ulong owner)
    {
        Validate(profile, owner);
        if (profile.SchemaVersion == 2) return;
        foreach (var bar in profile.Bars) bar.Rows = 12 / bar.Columns;
        profile.SchemaVersion = 2;
        profile.NeedsUpgradeSave = true;
        Validate(profile, owner);
    }
    // Only types understood by this release. Inventory-location types must be normalized before saving.
    public static bool IsSupportedType(byte type) => type is 1 or 2 or 4 or 6 or 7 or 8 or 9 or 10 or 11
        or 12 or 13 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 24 or 25 or 26 or 27 or 29 or 30 or 31 or 32 or 33 or 34 or 35;
    public static bool IsValidId(byte type, uint id) => type switch
    {
        7 => (id >> 8) <= 1 && (id & 255) < 100,
        15 => id < 100,
        8 => id <= 17 || id == 255,
        18 => id < 8 || id == 255,
        >= 21 and <= 35 => id < 10_000_000,
        _ => id > 0 && id < 10_000_000,
    };

    public static IEnumerable<string> Commands(string text)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var command = raw.Trim().TrimEnd(';').Trim();
            if (command.Length == 0) continue;
            if (command[0] == '／') command = "/" + command[1..];
            if (command[0] == '＼') command = "\\" + command[1..];
            if (command[0] is not ('/' or '\\')) command = "/" + command;
            yield return command;
        }
    }

    public static bool TryResolveBarSelector(IReadOnlyList<DragHotbar> bars, string selector, out int index)
    {
        index = -1;
        selector = selector.Trim();
        var number = selector.StartsWith('#') ? selector[1..] : selector;
        if (int.TryParse(number, out var ordinal) && ordinal > 0 && ordinal <= bars.Count)
        { index = ordinal - 1; return true; }

        var matches = Enumerable.Range(0, bars.Count)
            .Where(i => string.Equals(bars[i].Name, selector, StringComparison.OrdinalIgnoreCase)
                || string.Equals($"#{i + 1} {bars[i].Name}", selector, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length != 1) return false;
        index = matches[0];
        return true;
    }

    public static string? MacroDropText(HotbarEntry entry, string? emoteCommand = null)
    {
        if (!entry.HasNative || string.IsNullOrWhiteSpace(entry.Name)) return null;
        // Match the native macro editor: action/menu entries insert their name,
        // while emotes use the actual TextCommand supplied by the game sheet.
        return entry.NativeType == 6
            ? string.IsNullOrWhiteSpace(emoteCommand) ? null : NormalizeMacroCommand(emoteCommand)
            : entry.Name.Trim();
    }

    public static bool TryAppendMacroCommand(string current, string command, out string result)
    {
        result = current;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var separator = string.IsNullOrWhiteSpace(current) || current.EndsWith('\n') ? "" : "\n";
        var candidate = current + separator + command;
        if (candidate.Length > 2048) return false;
        result = candidate;
        return true;
    }

    private static string NormalizeMacroCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length > 0 && trimmed[0] == '／') trimmed = "/" + trimmed[1..];
        return trimmed.Length > 0 && trimmed[0] == '/' ? trimmed : "/" + trimmed;
    }

    public static void Validate(HotbarProfile profile, ulong owner)
    {
        if (profile.SchemaVersion is not (1 or 2) || profile.Owner != owner.ToString("X16"))
            throw new InvalidDataException("配置版本或身份标记不匹配。");
        if (profile.Bars is null || profile.Bars.Count > MaxBars) throw new InvalidDataException("热键栏数量无效。");
        var ids = new HashSet<string>();
        foreach (var bar in profile.Bars)
        {
            if (bar is null || !Guid.TryParseExact(bar.Id, "N", out _) || !ids.Add(bar.Id)
                || bar.Name is null || bar.Name.Length > 128
                || (profile.SchemaVersion == 1 ? !LegacyLayouts.Contains(bar.Columns) : !IsValidLayout(bar.Columns, bar.Rows))
                || !float.IsFinite(bar.Scale) || bar.Scale is < 0.5f or > 2.5f
                || !float.IsFinite(bar.Opacity) || bar.Opacity is < 0 or > 1
                || !float.IsFinite(bar.X) || !float.IsFinite(bar.Y)
                || (bar.AnchorX.HasValue != bar.AnchorY.HasValue)
                || (bar.AnchorX is { } anchorX && (!float.IsFinite(anchorX) || anchorX is < 0 or > 1))
                || (bar.AnchorY is { } anchorY && (!float.IsFinite(anchorY) || anchorY is < 0 or > 1))
                || bar.Slots is null || (profile.SchemaVersion == 1 ? bar.Slots.Count != 12
                    : bar.Slots.Count < bar.SlotCount || bar.Slots.Count > MaxSlots))
                throw new InvalidDataException("热键栏布局无效。");
            foreach (var slot in bar.Slots)
            {
                if (slot is null || !Enum.IsDefined(slot.Mode) || slot.Name is null || slot.Name.Length > 256
                    || slot.Tooltip is null || slot.Tooltip.Length > 1024 || slot.Command is null || slot.Command.Length > 2048
                    || slot.IconId > 10_000_000
                    || (slot.PreservedNativeType == 0 && slot.PreservedNativeId != 0)
                    || (slot.PreservedNativeType != 0 && (slot.NativeType != 0 || slot.NativeId != 0))
                    || (slot.NativeType != 0 && !slot.HasNative)
                    || (slot.NativeType == 0 && slot.NativeId != 0))
                    throw new InvalidDataException("格子内容无效。");
            }
        }
    }
}

internal static class HotbarProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static HotbarProfile Load(string path, ulong owner, out bool fromBackup)
    {
        fromBackup = false;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new() { Owner = owner.ToString("X16") };
        Exception? original = null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            HotbarProfile? parsed = null;
            try
            {
                var result = parsed = JsonSerializer.Deserialize<HotbarProfile>(File.ReadAllText(candidate), Json)
                    ?? throw new InvalidDataException("配置为空。");
                HotbarRules.Upgrade(result, owner);
                fromBackup = candidate != path;
                if (fromBackup && File.Exists(path))
                    File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
                return result;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A newer schema or another owner's valid JSON is not corruption to roll back.
                if (parsed is not null && (parsed.SchemaVersion is not (1 or 2) || parsed.Owner != owner.ToString("X16"))) throw;
                original ??= error;
            }
        }
        throw new InvalidDataException("配置及备份均无法读取，暂停写入以保护原文件。", original);
    }

    public static void Save(string path, HotbarProfile profile, ulong owner, bool recovering)
    {
        HotbarRules.Upgrade(profile, owner);
        var snapshot = JsonSerializer.Serialize(profile, Json);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, snapshot);
            if (File.Exists(path)) File.Replace(temporary, path, recovering ? null : path + ".bak");
            else File.Move(temporary, path);
            profile.NeedsUpgradeSave = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
