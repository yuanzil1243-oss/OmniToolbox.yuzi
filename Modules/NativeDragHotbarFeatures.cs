using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OmniToolbox.SuperHotbarInterop;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class HotbarCommandFeedback
{
    public static string Format(string message) => message.TrimEnd('。', '.') + "。";
    public static string Visibility(bool takeover, int index, string name, bool visible)
    {
        var kind = takeover ? "接管栏" : "扩展栏";
        var target = index < 0 ? "全部" + kind : $"{kind} #{index + 1}" + (string.IsNullOrWhiteSpace(name) ? "" : " " + name);
        return target + (visible ? " 已显示" : " 已隐藏");
    }
}

internal static class SuperHotbarCommands
{
    public static bool TryParse(string arguments, out bool takeover, out HotbarVisibilityAction action, out string selector)
    {
        var parts = arguments.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        takeover = parts.Length > 0 && parts[0].Equals("takeover", StringComparison.OrdinalIgnoreCase);
        return HotbarVisibilityCommands.TryParse(takeover && parts.Length == 2 ? parts[1] : takeover ? "" : arguments,
            out action, out selector);
    }
}

internal static class SuperInputKeys
{
    // Keep left/right mouse buttons exclusively available to ordinary UI interaction.
    public static bool Valid(int key) => key is >= 4 and <= 6
        || key is >= 8 and <= 254 && key is not (16 or 17 or 18 or 91 or 92 or >= 160 and <= 165);
    // The game stores mouse bindings in PAD_MMB/PAD_MB4/PAD_MB5, not Windows VK 4/5/6.
    // Keep the older low-code aliases readable, but always write the actual game encoding.
    public static int Windows(SuperHotbarKeyMap map, int key) => key switch
    { 161 => 4, 163 => 5, 164 => 6, >= 4 and <= 6 => key, _ => map.Windows(key) };
    public static int Native(SuperHotbarKeyMap map, int key) => key switch
    { 4 => 161, 5 => 163, 6 => 164, _ => map.Native(key) };
    public static bool TryNative(SuperHotbarKeyMap map, int key, out int native)
    { if (key is >= 4 and <= 6) { native = Native(map, key); return true; } return map.TryNative(key, out native); }

    public static SuperShortcut Read(SuperHotbarKeyMap map, int native, int modifiers)
    {
        if (native is < 0 or > 255 || modifiers is < 0 or > 7)
            throw new InvalidDataException("原生按键数据无效，未建立不完整档案。");
        try
        {
            var value = new SuperShortcut { Key = Windows(map, native), Modifiers = modifiers };
            if (SuperShortcut.Valid(value)) return value;
        }
        catch (InvalidDataException) { /* Unsupported keys must not abort the complete native snapshot. */ }
        return new() { UnmappedNativeKey = native, Modifiers = modifiers };
    }
}

internal sealed class SuperRepeatClock
{
    private readonly Dictionary<int, long> due = [];
    public void Clear() => due.Clear();
    public void Release(int key) => due.Remove(key);
    // A press is already executed by the edge poll. Never repeat it on this same frame.
    public bool Tick(int key, long now, float cps, bool eligible)
    {
        if (!eligible) { Release(key); return false; }
        var interval = Math.Max(1, (long)(1000 / (float.IsFinite(cps) ? Math.Clamp(cps, 1, 30) : 10)));
        if (!due.TryGetValue(key, out var next)) { due[key] = now + interval; return false; }
        if (now < next) return false;
        due[key] = now + interval; return true; // no catch-up bursts after a slow frame
    }
}

internal readonly record struct HotbarRect(Vector2 Min, Vector2 Max)
{
    public bool Valid => float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Max.X)
        && float.IsFinite(Max.Y) && Max.X > Min.X && Max.Y > Min.Y;
    public bool Contains(Vector2 point) => point.X >= Min.X && point.X < Max.X && point.Y >= Min.Y && point.Y < Max.Y;
}

internal static class SuperHotbarVisibility
{
    public static Vector2 Cell(int index, int columns, float size, Vector2 spacing) =>
        new(index % columns * (size + spacing.X), index / columns * (size + spacing.Y));
    public static bool HideEmpty(IReadOnlyDictionary<string, uint> options, int bar, bool fallback)
        => options.TryGetValue($"HotbarNoneSlotDisp{bar + 1:D2}", out var visible) ? visible == 0
            : options.TryGetValue("HotbarEmptyVisible", out visible) ? visible == 0 : fallback;
    public static List<HotbarRect> VisibleParts(HotbarRect area, IReadOnlyList<HotbarRect> covers)
    {
        var parts = new List<HotbarRect>(); if (area.Valid) parts.Add(area);
        foreach (var cover in covers)
        {
            if (!cover.Valid) continue;
            var next = new List<HotbarRect>();
            foreach (var part in parts)
            {
                var min = Vector2.Max(part.Min, cover.Min); var max = Vector2.Min(part.Max, cover.Max);
                if (max.X <= min.X || max.Y <= min.Y) { next.Add(part); continue; }
                Add(part.Min, new(part.Max.X, min.Y)); Add(new(part.Min.X, max.Y), part.Max);
                Add(new(part.Min.X, min.Y), new(min.X, max.Y)); Add(new(max.X, min.Y), new(part.Max.X, max.Y));
                void Add(Vector2 a, Vector2 b) { var rect = new HotbarRect(a, b); if (rect.Valid) next.Add(rect); }
            }
            parts = next;
            if (parts.Count == 0) break;
        }
        return parts;
    }
}

internal static class SuperHotbarResetStore
{
    // Move complete module profiles, backups and history out of the auto-load namespace.
    // A failed reset rolls all moved paths back before it reports failure.
    public static string Clear(string directory, bool includeExtra)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var targets = Directory.EnumerateFileSystemEntries(directory).Where(path =>
        {
            var name = Path.GetFileName(path);
            return ProfileName(name, "SuperHotbar-") || includeExtra && (ProfileName(name, "NativeDragHotbar-")
                || name.StartsWith("NativeDragHotbar-shared.json", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        var archive = Path.Combine(directory, "SuperHotbar-reset", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        var moved = new List<(string Source, string Target)>();
        var shared = HotbarSharedProfiles.PathFor(directory);
        try
        {
            foreach (var source in targets)
            {
                var target = Path.Combine(archive, Path.GetFileName(source));
                if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
                moved.Add((source, target));
            }
            if (includeExtra) HotbarProfileStore.Save(shared, new() { Owner = "0000000000000000", Bars = [] }, 0, false);
            return archive;
        }
        catch
        {
            if (includeExtra && File.Exists(shared) && !targets.Contains(shared, StringComparer.OrdinalIgnoreCase)) File.Delete(shared);
            // Save uses an atomic replace; a successfully created new empty profile must not block rollback.
            if (moved.Any(p => p.Source.Equals(shared, StringComparison.OrdinalIgnoreCase)) && File.Exists(shared)) File.Delete(shared);
            foreach (var (source, target) in moved.AsEnumerable().Reverse())
                if (Directory.Exists(target)) Directory.Move(target, source); else File.Move(target, source);
            throw;
        }
    }
    private static bool ProfileName(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length < prefix.Length + 21) return false;
        return ulong.TryParse(name.AsSpan(prefix.Length, 16), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out _) && name.AsSpan(prefix.Length + 16).StartsWith(".json", StringComparison.OrdinalIgnoreCase);
    }
}
