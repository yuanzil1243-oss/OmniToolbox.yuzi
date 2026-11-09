using System.Reflection;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniToolbox.NativeDragHotbarLocal;

internal static class GameSettingsChecks
{
    internal static WorkshopGameTemplate Fresh() => new()
    {
        Source = "1122334455667788", SourceName = "示例角色", CapturedAt = "2026-11-09T06:00:00Z",
        StructsId = "00000000-0000-0000-0000-000000000001", KeySize = 11, KeyCount = 512,
        Windows = [new("_ParameterWidget", 0, WorkshopGameRules.NameHash("_ParameterWidget"), 20, 50, 1, 0, 160, 30, 1, 1, 0, 255),
            new("Inventory", -1, WorkshopGameRules.NameHash("Inventory"), 30, 70, 1, 0, 500, 300, 1, 1, 0, 255)],
        Options = [new("Ui", "BattleEffectSelf", "UInt", 1), new("Control", "KeyboardSpeed", "UInt", 1)],
        Keys = [new(1, Convert.ToBase64String(new byte[11]))],
    };
    internal static void Run(string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        var template = Fresh(); WorkshopGameRules.Validate(template);
        var original = WorkshopGameRules.Fingerprint(template);
        var copy = WorkshopProfiles.Copy(template);
        check(WorkshopGameRules.Compare(copy, template).Count == 0 && WorkshopGameRules.Fingerprint(copy) == original,
            "structured game template round-trips and equal settings produce zero writes");
        copy.Source = "8877665544332211"; copy.SourceName = "另一个角色"; copy.CapturedAt = DateTimeOffset.UtcNow.ToString("O"); copy.Id = Guid.NewGuid().ToString("N");
        check(WorkshopGameRules.Fingerprint(copy) == original, "preview fingerprint ignores metadata and can compare different characters");
        copy.Windows[0] = copy.Windows[0] with { X = 45 }; copy.Options[0] = copy.Options[0] with { UInt = 2 };
        copy.Keys[0] = copy.Keys[0] with { Data = Convert.ToBase64String(Enumerable.Repeat((byte)1, 11).ToArray()) }; copy.CurrentLayout = 2;
        check(WorkshopGameRules.Compare(template, copy).Count == 4, "preview reports independent HUD position, chosen layout, option and non-hotbar key changes");
        foreach (var name in new[] { "DktSessionId", "LastLogin0", "LastLogin1", "WorldId", "MainAdapter", "ScreenShotDir", "PadGuid",
            "InstanceGuid", "ActiveProductGuid", "MsqProgress", "ScreenMode", "ScreenWidth", "ScreenHeight", "ConfigVersion", "ServiceIndex", "Port" })
            check(!WorkshopGameRules.OptionAllowed("System", name), "system template excludes identity/device/session or display-mode option " + name);
        foreach (var name in new[] { "SoundMaster", "SoundSe", "IsSndBgm", "AccessibilityColorBlindFilterEnable", "ShadowVisibilityTypeParty_DX11", "TextureFilterQuality_DX11", "Gamma" })
            check(WorkshopGameRules.OptionAllowed("System", name), "optional system scope includes supported preference " + name);
        foreach (var (section, name) in new[] { ("Ui", "HotbarLock"), ("Ui", "HotbarCommon01"), ("Ui", "ActiveLS_H"), ("Control", "ExHotbarSetting"), ("Control", "HotbarDispSetNum"), ("Ui", "PetMirageTypeFairy") })
            check(!WorkshopGameRules.OptionAllowed(section, name), "character template preserves native bar/account-specific option " + name);
        check(!WorkshopGameRules.WindowAllowed("_ActionBar") && !WorkshopGameRules.WindowAllowed("_ActionBar09")
            && WorkshopGameRules.WindowAllowed("_TargetInfo") && !WorkshopGameRules.WindowAllowed("../../evil"),
            "layout scope excludes original native action bars and rejects path-like or executable window names");
        check(WorkshopGameRules.KeyAllowed(44) && !WorkshopGameRules.KeyAllowed(45) && !WorkshopGameRules.KeyAllowed(190)
            && WorkshopGameRules.KeyAllowed(191) && !WorkshopGameRules.KeyAllowed(447), "other-key scope cannot modify the workshop/native-hotbar binding range");
        void Bad(Action<WorkshopGameTemplate> edit, string name)
        { var bad = WorkshopProfiles.Copy(template); edit(bad); reject(() => WorkshopGameRules.Validate(bad), name); }
        Bad(t => t.Windows.Add(t.Windows[0]), "duplicate structured HUD identity is rejected");
        Bad(t => t.Windows[0] = t.Windows[0] with { Hash = 1 }, "forged native addon hash is rejected before native calls");
        Bad(t => t.Windows[0] = t.Windows[0] with { X = float.NaN }, "nonfinite native geometry is rejected");
        Bad(t => t.Windows[0] = t.Windows[0] with { Scale = 100 }, "unbounded native scale is rejected");
        Bad(t => t.Windows[0] = t.Windows[0] with { Layout = 4 }, "fifth HUD layout cannot be imported");
        Bad(t => t.Options.Add(new("System", "SoundMaster", "UInt", 10)), "system option requires an explicit system scope");
        Bad(t => t.Options.Add(new("Ui", "LogTabName2", "String")), "string configuration values never enter portable game templates");
        Bad(t => t.Keys[0] = new(57, t.Keys[0].Data), "template cannot smuggle a hotbar key into other bindings");
        Bad(t => t.Keys[0] = new(1, "bad-base64"), "malformed native key encoding is rejected");
        Bad(t => t.Keys[0] = new(1, Convert.ToBase64String(new byte[10])), "short native key buffers are rejected");
        Bad(t => t.Scope = new(false, true, true), "layout data cannot bypass a disabled layout scope");
        Bad(t => t.Source = "0000000000000000", "zero game template source identity is rejected");
        var missing = WorkshopProfiles.Copy(template); missing.Options.RemoveAt(0);
        reject(() => WorkshopGameRules.Compare(missing, template), "preview refuses a target option unavailable on this client");
        var localBefore = WorkshopProfiles.Copy(template); localBefore.LocalHudSnapshot = [];
        var localAfter = WorkshopProfiles.Copy(localBefore);
        localAfter.LocalHudSnapshot!.Add(template.Windows[0] with { Name = "" });
        check(WorkshopGameRules.Compare(localAfter, localBefore).Any(r => r.After.Contains("默认／空布局")),
            "local restore preview detects that a previously empty/default HUD must be cleared rather than overlaid");
        reject(() => WorkshopProfiles.Validate(new WorkshopProfile { GameSettings = localBefore }),
            "complete same-character HUD backup can never enter a portable cross-character profile");
        var emptyBackup = WorkshopGameBackupStore.Save(temp, localBefore);
        check(WorkshopGameBackupStore.Load(emptyBackup, localBefore.Source).LocalHudSnapshot is { Count: 0 },
            "local durable backup distinguishes an exact empty HUD from a portable template without an exact snapshot");
        var library = new WorkshopLibrary(); var added = WorkshopProfiles.Add(library, new() { GameSettings = template }, "模板测试");
        added.AutoApplyGameSettings = true; added.AppliedGameSettings[template.Source] = template.Id;
        const ulong sourceActor = 0x1122334455667788, otherActor = 0x8877665544332211;
        check(!WorkshopGameRules.NeedsAutomaticApply(added, sourceActor) && !WorkshopGameRules.NeedsAutomaticApply(added, 0)
            && WorkshopGameRules.NeedsAutomaticApply(added, otherActor), "automatic template policy skips the source actor/logout and selects a different character");
        added.AppliedGameSettings[otherActor.ToString("X16")] = template.Id;
        check(!WorkshopGameRules.NeedsAutomaticApply(added, otherActor), "completed marker preserves subsequent manual edits on the same character");
        var newTemplate = WorkshopProfiles.Copy(template); newTemplate.Id = Guid.NewGuid().ToString("N"); added.GameSettings = newTemplate;
        check(WorkshopGameRules.NeedsAutomaticApply(added, otherActor), "recapturing with a new template identity schedules other characters for one fresh application");
        added.AutoApplyGameSettings = false;
        check(!WorkshopGameRules.NeedsAutomaticApply(added, otherActor), "disabled opt-in prevents automatic game writes even when a template revision is new");
        added.AutoApplyGameSettings = true; added.GameSettings = template; added.AppliedGameSettings.Remove(otherActor.ToString("X16"));
        var libraryText = WorkshopLibraryStore.Encode(library); var restored = WorkshopLibraryStore.Decode(libraryText);
        check(restored.Current.AutoApplyGameSettings && restored.Current.AppliedGameSettings.Count == 1
            && WorkshopGameRules.Fingerprint(restored.Current.GameSettings!) == original, "local library preserves per-profile template, opt-in and completed-character marker");
        var exported = WorkshopProfileTransfer.Encode(added); var imported = WorkshopProfileTransfer.Decode(exported, new(), true).Profile!;
        check(!imported.AutoApplyGameSettings && imported.AppliedGameSettings.Count == 0 && imported.GameSettings is not null
            && added.AutoApplyGameSettings && added.AppliedGameSettings.Count == 1,
            "portable export/import preserves template but strips automatic native-write authorization and local completion markers");
        var clone = WorkshopProfiles.Add(library, added, "模板副本");
        check(!clone.AutoApplyGameSettings && clone.AppliedGameSettings.Count == 0 && clone.GameSettings is not null,
            "copying a configuration retains template with automatic application initially off");
        var backup = WorkshopGameBackupStore.Save(temp, template);
        check(WorkshopGameRules.Fingerprint(WorkshopGameBackupStore.Load(backup, template.Source)) == original
            && !Directory.EnumerateFiles(Path.GetDirectoryName(backup)!, "*.tmp").Any(),
            "application-before backup durably writes, verifies exact structured values and leaves no temporary file");
        reject(() => WorkshopGameBackupStore.Load(backup, "8877665544332211"), "local restore backup cannot be loaded as another character's previous settings");
        var envelope = JsonNode.Parse(File.ReadAllText(backup))!; envelope["Payload"] = envelope["Payload"]!.GetValue<string>() + " ";
        File.WriteAllText(backup, envelope.ToJsonString());
        reject(() => WorkshopGameBackupStore.Load(backup, template.Source), "application-before backup rejects corrupt payload bytes");
    }
    internal static unsafe void Compiled(Assembly assembly, Assembly structs, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var native = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.WorkshopGameNative", true)!;
        var module = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar", true)!;
        string[] Calls(Type t, string name) => ItemAndFontChecks.Calls(t.GetMethod(name, flags)!).ToArray();
        check(Calls(native, "Capture").Contains("Validate") && !Calls(native, "Capture").Any(n => n is "Set" or "ReadFile" or "WriteFile" or "SaveFile"),
            "compiled template capture is read-only and never serializes an opaque native file blob");
        var apply = Calls(module, "ApplyWorkshopGameTemplate");
        check(Array.IndexOf(apply, "Save") >= 0 && Array.IndexOf(apply, "Save") < Array.IndexOf(apply, "Apply"),
            "compiled apply writes the durable current-character backup before any game-settings mutation");
        check(Calls(native, "ApplyCore").Contains("GetConfigEntryByAddonName") && Calls(native, "ApplyCore").Contains("ApplyHudLayout")
            && Calls(native, "ApplyCore").Contains("SaveFile") && !Calls(native, "ApplyCore").Contains("ReadFile"),
            "compiled native apply uses typed entries/list-aware accessor, refreshes HUD and requests game saves without unsafe native blob import");
        check(Calls(native, "Apply").Count(n => n == "ApplyCore") == 2 && Calls(native, "Apply").Contains("Compare"),
            "compiled game apply reads values back and attempts previous-settings recovery if it fails");
        check(Calls(module, "DrawWorkshopArchiveSettings").Contains("DrawWorkshopGameSettings")
            && Calls(module, "UpdateSuper").Contains("UpdateWorkshopGameTemplate")
            && Calls(module, "UpdateWorkshopGameTemplate").Contains("ApplyWorkshopGameTemplate"),
            "template capture/manual preview and opt-in cross-character application are wired into actual archive UI and framework updates");
        var setType = structs.GetType("FFXIVClientStructs.FFXIV.Client.UI.Misc.AddonConfigDataSet", true)!;
        var entryType = structs.GetType("FFXIVClientStructs.FFXIV.Client.UI.Misc.AddonConfigEntry", true)!;
        var memory = Marshal.AllocHGlobal(setType.StructLayoutAttribute!.Size);
        try
        {
            var sourceBytes = new byte[setType.StructLayoutAttribute.Size]; Marshal.Copy(sourceBytes, 0, memory, sourceBytes.Length);
            var offset = setType.GetField("_hudLayoutConfigEntries", flags)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            var entrySize = entryType.StructLayoutAttribute!.Size;
            void Entry(int index, uint hash)
            { Marshal.WriteInt32(memory, offset + index * entrySize, unchecked((int)hash)); Marshal.WriteByte(memory, offset + index * entrySize + 32, 1); }
            var barHash = WorkshopGameRules.NameHash("_ActionBar"); var widgetHash = WorkshopGameRules.NameHash("_ParameterWidget");
            Entry(0, barHash); Entry(1, widgetHash); Entry(2, 0xAAFF1234);
            var method = native.GetMethod("HudPlan", flags)!;
            var compiled = JsonSerializer.Deserialize(JsonSerializer.Serialize(Fresh(), WorkshopProfiles.Json), method.GetParameters()[0].ParameterType)!;
            object[] Parameters() => [compiled, Pointer.Box((void*)memory, method.GetParameters()[1].ParameterType)];
            var plan = (Array)method.Invoke(null, Parameters())!;
            var rows = ((IEnumerable)plan.GetValue(0)!).Cast<object>().ToArray();
            uint Hash(object entry) => (uint)entryType.GetField("AddonNameHash")!.GetValue(entry)!;
            check(rows.Length == 3 && Hash(rows[0]) == widgetHash && Hash(rows[1]) == barHash && Hash(rows[2]) == 0xAAFF1234,
                "actual compiled HUD merge overlays saved non-hotbar entries while preserving native hotbars and unknown target HUD entries");
            check((float)entryType.GetField("X")!.GetValue(rows[0])! == 20f && (bool)entryType.GetField("HasValue")!.GetValue(rows[0])!,
                "actual HUD planning converts structured fields to pointer-free native entry values and a compact HasValue prefix");
            var after = new byte[sourceBytes.Length]; Marshal.Copy(memory, after, 0, after.Length);
            check(BitConverter.ToUInt32(after, offset) == barHash && BitConverter.ToUInt32(after, offset + entrySize) == widgetHash,
                "native HUD preflight planning leaves the target layout memory unchanged");
            var exactBefore = Fresh(); exactBefore.LocalHudSnapshot = [];
            var exactCompiled = JsonSerializer.Deserialize(JsonSerializer.Serialize(exactBefore, WorkshopProfiles.Json), method.GetParameters()[0].ParameterType)!;
            var exactPlan = (Array)method.Invoke(null, [exactCompiled, Pointer.Box((void*)memory, method.GetParameters()[1].ParameterType)])!;
            check(exactPlan.Cast<object>().All(v => !((IEnumerable)v).Cast<object>().Any()),
                "actual compiled local-backup restore plan restores all four default/empty HUDs without retaining imported entries");
            for (var i = 0; i < 112; i++) Entry(i, (uint)(i + 100));
            try { method.Invoke(null, Parameters()); throw new Exception("oversized HUD merge accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            { check(Marshal.ReadInt32(memory, offset) == 100, "HUD-capacity overflow is rejected before any target entry is replaced"); }
        }
        finally { Marshal.FreeHGlobal(memory); }
        CaptureRegression(native, setType, entryType, check);
    }

    private static unsafe void CaptureRegression(Type native, Type setType, Type entryType, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var method = native.GetMethod("CaptureWindows", flags)!;
        var templateType = method.GetParameters()[0].ParameterType;
        var memory = Marshal.AllocHGlobal(setType.StructLayoutAttribute!.Size);
        var nodes = Marshal.AllocHGlobal(24 * 3);
        try
        {
            new Span<byte>((void*)memory, setType.StructLayoutAttribute.Size).Clear();
            new Span<byte>((void*)nodes, 72).Clear();
            var ordinary = setType.GetField("_configEntries", flags)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            var hud = setType.GetField("_hudLayoutConfigEntries", flags)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            var used = setType.GetField("UsedAddonConfigEntries", flags)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            var size = entryType.StructLayoutAttribute!.Size;
            var inventory = WorkshopGameRules.NameHash("Inventory"); var emote = WorkshopGameRules.NameHash("Emote");
            var widget = WorkshopGameRules.NameHash("_ParameterWidget");
            void Entry(int offset, int index, uint hash, float x = 20, float scale = 1)
            {
                var bytes = new Span<byte>((void*)(memory + offset + index * size), size); bytes.Clear();
                BitConverter.TryWriteBytes(bytes, hash); BitConverter.TryWriteBytes(bytes[4..], x);
                BitConverter.TryWriteBytes(bytes[12..], scale); bytes[32] = 1;
            }
            void List(int first = 0, int second = 1)
            {
                Marshal.WriteIntPtr(memory, used, nodes); Marshal.WriteInt64(memory, used + 8, 2);
                Marshal.WriteIntPtr(nodes, 0, nodes + 24); Marshal.WriteIntPtr(nodes, 8, nodes + 48);
                Marshal.WriteIntPtr(nodes, 24, nodes + 48); Marshal.WriteIntPtr(nodes, 32, nodes);
                Marshal.WriteIntPtr(nodes, 40, memory + ordinary + first * size);
                Marshal.WriteIntPtr(nodes, 48, nodes); Marshal.WriteIntPtr(nodes, 56, nodes + 24);
                Marshal.WriteIntPtr(nodes, 64, memory + ordinary + second * size);
            }
            Entry(ordinary, 0, inventory); Entry(ordinary, 1, emote);
            Entry(ordinary, 20, inventory, float.NaN); // stale recycled cell; HasValue can still be set
            Entry(hud, 0, widget); Entry(hud, 2, widget, float.NaN); // terminator at slot 1
            Entry(hud, 112, widget, 30); Entry(hud, 114, widget, 999_999); // a second valid HUD prefix
            List();
            var names = new Dictionary<uint, string> { [inventory] = "Inventory", [emote] = "Emote", [widget] = "_ParameterWidget" };
            var hudNames = new HashSet<string> { "_ParameterWidget" };
            WorkshopGameTemplate Capture(bool backup)
            {
                var draft = JsonSerializer.Deserialize(JsonSerializer.Serialize(Fresh(), WorkshopProfiles.Json), templateType)!;
                templateType.GetProperty("Windows")!.SetValue(draft, Activator.CreateInstance(templateType.GetProperty("Windows")!.PropertyType));
                method.Invoke(null, [draft, Pointer.Box((void*)memory, method.GetParameters()[1].ParameterType), names, hudNames, backup]);
                return JsonSerializer.Deserialize<WorkshopGameTemplate>(JsonSerializer.Serialize(draft, templateType), WorkshopProfiles.Json)!;
            }
            var saved = Capture(true); WorkshopGameRules.Validate(saved);
            check(saved.Windows.Count == 4 && saved.Windows.Count(w => w.Layout < 0) == 2
                && saved.LocalHudSnapshot is { Count: 2 },
                "actual native capture reads only used ordinary entries and each terminated HUD prefix, ignoring stale duplicate/NaN storage");
            check(saved.Windows.Select(w => (w.Layout, w.Name)).SequenceEqual(new[] { (-1, "Emote"), (-1, "Inventory"), (0, "_ParameterWidget"), (1, "_ParameterWidget") }),
                "actual native capture permits identical HUD names in different layouts and uses canonical ordering");
            List(1, 0); var reordered = Capture(true);
            check(WorkshopGameRules.Fingerprint(saved) == WorkshopGameRules.Fingerprint(reordered),
                "used-list reorder cannot invalidate an unchanged settings preview or readback fingerprint");
            check(Capture(false).LocalHudSnapshot is null, "portable capture does not accidentally retain character-local full HUD snapshot");
            // Ensure the merge also ignores an old entry beyond the first terminator.
            var planMethod = native.GetMethod("HudPlan", flags)!;
            var portable = Capture(false);
            var compiled = JsonSerializer.Deserialize(JsonSerializer.Serialize(portable, WorkshopProfiles.Json), templateType)!;
            var plan = (Array)planMethod.Invoke(null, [compiled, Pointer.Box((void*)memory, planMethod.GetParameters()[1].ParameterType)])!;
            check(((IEnumerable)plan.GetValue(0)!).Cast<object>().Count() == 1,
                "actual HUD merge never resurrects stale cells past the native terminator");
            void Rejected(string label)
            {
                try { Capture(true); throw new Exception("Invalid used-list entry accepted: " + label); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { check(true, label); }
            }
            Marshal.WriteIntPtr(nodes, 64, memory - 8); Rejected("out-of-storage native entry pointer is rejected before dereference");
            Marshal.WriteIntPtr(nodes, 64, memory + ordinary + 1); Rejected("misaligned native entry pointer is rejected before dereference");
            Marshal.WriteIntPtr(nodes, 64, memory + ordinary + size); Rejected("repeated native used-list pointer is rejected instead of duplicating a window");
            List(); Marshal.WriteInt64(memory, used + 8, 1104); Rejected("oversized used-list count is rejected before enumeration");
            Marshal.WriteInt64(memory, used + 8, -1); Rejected("negative used-list count is rejected before enumeration");
            Marshal.WriteInt64(memory, used + 8, 0); Marshal.WriteIntPtr(memory, used, 0);
            check(Capture(true).Windows.Count == 2, "empty native used-list does not dereference a null sentinel and still captures valid HUDs");
        }
        finally { Marshal.FreeHGlobal(nodes); Marshal.FreeHGlobal(memory); }
    }
}
