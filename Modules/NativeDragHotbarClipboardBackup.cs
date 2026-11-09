using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed record SuperClipboardBackup(SuperHotbarDocument Takeover, HotbarProfile? Extensions, bool? ShowCommandMessages);

internal sealed class SuperClipboardImportException(string message, Exception inner, bool rolledBack)
    : IOException(message, inner)
{
    internal bool RolledBack { get; } = rolledBack;
}

internal static class SuperHotbarClipboardStore
{
    private const string Format = "SuperHotbar.BackupWithExtensions";
    private const int MaximumCharacters = 32_000_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 40 };
    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Payload { get; set; } = "";
    }
    private sealed class Payload
    {
        public string Takeover { get; set; } = "";
        public HotbarProfile? Extensions { get; set; }
        public bool? ShowCommandMessages { get; set; }
    }

    internal static string Encode(SuperHotbarDocument takeover, ulong owner, HotbarProfile extensions, bool showCommandMessages)
    {
        HotbarRules.Validate(extensions, HotbarSharedProfiles.Owner);
        var payload = JsonSerializer.Serialize(new Payload {
            Takeover = SuperHotbarStore.Encode(takeover, owner), Extensions = extensions, ShowCommandMessages = showCommandMessages,
        }, Json);
        var text = JsonSerializer.Serialize(new Envelope { Format = Format, Payload = payload, Sha256 = Hash(payload) }, Json);
        if (text.Length > MaximumCharacters) throw new InvalidDataException("备份过大，请分别导出接管档案和扩展栏。");
        return text;
    }

    internal static SuperClipboardBackup Decode(string text, ulong owner)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumCharacters)
            throw new InvalidDataException("备份为空或过大。");
        try
        {
            using var root = JsonDocument.Parse(text);
            if (root.RootElement.ValueKind != JsonValueKind.Object
                || !root.RootElement.TryGetProperty("Format", out var format) || format.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("不是有效的热键栏备份。");
            if (format.GetString() == "SuperHotbar.Backup") return new(SuperHotbarStore.Decode(text, owner), null, null);
            if (format.GetString() != Format) throw new InvalidDataException("不支持此备份格式。");
            var envelope = JsonSerializer.Deserialize<Envelope>(text, Json)!;
            if (envelope.Payload is null || envelope.Sha256 != Hash(envelope.Payload))
                throw new InvalidDataException("备份校验失败，请重新复制完整内容。");
            var payload = JsonSerializer.Deserialize<Payload>(envelope.Payload, Json);
            if (payload?.Takeover is null || payload.Extensions is null) throw new InvalidDataException("合并备份不完整。");
            var takeover = SuperHotbarStore.Decode(payload.Takeover, owner);
            HotbarRules.Upgrade(payload.Extensions, HotbarSharedProfiles.Owner);
            return new(takeover, payload.Extensions, payload.ShowCommandMessages);
        }
        catch (JsonException error) { throw new InvalidDataException("备份格式无效。", error); }
    }

    internal static void Save(string directory, SuperClipboardBackup backup, ulong owner,
        bool takeoverRecovered, bool extensionsRecovered)
    {
        // Validate both halves before the first write. Each store keeps its usual history;
        // if a later write fails, restore the current files AND their previous .bak files.
        _ = SuperHotbarStore.Encode(backup.Takeover, owner);
        if (backup.Extensions is { } extensions) HotbarRules.Validate(extensions, HotbarSharedProfiles.Owner);
        var takeoverPath = Path.Combine(directory, $"SuperHotbar-{owner:X16}.json");
        var extensionPath = HotbarSharedProfiles.PathFor(directory);
        var paths = backup.Extensions is null ? new[] { takeoverPath, takeoverPath + ".bak" }
            : new[] { takeoverPath, takeoverPath + ".bak", extensionPath, extensionPath + ".bak" };
        var originals = paths.Select(path => (Path: path, Bytes: File.Exists(path) ? File.ReadAllBytes(path) : null)).ToArray();
        try
        {
            if (backup.Extensions is { } profile) HotbarProfileStore.Save(extensionPath, profile, HotbarSharedProfiles.Owner, extensionsRecovered);
            SuperHotbarStore.Save(takeoverPath, backup.Takeover, owner, takeoverRecovered);
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var original in originals)
            {
                try
                {
                    if (original.Bytes is null) { if (File.Exists(original.Path)) File.Delete(original.Path); continue; }
                    if (File.Exists(original.Path) && File.ReadAllBytes(original.Path).AsSpan().SequenceEqual(original.Bytes)) continue;
                    var temporary = original.Path + ".rollback-" + Guid.NewGuid().ToString("N");
                    try { File.WriteAllBytes(temporary, original.Bytes); File.Move(temporary, original.Path, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                catch (Exception rollback) { failures.Add(rollback); }
            }
            throw new SuperClipboardImportException(failures.Count == 0 ? "导入保存失败，原配置已保留。"
                : "导入保存失败，部分文件未能恢复；请停用模块并检查配置文件。",
                failures.Count == 0 ? error : new AggregateException([error, .. failures]), failures.Count == 0);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
