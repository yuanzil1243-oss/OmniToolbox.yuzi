using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class HotbarSharedProfiles
{
    // The persisted identity belongs to this local configuration, never to a logged-in character.
    public const ulong Owner = 0;
    public const string FileName = "NativeDragHotbar-shared.json";

    public static string PathFor(string directory) => Path.Combine(directory, FileName);

    public static HotbarProfile Load(string directory, ulong currentCharacter, out bool fromBackup,
        out string? migratedFrom)
    {
        var sharedPath = PathFor(directory);
        migratedFrom = null;
        if (Exists(sharedPath)) return HotbarProfileStore.Load(sharedPath, Owner, out fromBackup);

        fromBackup = false;
        HotbarProfile? current = null;
        var currentPath = Path.Combine(directory, $"NativeDragHotbar-{currentCharacter:X16}.json");
        var currentRecovered = false;
        if (currentCharacter != 0 && Exists(currentPath))
        {
            // Fail closed if the current character's main file and backup are both unreadable.
            current = HotbarProfileStore.Load(currentPath, currentCharacter, out currentRecovered);
            if (HasContent(current)) return Migrate(current, currentPath, currentRecovered, out migratedFrom);
        }

        HotbarProfile? fallback = null;
        string? fallbackPath = null;
        var fallbackRecovered = false;
        Exception? unreadable = null;
        foreach (var candidate in LegacyProfiles(directory)
            .Where(item => item.Owner != currentCharacter)
            .OrderByDescending(item => Modified(item.Path))
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            HotbarProfile legacy;
            bool recovered;
            try { legacy = HotbarProfileStore.Load(candidate.Path, candidate.Owner, out recovered); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidDataException)
            { unreadable ??= error; continue; }
            if (HasContent(legacy)) return Migrate(legacy, candidate.Path, recovered, out migratedFrom);
            if (fallback is null)
            { fallback = legacy; fallbackPath = candidate.Path; fallbackRecovered = recovered; }
        }
        // Do not silently initialize an empty shared profile while an unreadable legacy profile
        // may still contain the user's settings. Existing sources are never overwritten.
        if (unreadable is not null)
            throw new InvalidDataException("旧角色配置无法完整读取，暂停建立共享配置以保护原设置。", unreadable);
        if (current is not null) return Migrate(current, currentPath, currentRecovered, out migratedFrom);
        if (fallback is not null) return Migrate(fallback, fallbackPath!, fallbackRecovered, out migratedFrom);
        return new() { Owner = Owner.ToString("X16"), NeedsUpgradeSave = true };
    }

    private static HotbarProfile Migrate(HotbarProfile profile, string source, bool recovered,
        out string? migratedFrom)
    {
        profile.Owner = Owner.ToString("X16");
        profile.NeedsUpgradeSave = true;
        HotbarRules.Validate(profile, Owner);
        migratedFrom = recovered ? source + ".bak" : source;
        return profile;
    }

    private static bool HasContent(HotbarProfile profile) => profile.Bars.Any(bar => bar.Slots.Any(slot =>
        !slot.IsEmpty || slot.Mode != HotbarEntryMode.Native || slot.IconOverride
        || !string.IsNullOrEmpty(slot.Name) || !string.IsNullOrEmpty(slot.Tooltip)));

    private static bool Exists(string path) => File.Exists(path) || File.Exists(path + ".bak");

    private static DateTime Modified(string path) => File.Exists(path)
        ? File.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path + ".bak");

    private static IEnumerable<(string Path, ulong Owner)> LegacyProfiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        var paths = Directory.EnumerateFiles(directory, "NativeDragHotbar-*.json")
            .Concat(Directory.EnumerateFiles(directory, "NativeDragHotbar-*.json.bak").Select(path => path[..^4]))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var id = name["NativeDragHotbar-".Length..];
            if (id.Length == 16 && ulong.TryParse(id, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var owner) && owner != Owner)
                yield return (path, owner);
        }
    }
}
