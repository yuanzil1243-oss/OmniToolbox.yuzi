using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed class WorkshopProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "默认配置";
    public SuperHotbarDocument? Takeover { get; set; }
    public HotbarProfile Extensions { get; set; } = new() { Owner = "0000000000000000", Bars = [] };
    public bool ShowCommandMessages { get; set; } = true;
}

internal sealed class WorkshopLibrary
{
    public int Version { get; set; } = 1;
    public string SelectedId { get; set; } = "";
    public List<WorkshopProfile> Profiles { get; set; } = [];
    public WorkshopProfile Current => Profiles.Single(p => p.Id == SelectedId);
}

internal static class WorkshopProfiles
{
    // Portable storage never authorizes game writes. A fresh runtime copy is bound to the
    // actually logged-in character; SuperNativeArchive.Require still guards every native write.
    public const ulong PortableOwner = 1;
    public const int MaximumProfiles = 32;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 48,
        IgnoreReadOnlyProperties = true };

    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)
        ?? throw new InvalidDataException("配置为空。");

    public static ulong OwnerOf(SuperHotbarDocument value)
    {
        if (value.Character is null || value.Character.Length != 16
            || !ulong.TryParse(value.Character, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var owner)
            || owner == 0) throw new InvalidDataException("档案角色标识无效。");
        return owner;
    }

    public static SuperHotbarDocument Portable(SuperHotbarDocument value)
    {
        var source = OwnerOf(value);
        SuperHotbarRules.Validate(value, source);
        var result = Copy(value);
        if (result.MacroBackupCharacter.Length == 0 && source != PortableOwner)
            result.MacroBackupCharacter = source.ToString("X16");
        if (result.MacroBackupAt.Length == 0) result.MacroBackupAt = result.CapturedAt;
        result.Character = PortableOwner.ToString("X16");
        SuperHotbarRules.Validate(result, PortableOwner);
        return result;
    }

    public static SuperHotbarDocument? Bind(WorkshopProfile profile, ulong character)
    {
        if (character == 0) throw new InvalidDataException("请先登录角色。");
        Validate(profile);
        if (profile.Takeover is null) return null;
        var result = Copy(profile.Takeover);
        result.Character = character.ToString("X16");
        SuperHotbarRules.Validate(result, character);
        return result;
    }

    public static void Validate(WorkshopProfile profile)
    {
        if (profile is null || !Guid.TryParse(profile.Id, out _) || string.IsNullOrWhiteSpace(profile.Name)
            || profile.Name.Length > 128 || profile.Extensions is null) throw new InvalidDataException("配置名称或标识无效。");
        HotbarRules.Upgrade(profile.Extensions, 0);
        if (profile.Takeover is not null) SuperHotbarRules.Validate(profile.Takeover, PortableOwner);
    }

    public static void Validate(WorkshopLibrary library)
    {
        if (library.Version != 1) throw new NotSupportedException("配置库版本较新，未回退或覆盖。");
        if (library.Profiles is null || library.Profiles.Count is < 1 or > MaximumProfiles)
            throw new InvalidDataException("配置数量应为 1～32 套。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in library.Profiles)
        {
            Validate(profile);
            if (!ids.Add(profile.Id) || !names.Add(profile.Name.Trim())) throw new InvalidDataException("配置标识或名称重复。");
        }
        if (!library.Profiles.Any(p => p.Id == library.SelectedId)) throw new InvalidDataException("当前配置不存在。");
    }

    public static string UniqueName(WorkshopLibrary library, string requested)
    {
        var stem = string.IsNullOrWhiteSpace(requested) ? "新配置" : requested.Trim();
        stem = stem[..Math.Min(stem.Length, 112)];
        var name = stem;
        for (var suffix = 2; library.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = stem + " " + suffix;
        return name;
    }

    public static WorkshopProfile Add(WorkshopLibrary library, WorkshopProfile profile, string name)
    {
        if (library.Profiles.Count >= MaximumProfiles) throw new InvalidDataException("最多保存 32 套配置。");
        var added = Copy(profile);
        added.Id = Guid.NewGuid().ToString("N"); added.Name = UniqueName(library, name);
        Validate(added); library.Profiles.Add(added); library.SelectedId = added.Id;
        return added;
    }

    public static void UpdateMacroBackup(SuperHotbarDocument target, IReadOnlyList<SuperMacro> macros,
        ulong source, string sourceName, string timestamp)
    {
        if (source == 0 || macros is null || macros.Count != 200) throw new InvalidDataException("用户宏读取不完整，未更新备份。");
        var candidate = Copy(target);
        candidate.Macros = Copy(macros.ToList()); candidate.MacroBackupCharacter = source.ToString("X16");
        candidate.MacroBackupName = sourceName; candidate.MacroBackupAt = timestamp;
        SuperHotbarRules.Validate(candidate, OwnerOf(candidate));
        target.Macros = candidate.Macros; target.MacroBackupCharacter = candidate.MacroBackupCharacter;
        target.MacroBackupName = sourceName; target.MacroBackupAt = timestamp;
    }

    public static int ImportExtensions(WorkshopProfile target, HotbarShareDocument imported, bool replace, bool globals)
    {
        Validate(target);
        var extensions = replace ? new HotbarProfile { Owner = "0000000000000000", Show = target.Extensions.Show, Bars = [] }
            : Copy(target.Extensions);
        var first = HotbarTransfer.Append(extensions, 0, imported);
        if (globals && imported.ShowAllBars is { } show) extensions.Show = show;
        target.Extensions = extensions;
        if (globals && imported.ShowCommandMessages is { } messages) target.ShowCommandMessages = messages;
        return first;
    }
}

internal static class WorkshopLibraryStore
{
    public const string FileName = "HotbarWorkshopProfiles.json";
    private const string Format = "HotbarWorkshop.Library";
    private const int MaximumCharacters = 64_000_000;
    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public string Payload { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    public static string PathFor(string directory) => Path.Combine(directory, FileName);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Encode(WorkshopLibrary library)
    {
        WorkshopProfiles.Validate(library);
        var payload = JsonSerializer.Serialize(library, WorkshopProfiles.Json);
        var text = JsonSerializer.Serialize(new Envelope { Format = Format, Payload = payload, Sha256 = Hash(payload) }, WorkshopProfiles.Json);
        if (text.Length > MaximumCharacters) throw new InvalidDataException("本机配置库过大，未写入。");
        return text;
    }

    public static WorkshopLibrary Decode(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumCharacters) throw new InvalidDataException("本机配置库为空或过大。");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(text, WorkshopProfiles.Json)
                ?? throw new InvalidDataException("配置库为空。");
            if (envelope.Format != Format) throw new NotSupportedException("不支持此配置库格式，未覆盖。");
            if (envelope.Payload is null || envelope.Sha256 != Hash(envelope.Payload)) throw new InvalidDataException("配置库校验失败。");
            var library = JsonSerializer.Deserialize<WorkshopLibrary>(envelope.Payload, WorkshopProfiles.Json)
                ?? throw new InvalidDataException("配置库内容为空。");
            WorkshopProfiles.Validate(library); return library;
        }
        catch (JsonException error) { throw new InvalidDataException("配置库格式无效。", error); }
    }

    public static WorkshopLibrary Load(string directory, ulong currentCharacter, bool showMessages,
        out bool recovered, out bool migrated)
    {
        var path = PathFor(directory); recovered = migrated = false;
        if (File.Exists(path) || File.Exists(path + ".bak"))
        {
            Exception? first = null;
            foreach (var candidate in new[] { path, path + ".bak" })
                try
                {
                    var library = Decode(File.ReadAllText(candidate)); recovered = candidate != path;
                    if (recovered && File.Exists(path)) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"), false);
                    return library;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { first ??= error; }
            throw new InvalidDataException("配置库与备份均不可读，已暂停写入。", first);
        }
        var result = Migrate(directory, currentCharacter, showMessages);
        Save(directory, result, false); migrated = true; return result;
    }

    private static WorkshopLibrary Migrate(string directory, ulong currentCharacter, bool showMessages)
    {
        var extensions = HotbarSharedProfiles.Load(directory, currentCharacter, out _, out _);
        var result = new WorkshopLibrary();
        var paths = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "SuperHotbar-*.json*")
            .Select(p => p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => LegacyOwner(p, out _))
            .OrderByDescending(p => LegacyOwner(p, out var id) && id == currentCharacter)
            .ThenByDescending(p => File.GetLastWriteTimeUtc(File.Exists(p) ? p : p + ".bak")).ToArray() : [];
        if (paths.Length > WorkshopProfiles.MaximumProfiles)
            throw new InvalidDataException("旧角色档案超过 32 份，请先将不需要自动迁移的旧文件移到其他文件夹，原文件未改动。");
        foreach (var path in paths)
        {
            LegacyOwner(path, out var id);
            try
            {
                var data = SuperHotbarStore.Load(path, id, out _);
                var added = WorkshopProfiles.Add(result, new() { Takeover = WorkshopProfiles.Portable(data),
                    Extensions = WorkshopProfiles.Copy(extensions), ShowCommandMessages = showMessages },
                    result.Profiles.Count == 0 ? "默认配置" : "旧配置");
                if (result.Profiles.Count == 1) result.SelectedId = added.Id;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { throw new InvalidDataException("旧角色档案无法读取，暂停迁移以保留全部配置：" + Path.GetFileName(path), error); }
        }
        if (result.Profiles.Count == 0)
            WorkshopProfiles.Add(result, new() { Extensions = WorkshopProfiles.Copy(extensions), ShowCommandMessages = showMessages }, "默认配置");
        result.SelectedId = result.Profiles[0].Id;
        WorkshopProfiles.Validate(result); return result;
    }

    private static bool LegacyOwner(string path, out ulong owner)
    {
        owner = 0; var name = Path.GetFileName(path);
        return name.Length == 33 && name.StartsWith("SuperHotbar-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(name.AsSpan(12, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out owner) && owner != 0;
    }

    public static void Save(string directory, WorkshopLibrary library, bool recovered)
    {
        var text = Encode(library); Directory.CreateDirectory(directory);
        var path = PathFor(directory); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, recovered ? null : path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record WorkshopImport(WorkshopProfile? Profile, HotbarShareDocument? Extensions);

internal static class WorkshopProfileTransfer
{
    private const string Format = "HotbarWorkshop.Profile";
    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public int Version { get; set; } = 1;
        public string Payload { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Encode(WorkshopProfile profile)
    {
        WorkshopProfiles.Validate(profile);
        var payload = JsonSerializer.Serialize(profile, WorkshopProfiles.Json);
        var text = JsonSerializer.Serialize(new Envelope { Format = Format, Payload = payload, Sha256 = Hash(payload) }, WorkshopProfiles.Json);
        if (text.Length > 32_000_000) throw new InvalidDataException("导出配置过大。");
        return text;
    }
    public static WorkshopImport Decode(string text, HotbarProfile currentExtensions, bool currentMessages)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32_000_000) throw new InvalidDataException("导入文本为空或过大。");
        try
        {
            using var document = JsonDocument.Parse(text);
            var format = document.RootElement.GetProperty("Format").GetString();
            if (format == "NativeDragHotbar.Settings") return new(null, HotbarTransfer.Parse(text));
            if (format == Format)
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(text, WorkshopProfiles.Json)!;
                if (envelope.Version != 1 || envelope.Payload is null || envelope.Sha256 != Hash(envelope.Payload))
                    throw new InvalidDataException("导入配置版本或校验无效。");
                var profile = JsonSerializer.Deserialize<WorkshopProfile>(envelope.Payload, WorkshopProfiles.Json)
                    ?? throw new InvalidDataException("导入配置为空。");
                WorkshopProfiles.Validate(profile); return new(profile, null);
            }
            if (format is not ("SuperHotbar.Backup" or "SuperHotbar.BackupWithExtensions"))
                throw new InvalidDataException("不支持此导入格式。");
            // Discover the source identity only to validate the OLD envelope. Rebinding is an
            // explicit import into a portable profile, never a bypass of a game-write identity check.
            var raw = document.RootElement.GetProperty("Payload").GetString()!;
            if (format == "SuperHotbar.BackupWithExtensions")
            {
                using var combined = JsonDocument.Parse(raw);
                using var old = JsonDocument.Parse(combined.RootElement.GetProperty("Takeover").GetString()!);
                raw = old.RootElement.GetProperty("Payload").GetString()!;
            }
            using var data = JsonDocument.Parse(raw);
            var owner = Convert.ToUInt64(data.RootElement.GetProperty("Character").GetString(), 16);
            var backup = SuperHotbarClipboardStore.Decode(text, owner);
            return new(new WorkshopProfile { Name = "导入配置", Takeover = WorkshopProfiles.Portable(backup.Takeover),
                Extensions = WorkshopProfiles.Copy(backup.Extensions ?? currentExtensions),
                ShowCommandMessages = backup.ShowCommandMessages ?? currentMessages }, null);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or OverflowException or InvalidOperationException)
        { throw new InvalidDataException("不是有效的配置或扩展栏导出文本。", error); }
    }
}
