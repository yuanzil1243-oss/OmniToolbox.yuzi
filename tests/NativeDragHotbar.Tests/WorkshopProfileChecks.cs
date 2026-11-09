using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniToolbox.NativeDragHotbarLocal;

internal static class WorkshopProfileChecks
{
    private const ulong A = 0x1122334455667788, B = 0x8877665544332211;
    internal static SuperHotbarDocument Fresh(ulong owner = A) => new()
    {
        Character = owner.ToString("X16"), CapturedAt = "2026-11-09T01:00:00Z", NativeGroupCount = 2,
        NativeKeybindSize = 11, NativeStructsId = "00000000-0000-0000-0000-000000000001",
        KeyboardEncodingVersion = 1, AppearanceDefaultsVersion = 1, EmptySlotDefaultsVersion = 1,
        AppearanceMappingVersion = 1, NativeGeometryVersion = 1,
        Appearance = Enumerable.Range(0, 10).Select(i => new DragHotbar { Name = "栏 " + (i + 1) }).ToList(),
        Groups = Enumerable.Range(0, 2).Select(g => new SuperSavedGroup { Index = g,
            Bars = Enumerable.Range(0, 18).Select(_ => Enumerable.Range(0, 16)
                .Select(_ => new SuperSavedSlot()).ToList()).ToList() }).ToList(),
        Bindings = Enumerable.Range(57, 120).Select(id => new SuperBinding { InputId = id,
            NativeData = Convert.ToBase64String(new byte[11]) }).ToList(),
        Macros = Enumerable.Range(0, 200).Select(i => new SuperMacro { Set = i / 100, Index = i % 100,
            Name = "宏 " + i, Lines = Enumerable.Range(0, 15).Select(l => l == 0 ? "/echo " + i : "").ToList() }).ToList(),
    };
    private static WorkshopProfile Profile() => new()
    {
        Takeover = WorkshopProfiles.Portable(Fresh()),
        Extensions = new() { Owner = "0000000000000000", Bars = [new() { Name = "道具栏" }] },
    };
    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value, WorkshopProfiles.Json);
    private static string Rehash(string text, Action<JsonNode> edit)
    {
        var envelope = JsonNode.Parse(text)!;
        var payload = JsonNode.Parse(envelope["Payload"]!.GetValue<string>())!;
        edit(payload); var raw = payload.ToJsonString(); envelope["Payload"] = raw;
        envelope["Sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return envelope.ToJsonString();
    }

    internal static void Run(string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        var directory = Path.Combine(temp, "workshop"); Directory.CreateDirectory(directory);
        var library = new WorkshopLibrary();
        var first = WorkshopProfiles.Add(library, Profile(), "自己的名称");
        first.Takeover!.EditedBars["1:0"] = Enumerable.Range(0, 12).Select(i => new HotbarEntry
            { NativeType = i == 0 ? (byte)7 : (byte)0, NativeId = i == 0 ? 256u : 0 }).ToList();
        first.Takeover.Bindings[0].Primary = new() { Key = 65, Modifiers = 2 };
        first.Takeover.Bindings[0].PrimaryEdited = true;
        first.Takeover.BuiltInRepeat = true; first.Takeover.RepeatCps = 12;
        first.Extensions.Show = false; first.ShowCommandMessages = false;
        var second = WorkshopProfiles.Add(library, first, "自己的名称");
        check(first.Id != second.Id && second.Name == "自己的名称 2" && library.SelectedId == second.Id,
            "copy creates a distinct user-named selected profile without predefined categories");
        second.Extensions.Bars[0].Name = "副本栏"; second.Takeover!.Macros[0].Lines[0] = "/echo 副本";
        check(first.Extensions.Bars[0].Name == "道具栏" && first.Takeover.Macros[0].Lines[0] == "/echo 0",
            "profile copies deeply isolate bars and macro lines");
        WorkshopLibraryStore.Save(directory, library, false);
        var loaded = WorkshopLibraryStore.Load(directory, B, true, out var recovered, out var migrated);
        check(!recovered && !migrated && loaded.SelectedId == second.Id && loaded.Current.Name == second.Name,
            "last selection persists when another character loads the local library");
        var portableBefore = Snapshot(loaded);
        var boundA = WorkshopProfiles.Bind(loaded.Current, A)!;
        var boundB = WorkshopProfiles.Bind(loaded.Current, B)!;
        check(boundA.Character == A.ToString("X16") && boundB.Character == B.ToString("X16")
            && boundB.MacroBackupCharacter == A.ToString("X16") && Snapshot(loaded) == portableBefore,
            "each character receives a fresh runtime binding while original macro source and portable data stay intact");
        check(boundB.EditedBars["1:0"][0].NativeId == 256 && boundB.Bindings[0].Primary.Key == 65
            && boundB.BuiltInRepeat && boundB.RepeatCps == 12 && !loaded.Current.Extensions.Show && !loaded.Current.ShowCommandMessages,
            "job-specific edited macros, bindings, repeat and extension/message preferences survive cross-character binding");
        boundA.Macros[0].Lines[0] = "/echo runtime";
        check(boundB.Macros[0].Lines[0] == "/echo 副本", "characters never share mutable runtime macro arrays");
        reject(() => SuperHotbarRules.Validate(boundA, B), "cross-character runtime fails the unchanged strict native identity validator");
        reject(() => WorkshopProfiles.Bind(loaded.Current, 0), "logged-out state cannot obtain a native-write runtime identity");
        loaded.Current.Name = "重新命名"; WorkshopLibraryStore.Save(directory, loaded, false);
        check(WorkshopLibraryStore.Load(directory, A, true, out _, out _).Current.Name == "重新命名", "rename survives save/reload");
        loaded.Profiles.Remove(loaded.Current); loaded.SelectedId = first.Id; WorkshopLibraryStore.Save(directory, loaded, false);
        check(WorkshopLibraryStore.Load(directory, B, true, out _, out _).Profiles.Count == 1, "deleting one configuration preserves the other and its selected identity");
        var invalid = WorkshopProfiles.Copy(loaded); invalid.Profiles.Clear();
        reject(() => WorkshopProfiles.Validate(invalid), "deleting the final configuration is rejected");
        invalid = WorkshopProfiles.Copy(loaded); invalid.SelectedId = Guid.NewGuid().ToString("N");
        reject(() => WorkshopProfiles.Validate(invalid), "a nonexistent selected configuration cannot be saved");
        invalid = WorkshopProfiles.Copy(loaded); WorkshopProfiles.Add(invalid, invalid.Current, "another"); invalid.Current.Name = invalid.Profiles[0].Name;
        reject(() => WorkshopProfiles.Validate(invalid), "duplicate profile names reject the complete candidate");
        var cap = new WorkshopLibrary();
        for (var i = 0; i < WorkshopProfiles.MaximumProfiles; i++) WorkshopProfiles.Add(cap, new(), "配置");
        var capBefore = Snapshot(cap); reject(() => WorkshopProfiles.Add(cap, new(), "溢出"), "33rd configuration is rejected");
        check(Snapshot(cap) == capBefore, "profile limit failure keeps selection and all existing configurations intact");

        var target = WorkshopProfiles.Copy(first.Takeover);
        var beforeMacro = JsonNode.Parse(Snapshot(target))!;
        var live = Fresh(B).Macros; live[0].Lines[0] = "/echo 刚编辑的宏";
        WorkshopProfiles.UpdateMacroBackup(target, live, B, "来源角色", "2026-11-09T03:00:00Z");
        var expected = JsonNode.Parse(Snapshot(target))!;
        foreach (var name in new[] { "Macros", "MacroBackupCharacter", "MacroBackupName", "MacroBackupAt" }) { beforeMacro.AsObject().Remove(name); expected.AsObject().Remove(name); }
        check(JsonNode.DeepEquals(beforeMacro, expected) && target.MacroBackupName == "来源角色" && target.Macros[0].Lines[0] == live[0].Lines[0],
            "macro-only refresh changes exactly macro backup and its source/time; all other settings remain byte-equivalent");
        live[0].Lines[0] = "external edit";
        check(target.Macros[0].Lines[0] == "/echo 刚编辑的宏", "saved macro backup does not follow later live edits or source array mutations");
        var macroBefore = Snapshot(target);
        reject(() => WorkshopProfiles.UpdateMacroBackup(target, live.Take(199).ToList(), B, "", ""), "partial macro capture is rejected");
        var malformed = WorkshopProfiles.Copy(live); malformed[199].Index = 98;
        reject(() => WorkshopProfiles.UpdateMacroBackup(target, malformed, B, "", ""), "duplicate macro positions are rejected");
        malformed = WorkshopProfiles.Copy(live); malformed[0].Lines.RemoveAt(14);
        reject(() => WorkshopProfiles.UpdateMacroBackup(target, malformed, B, "", ""), "incomplete macro lines are rejected");
        check(Snapshot(target) == macroBefore, "failed macro capture cannot partially replace the saved backup");

        Transfer(check, reject);
        Storage(directory, loaded, check, reject);
        Migration(temp, check, reject);
    }

    private static void Transfer(Action<bool, string> check, Action<Action, string> reject)
    {
        var profile = Profile(); var before = Snapshot(profile);
        var encoded = WorkshopProfileTransfer.Encode(profile);
        var decoded = WorkshopProfileTransfer.Decode(encoded, new() { Owner = "0000000000000000" }, false);
        check(decoded.Extensions is null && Snapshot(decoded.Profile) == before, "complete profile export/import preserves saved macros and extensions exactly");
        reject(() => WorkshopProfileTransfer.Decode(encoded.Replace("HotbarWorkshop.Profile", "Unknown.Profile"), profile.Extensions, true), "unknown clipboard format is rejected");
        reject(() => WorkshopProfileTransfer.Decode(Rehash(encoded, p => p["Extensions"]!["Owner"] = A.ToString("X16")), profile.Extensions, true), "checksummed profile still validates extension owner");
        reject(() => WorkshopProfileTransfer.Decode(Rehash(encoded, p => p["Takeover"]!["Character"] = A.ToString("X16")), profile.Extensions, true), "checksummed portable profile still validates portable takeover owner");
        reject(() => WorkshopProfileTransfer.Decode(Rehash(encoded, p => p["Name"] = ""), profile.Extensions, true), "checksummed profile still validates names");
        var native = Fresh();
        var legacy = WorkshopProfileTransfer.Decode(SuperHotbarStore.Encode(native, A), profile.Extensions, false).Profile!;
        check(legacy.Takeover!.Character == "0000000000000001" && legacy.Takeover.MacroBackupCharacter == A.ToString("X16")
            && Snapshot(legacy.Extensions) == Snapshot(profile.Extensions) && !legacy.ShowCommandMessages,
            "legacy takeover-only clipboard imports portably and retains current extensions/preferences");
        var combined = WorkshopProfileTransfer.Decode(SuperHotbarClipboardStore.Encode(native, A, profile.Extensions, false), new() { Owner = "0000000000000000", Bars = [] }, true).Profile!;
        check(combined.Extensions.Bars.Count == 1 && !combined.ShowCommandMessages, "legacy combined clipboard automatically retains its own extensions and message preference");
        var extensions = HotbarTransfer.Export(profile.Extensions, null, false, "2.3.0");
        var extra = WorkshopProfileTransfer.Decode(extensions, profile.Extensions, true);
        check(extra.Profile is null && extra.Extensions!.Bars.Count == 1, "legacy extension-only clipboard automatically enters the extension import path");
        var imported = extra.Extensions!; imported.ShowAllBars = false;
        var takeoverBefore = Snapshot(profile.Takeover);
        var oldId = profile.Extensions.Bars[0].Id;
        check(WorkshopProfiles.ImportExtensions(profile, imported, false, false) == 1
            && profile.Extensions.Bars.Count == 2 && profile.Extensions.Bars[0].Id == oldId && profile.Extensions.Show && profile.ShowCommandMessages,
            "default extension import appends, preserves existing bar identities and ignores global settings");
        check(profile.Extensions.Bars[1].Id != oldId && profile.Extensions.Bars[1].Id != imported.Bars[0].Id,
            "appended extensions receive independent identities");
        check(WorkshopProfiles.ImportExtensions(profile, imported, true, true) == 0
            && profile.Extensions.Bars.Count == 1 && !profile.Extensions.Show && !profile.ShowCommandMessages && Snapshot(profile.Takeover) == takeoverBefore,
            "explicit replacement/import globals changes only selected profile extensions and preferences");
        profile.Extensions.Bars = Enumerable.Range(0, 12).Select(_ => new DragHotbar()).ToList();
        var full = Snapshot(profile);
        reject(() => WorkshopProfiles.ImportExtensions(profile, imported, false, true), "extension append respects the twelve-bar limit");
        check(Snapshot(profile) == full && Snapshot(imported) == Snapshot(extra.Extensions), "failed extension append preserves the complete target and source document");
        check(Snapshot(decoded.Profile) == before, "all import preparation leaves original complete profile unchanged");
    }

    private static void Storage(string directory, WorkshopLibrary library, Action<bool, string> check, Action<Action, string> reject)
    {
        var path = WorkshopLibraryStore.PathFor(directory);
        WorkshopLibraryStore.Save(directory, library, false);
        var good = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var candidate = WorkshopProfiles.Copy(library); candidate.Current.Name = "不能写入";
            try { WorkshopLibraryStore.Save(directory, candidate, false); throw new Exception("Locked save unexpectedly succeeded"); }
            catch (IOException) { check(true, "locked library rejects an incomplete replacement"); }
        }
        check(File.ReadAllBytes(path).SequenceEqual(good) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup)
            && !Directory.EnumerateFiles(directory, "*.tmp-*").Any(), "atomic failure preserves main/backup bytes and removes temporary output");
        var envelope = JsonNode.Parse(File.ReadAllText(path))!; envelope["Payload"] = envelope["Payload"]!.GetValue<string>() + " ";
        reject(() => WorkshopLibraryStore.Decode(envelope.ToJsonString()), "checksum rejects changed payload bytes");
        File.WriteAllText(path, "broken");
        var recovered = WorkshopLibraryStore.Load(directory, B, true, out var fallback, out var migrated);
        check(fallback && !migrated && Snapshot(recovered) == Snapshot(WorkshopLibraryStore.Decode(Encoding.UTF8.GetString(backup)))
            && File.ReadAllText(path) == "broken" && Directory.EnumerateFiles(directory, "*.corrupt-*").Count() == 1,
            "corrupt library recovers exact backup and preserves corrupt source for inspection");
        WorkshopLibraryStore.Save(directory, recovered, true);
        check(File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "recovery save never replaces the good backup with corrupt main bytes");
        var future = Rehash(File.ReadAllText(path), p => p["Version"] = 99); File.WriteAllText(path, future);
        try { WorkshopLibraryStore.Load(directory, A, true, out _, out _); throw new Exception("Future version accepted"); }
        catch (NotSupportedException) { check(File.ReadAllText(path) == future, "future library version refuses backup rollback and overwrite"); }
        File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "broken backup");
        reject(() => WorkshopLibraryStore.Load(directory, A, true, out _, out _), "unreadable main and backup pause library initialization");
        check(File.ReadAllText(path) == "broken" && File.ReadAllText(path + ".bak") == "broken backup", "failed recovery preserves both unreadable originals");
    }

    private static void Migration(string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        var directory = Path.Combine(temp, "workshop-migration"); Directory.CreateDirectory(directory);
        var paths = new[] { A, B }.Select(id => Path.Combine(directory, $"SuperHotbar-{id:X16}.json")).ToArray();
        SuperHotbarStore.Save(paths[0], Fresh(A), A); SuperHotbarStore.Save(paths[1], Fresh(B), B);
        File.Move(paths[1], paths[1] + ".bak");
        var extensions = new HotbarProfile { Owner = "0000000000000000" }; extensions.Bars[0].Name = "旧扩展栏";
        HotbarProfileStore.Save(HotbarSharedProfiles.PathFor(directory), extensions, 0, false);
        var originals = Directory.EnumerateFiles(directory).ToDictionary(p => p, File.ReadAllBytes);
        var library = WorkshopLibraryStore.Load(directory, A, false, out var recovered, out var migrated);
        check(migrated && !recovered && library.Profiles.Count == 2 && library.Current.Takeover!.MacroBackupCharacter == A.ToString("X16"),
            "first migration selects current-character archive and migrates another character's backup-only archive");
        check(library.Profiles.All(p => p.Extensions.Bars[0].Name == "旧扩展栏" && !p.ShowCommandMessages)
            && originals.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "migration copies shared extensions/messages into every profile and leaves all legacy file bytes intact");
        check(WorkshopLibraryStore.Load(directory, B, true, out _, out var again).SelectedId == library.SelectedId && !again,
            "later character login uses persisted selection without repeating migration or native capture");
        var broken = Path.Combine(temp, "workshop-broken-migration"); Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, $"SuperHotbar-{A:X16}.json"), "corrupt");
        reject(() => WorkshopLibraryStore.Load(broken, A, true, out _, out _), "unreadable legacy takeover prevents silent empty migration");
        check(!File.Exists(WorkshopLibraryStore.PathFor(broken)), "failed migration does not create an empty replacement library");
        var empty = WorkshopLibraryStore.Load(Path.Combine(temp, "workshop-empty"), A, true, out _, out _);
        check(empty.Profiles.Count == 1 && empty.Current.Takeover is null, "first-time user receives one default configuration without takeover");
    }

    internal static void Compiled(string root, Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        const string ns = "OmniToolbox.NativeDragHotbarLocal.";
        var type = assembly.GetType(ns + "NativeDragHotbar", true)!;
        string[] Calls(string name) => ItemAndFontChecks.Calls(type.GetMethod(name, flags)!).ToArray();
        var execute = Calls("Execute");
        check(execute.Contains("Execute") && !execute.Contains("ExecuteMacro") && !execute.Contains("get_Macros") && !execute.Contains("SavedMacro"),
            "compiled common click/key execution dispatches live native entry without consulting macro snapshot");
        check(Calls("PollSuperKeys").Contains("ExecuteSuperPosition") && Calls("ExecuteSuperPosition").Contains("Execute")
            && Calls("DrawSlot").Contains("Execute"),
            "mouse and keyboard both reach the same live macro execution path");
        var bridge = assembly.GetType(ns + "NativeHotbarBridge", true)!;
        check(!bridge.GetFields(flags).Any(f => f.Name == "macroCopies") && bridge.GetMethod("ExecuteMacro", flags) is null,
            "compiled bridge allocates no snapshot macro copies for execution");
        check(Calls("OnSuperNativeDraw").Contains("PreventOriginal") && Calls("OnSuperNativeDraw").Contains("HudEditing")
            && Calls("OnSuperNativeDraw").Contains("get_SuperActive") && Calls("RestoreSuperVisibility").Contains("ReleaseAll"),
            "native hiding respects takeover/HUD state and releases transient mouse interception without changing visibility");
        check(!File.ReadAllText(Path.Combine(root, "Modules/NativeDragHotbarSuper.cs")).Contains("ToggleVisibility")
            && type.GetField("superHidden", flags) is null, "takeover never changes native root visibility or caches replaceable node pointers");
        foreach (var flow in new[] { "ExitSuperTakeover", "PauseSuper", "StopSuper", "ResetWorkshopRuntime", "UpdateSuper" })
            check(Calls(flow).Contains("RestoreSuperVisibility"), "native suppression releases on lifecycle path: " + flow);
        var module = Activator.CreateInstance(type)!;
        void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(module, value);
        object? Get(string name) => type.GetField(name, flags)!.GetValue(module);
        object? Call(string name) => type.GetMethod(name, flags)!.Invoke(module, null);
        Set("superNativeDrawSuppressed", true); Set("restoreConfirm", true); Set("workshopMacroConfirm", true);
        Set("superReady", true); Set("superFault", true); Call("ResetWorkshopRuntime");
        check(!(bool)Get("superNativeDrawSuppressed")! && !(bool)Get("restoreConfirm")! && !(bool)Get("workshopMacroConfirm")!
            && !(bool)Get("superReady")! && !(bool)Get("superFault")!, "actual runtime reset releases suppression and stale macro/restore confirmation");
        var library = new WorkshopLibrary(); WorkshopProfiles.Add(library, Profile(), "跨角色配置");
        var compiledLibrary = JsonSerializer.Deserialize(Snapshot(library), assembly.GetType(ns + "WorkshopLibrary", true)!)!;
        Set("workshopLibrary", compiledLibrary); Set("owner", B); Call("BindWorkshopCharacter");
        var bound = Get("super")!;
        check((string)bound.GetType().GetProperty("Character")!.GetValue(bound)! == B.ToString("X16")
            && (string)bound.GetType().GetProperty("MacroBackupCharacter")!.GetValue(bound)! == A.ToString("X16")
            && (ulong)Get("superOwner")! == B, "actual runtime binds selected saved profile to newly logged-in character and retains backup origin");
        Set("owner", 0UL); Call("BindWorkshopCharacter");
        check(Get("super") is null && (ulong)Get("superOwner")! == 0 && !(bool)Get("superReady")!, "actual logout drops native runtime but retains shared selected library");
        var commit = Calls("CommitWorkshopEdit");
        check(Array.IndexOf(commit, "Save") >= 0 && Array.IndexOf(commit, "Save") < Array.IndexOf(commit, "ResetWorkshopRuntime"),
            "compiled profile switch persists validated candidate before resetting live runtime");
        check(Calls("DrawSuperResetSettings").Contains("MarkWorkshopOperation") && Calls("ClearSuperData").Contains("CommitWorkshopEdit")
            && !Calls("ClearSuperData").Contains("Clear"), "clear/reset targets selected-profile candidate and never invokes legacy directory-wide deletion");
    }
}
