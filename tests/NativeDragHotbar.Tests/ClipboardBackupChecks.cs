using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class ClipboardBackupChecks
{
    internal static void Run(string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        const ulong owner = 0x1122334455667788;
        var takeover = new SuperHotbarDocument {
            Character = owner.ToString("X16"), NativeGroupCount = 1, NativeKeybindSize = 11,
            NativeStructsId = "00000000-0000-0000-0000-000000000001",
            Appearance = Enumerable.Range(0, 10).Select(_ => new DragHotbar()).ToList(),
            Groups = [new() { Index = 0, Bars = Enumerable.Range(0, 18).Select(_ =>
                Enumerable.Range(0, 16).Select(_ => new SuperSavedSlot()).ToList()).ToList() }],
            Bindings = Enumerable.Range(57, 120).Select(id => new SuperBinding { InputId = id,
                NativeData = Convert.ToBase64String(new byte[11]) }).ToList(),
            Macros = [new() { Set = 1, Index = 0, Name = "宏正文保留", Lines = Enumerable.Repeat("/ac 飞石 <t>", 15).ToList() }],
        };
        var extensions = new HotbarProfile { Owner = "0000000000000000", Show = false,
            Bars = [new() { Name = "扩展栏 A", Opacity = 0.3f, X = 123, Y = 456, AnchorX = 0.25f, AnchorY = 0.5f }] };
        HotbarRules.Resize(extensions.Bars[0], 10, 12);
        extensions.Bars[0].Slots[119] = new() { Mode = HotbarEntryMode.Command, Command = "/echo hidden", Name = "暂存内容", IconId = 234001 };
        HotbarRules.Resize(extensions.Bars[0], 4, 2);
        var oldText = SuperHotbarStore.Encode(takeover, owner);
        var originalExtensions = JsonSerializer.Serialize(extensions);
        var text = SuperHotbarClipboardStore.Encode(takeover, owner, extensions, false);
        var decoded = SuperHotbarClipboardStore.Decode(text, owner);
        check(SuperHotbarStore.Encode(decoded.Takeover, owner) == oldText && decoded.ShowCommandMessages == false,
            "combined clipboard backup preserves complete takeover archive, macros and command-feedback preference");
        check(decoded.Extensions is { Show: false } profile && JsonSerializer.Serialize(profile) == originalExtensions
            && profile.Bars[0].Slots[119].Command == "/echo hidden" && profile.Bars[0].Columns == 4,
            "combined backup retains extension visibility, geometry, opacity, exact IDs and hidden slots");
        check(JsonSerializer.Serialize(extensions) == originalExtensions && SuperHotbarStore.Encode(takeover, owner) == oldText,
            "copy and parse do not mutate source archives or execute any commands");
        decoded.Extensions!.Bars[0].Name = "changed";
        check(extensions.Bars[0].Name == "扩展栏 A", "decoded bundle owns independent extension objects");
        var legacy = SuperHotbarClipboardStore.Decode(oldText, owner);
        check(legacy.Extensions is null && legacy.ShowCommandMessages is null && SuperHotbarStore.Encode(legacy.Takeover, owner) == oldText,
            "existing takeover-only clipboard backup remains supported and omits extension changes");
        var empty = SuperHotbarClipboardStore.Decode(SuperHotbarClipboardStore.Encode(takeover, owner,
            new() { Owner = "0000000000000000", Bars = [] }, true), owner);
        check(empty.Extensions!.Bars.Count == 0 && empty.ShowCommandMessages == true, "zero-extension combined backup preserves an intentionally empty configuration");
        foreach (var bad in new[] { "", "[]", "{}", "null", "{\"Format\":false}", text.Replace("Payload", "LostPayload"), text.Replace("Sha256", "LostHash") })
            reject(() => SuperHotbarClipboardStore.Decode(bad, owner), "invalid or damaged clipboard bundle rejected before changes");
        string WithPayload(Action<System.Text.Json.Nodes.JsonNode> edit)
        {
            var envelope = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            var payload = System.Text.Json.Nodes.JsonNode.Parse(envelope["Payload"]!.GetValue<string>())!;
            edit(payload);
            var serialized = payload.ToJsonString();
            envelope["Payload"] = serialized;
            envelope["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serialized)));
            return envelope.ToJsonString();
        }
        var legacyCombined = SuperHotbarClipboardStore.Decode(WithPayload(p => {
            p.AsObject().Remove("ShowCommandMessages"); p["ShowHelp"] = false;
        }), owner);
        check(legacyCombined.Extensions is not null && legacyCombined.ShowCommandMessages is null,
            "2.2.5 combined backup remains importable and cannot suppress instructions or change command feedback");
        foreach (var malformed in new[] {
            WithPayload(p => p["Extensions"] = null),
            WithPayload(p => p["Extensions"]!["Owner"] = "0000000000000001"),
            WithPayload(p => p["Extensions"]!["Bars"] = null),
            WithPayload(p => p["Extensions"]!["Bars"]![0]!["Columns"] = 121),
            WithPayload(p => p["Extensions"]!["Bars"]![0]!["Slots"]![0]!["Mode"] = 999),
        }) reject(() => SuperHotbarClipboardStore.Decode(malformed, owner), "checksummed bundle still validates extension ownership, layout and slot content");
        var tampered = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        tampered["Payload"] = tampered["Payload"]!.GetValue<string>() + " ";
        reject(() => SuperHotbarClipboardStore.Decode(tampered.ToJsonString(), owner),
            "combined checksum detects altered payload bytes");
        var changedText = text.Replace("SuperHotbar.BackupWithExtensions", "unknown-format");
        reject(() => SuperHotbarClipboardStore.Decode(changedText, owner), "unknown bundle format cannot be imported");
        try { SuperHotbarClipboardStore.Decode(text, owner + 1); throw new Exception("wrong character accepted"); }
        catch (SuperHotbarIncompatibleException) { check(true, "combined archive retains existing same-character protection"); }
        var directory = Path.Combine(temp, "combined-backup"); Directory.CreateDirectory(directory);
        var sharedPath = HotbarSharedProfiles.PathFor(directory);
        HotbarProfileStore.Save(sharedPath, new() { Owner = "0000000000000000", Bars = [] }, 0, false);
        var previousShared = File.ReadAllBytes(sharedPath);
        var imported = SuperHotbarClipboardStore.Decode(text, owner);
        SuperHotbarClipboardStore.Save(directory, imported, owner, false, false);
        check(SuperHotbarStore.Load(Path.Combine(directory, $"SuperHotbar-{owner:X16}.json"), owner, out _).Macros[0].Name == "宏正文保留"
            && HotbarProfileStore.Load(sharedPath, 0, out _).Bars[0].Slots[119].Command == "/echo hidden"
            && File.ReadAllBytes(sharedPath + ".bak").SequenceEqual(previousShared),
            "confirmed combined import saves both stores and preserves the previous extension backup");
        var beforeLegacyImport = File.ReadAllBytes(sharedPath);
        SuperHotbarClipboardStore.Save(directory, legacy, owner, false, false);
        check(File.ReadAllBytes(sharedPath).SequenceEqual(beforeLegacyImport), "legacy import leaves existing extension file byte-for-byte intact");
        var failedDirectory = Path.Combine(temp, "combined-failure"); Directory.CreateDirectory(failedDirectory);
        var failedPath = HotbarSharedProfiles.PathFor(failedDirectory);
        HotbarProfileStore.Save(failedPath, new() { Owner = "0000000000000000", Bars = [] }, 0, false);
        File.WriteAllText(failedPath + ".bak", "preserve previous backup");
        var before = File.ReadAllBytes(failedPath); var beforeBak = File.ReadAllBytes(failedPath + ".bak");
        Directory.CreateDirectory(Path.Combine(failedDirectory, $"SuperHotbar-{owner:X16}.json"));
        try { SuperHotbarClipboardStore.Save(failedDirectory, imported, owner, false, false); throw new Exception("expected save failure"); }
        catch (SuperClipboardImportException error) {
            check(error.RolledBack && File.ReadAllBytes(failedPath).SequenceEqual(before)
                && File.ReadAllBytes(failedPath + ".bak").SequenceEqual(beforeBak),
                "failure saving second archive restores extension file and original .bak without partial import");
        }
        var missingDirectory = Path.Combine(temp, "combined-failure-no-shared"); Directory.CreateDirectory(missingDirectory);
        Directory.CreateDirectory(Path.Combine(missingDirectory, $"SuperHotbar-{owner:X16}.json"));
        try { SuperHotbarClipboardStore.Save(missingDirectory, imported, owner, false, false); throw new Exception("expected save failure"); }
        catch (SuperClipboardImportException error) {
            check(error.RolledBack && !File.Exists(HotbarSharedProfiles.PathFor(missingDirectory)),
                "failed first-time bundle import removes only its newly created extension file");
        }
    }
}
