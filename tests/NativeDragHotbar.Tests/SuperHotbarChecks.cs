using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class SuperHotbarChecks
{
    public static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        const ulong owner = 0x1122334455667788;
        SuperHotbarDocument Fresh() => new()
        {
            Character = owner.ToString("X16"), CapturedAt = "2026-11-08T00:00:00Z", NativeGroupCount = 6,
            NativeKeybindSize = 16, NativeStructsId = "00000000-0000-0000-0000-000000000001",
            Appearance = Enumerable.Range(0, 10).Select(i => new DragHotbar { Name = "栏 " + i }).ToList(),
            Groups = Enumerable.Range(0, 6).Select(g => new SuperSavedGroup { Index = g,
                Bars = Enumerable.Range(0, 18).Select(b => Enumerable.Range(0, 16).Select(s => new SuperSavedSlot()).ToList()).ToList() }).ToList(),
            Bindings = Enumerable.Range(45, 146).Append(447).Select(id => new SuperBinding
                { InputId = id, NativeData = Convert.ToBase64String(new byte[16]) }).ToList(),
            PvpGroups = new() { [0] = 4, [1] = 5 },
            Macros = [new() { Set = 0, Index = 0, Name = "宏备份", Icon = 123,
                Lines = Enumerable.Range(0, 15).Select(i => i == 0 ? "/echo 测试" : i == 1 ? "/wait 2" : "").ToList() }],
        };
        HotbarEntry Describe(SuperSavedSlot s) => s.Type == 0 ? new() : new() { NativeType = s.Type, NativeId = s.Id };
        var data = Fresh();
        check(data.AppearanceMappingVersion == 0 && data.AllowKeyRepeater, "legacy archive defaults retain explicit appearance repair marker and repeat opt-in");
        data.AppearanceMappingVersion = 1;
        var mappingRoundtrip = SuperHotbarStore.Decode(SuperHotbarStore.Encode(data, owner), owner);
        check(mappingRoundtrip.AppearanceMappingVersion == 1 && mappingRoundtrip.AllowKeyRepeater, "corrected appearance marker and repeat preference survive archive round-trip");
        var allBars = Fresh();
        for (var g = 0; g < 4; g++)
            for (var b = 0; b < 10; b++)
                for (var s = 0; s < 12; s++) allBars.Groups[g].Bars[b][s] = new() { Type = 1, Id = (uint)(1000 + g * 120 + b * 12 + s) };
        HotbarEntry FullDescribe(SuperSavedSlot s) => new() { NativeType = s.Type, NativeId = s.Id };
        for (var b = 0; b < 10; b++)
        {
            allBars.Shared[b] = b % 2 == 0;
            for (var g = 1; g <= 3; g++)
            {
                var resolved = SuperHotbarRules.Resolve(allBars, b, g, FullDescribe);
                check(resolved.Count == 12 && resolved.Select(s => s.NativeId).SequenceEqual(
                    Enumerable.Range(0, 12).Select(s => (uint)(1000 + (allBars.Shared[b] ? 0 : g) * 120 + b * 12 + s))),
                    $"all twelve slots bar {b + 1} group {g} retain shared/job-specific content identity");
            }
        }
        var allRoundtrip = SuperHotbarStore.Decode(SuperHotbarStore.Encode(allBars, owner), owner);
        check(allRoundtrip.EditedBars.Count == 20 && allRoundtrip.EditedBars.All(p => p.Value.Select(s => s.NativeId)
            .SequenceEqual(allBars.EditedBars[p.Key].Select(s => s.NativeId))), "all ordinary shared and job-specific contents survive independent archive backup");
        for (var bar = 0; bar < data.Appearance.Count; bar++) NativeHotbarLayout.ApplyNativeShape(data.Appearance[bar], bar % 6);
        SuperHotbarRules.Validate(data, owner);
        var layoutsRoundtrip = SuperHotbarStore.Decode(SuperHotbarStore.Encode(data, owner), owner);
        check(layoutsRoundtrip.Appearance.Select(b => (b.Columns, b.Rows)).SequenceEqual(data.Appearance.Select(b => (b.Columns, b.Rows)))
            && layoutsRoundtrip.Appearance.All(b => b.SlotCount == 12 && b.Slots.Count == 12),
            "all six native shapes survive full archive backup without a schema migration or content resize");
        check(data.Groups.Count == 6 && data.Groups.All(g => g.Bars.Count == 18 && g.Bars.All(b => b.Count == 16)),
            "super archive preserves all jobs, cross bars and reserved native slots");
        data.Groups[0].Bars[0][0] = new() { Type = 1, Id = 119 };
        data.Groups[1].Bars[0][0] = new() { Type = 1, Id = 120 };
        data.Groups[2].Bars[0][0] = new() { Type = 1, Id = 121 };
        var first = SuperHotbarRules.Resolve(data, 0, 1, Describe);
        var second = SuperHotbarRules.Resolve(data, 0, 2, Describe);
        check(first[0].NativeId == 120 && second[0].NativeId == 121 && !ReferenceEquals(first, second),
            "job-specific bars have independent content for each job");
        first[0].NativeId = 122;
        check(SuperHotbarRules.Resolve(data, 0, 1, Describe)[0].NativeId == 122 && second[0].NativeId == 121,
            "editing one job does not modify another job or reload stale native content");
        data.Shared[0] = true;
        var shared = SuperHotbarRules.Resolve(data, 0, 1, Describe);
        check(shared[0].NativeId == 119 && ReferenceEquals(shared, SuperHotbarRules.Resolve(data, 0, 2, Describe)),
            "shared bars resolve to one shared store across jobs");
        data.Shared[0] = false;
        check(SuperHotbarRules.Resolve(data, 0, 1, Describe)[0].NativeId == 122,
            "switching shared mode preserves the dormant job-specific contents");
        data.Shared[0] = true;
        check(SuperHotbarRules.GroupFor(data, 0, 5) == 4 && SuperHotbarRules.GroupFor(data, 0, 1) == 0,
            "PvP shared content is separate from PvE shared content");
        data.Groups[4].Bars[0][0] = new() { Type = 25, Id = 1 };
        check(SuperHotbarRules.Resolve(data, 0, 5, Describe)[0].NativeType == 25,
            "PvP shared bars use the captured native PvP index map");
        check(SuperHotbarRules.EffectiveSlot(data, 1, 0, 0).Id == 122 && data.Groups[1].Bars[0][0].Id == 120,
            "restore planning uses module edits without mutating the original imported archive");
        data.Groups[1].Bars[0][15] = new() { Type = 250, Id = uint.MaxValue };
        data.Groups[1].Bars[17][15] = new() { Type = 251, Id = 1234 };
        check(SuperHotbarRules.EffectiveSlot(data, 1, 0, 15).Type == 250
            && SuperHotbarRules.EffectiveSlot(data, 1, 17, 15).Id == 1234,
            "restoring ordinary edits does not erase reserved or cross-bar archive data");
        first[1] = new() { PreservedNativeType = 250, PreservedNativeId = 987, IconId = 234001 };
        check(!first[1].IsEmpty && !first[1].HasNative && first[1].Copy().PreservedNativeId == 987
            && SuperHotbarRules.EffectiveSlot(data, 1, 0, 1).Type == 250,
            "unrecognized native commands are preserved losslessly and never guessed executable");
        first[2] = new() { Mode = HotbarEntryMode.Command, Command = "/echo plugin command" };
        check(SuperHotbarRules.EffectiveSlot(data, 1, 0, 2).Type == 0,
            "plugin command text is not misrepresented as a restorable native action");
        var binding = data.Bindings.Single(b => b.InputId == 57);
        binding.Primary = new() { Key = 65, Modifiers = 2 }; binding.PrimaryEdited = true;
        binding.Secondary = new() { Key = 112, Modifiers = 5 };
        check(binding.Primary.ToString() == "Ctrl+A" && binding.Secondary.ToString() == "Alt+Shift+F1",
            "keyboard labels preserve exact Ctrl Alt Shift combinations and alternate bindings");
        check(binding.Primary.Matches(2, k => k == 65) && !binding.Primary.Matches(3, k => k == 65)
            && !new SuperShortcut().Matches(0, _ => true), "shortcut modifiers match exactly and an empty binding never fires");
        check(SuperShortcut.Valid(new() { Key = 65, Modifiers = 7 }) && SuperShortcut.Valid(new())
            && !SuperShortcut.Valid(new() { Key = 16 }) && !SuperShortcut.Valid(new() { Key = 255 })
            && !SuperShortcut.Valid(new() { Key = 65, Modifiers = 8 }), "modifier-only invalid and unsupported shortcut keys are rejected");
        data.Bindings.Single(b => b.InputId == 58).Secondary = new() { Key = 65, Modifiers = 2 };
        check(SuperHotbarRules.Conflict(data, 57, binding.Primary) == 58
            && SuperHotbarRules.Conflict(data, 57, new()) == -1,
            "primary and alternate shortcut conflicts are detected across positions");
        check(SuperHotbarRules.SlotBindings(data).Count() == 120
            && SuperHotbarRules.SlotBindings(data).Last().Bar == 9 && SuperHotbarRules.SlotBindings(data).Last().Slot == 11,
            "all 120 ordinary positions map to native input identities without including pet or duty keys");
        check(SuperHotbarRules.HotbarInput(45) && SuperHotbarRules.HotbarInput(190)
            && SuperHotbarRules.HotbarInput(447) && !SuperHotbarRules.HotbarInput(44), "hotbar restore input scope excludes movement and general UI keys");
        var encoded = SuperHotbarStore.Encode(data, owner);
        var decoded = SuperHotbarStore.Decode(encoded, owner);
        check(decoded.EditedBars["1:0"][0].NativeId == 122 && decoded.Bindings.Single(b => b.InputId == 57).PrimaryEdited
            && decoded.Macros[0].Lines[1] == "/wait 2" && decoded.Groups[1].Bars[17][15].Type == 251,
            "complete export round-trips job edits keybindings macro waits and unknown archived types");
        check(decoded.Character == owner.ToString("X16") && !decoded.Enabled,
            "character identity and takeover state survive backup independently of the game");
        var tampered = JsonSerializer.Deserialize<Dictionary<string, string>>(encoded)!;
        tampered["Payload"] += " ";
        reject(() => SuperHotbarStore.Decode(JsonSerializer.Serialize(tampered), owner),
            "tampered backup payload fails its SHA256 check");
        reject(() => SuperHotbarStore.Decode("{}", owner), "missing backup envelope cannot become an empty configuration");
        reject(() => SuperHotbarStore.Decode(new string('x', 32_000_001), owner), "oversized backup rejected before JSON parsing");
        try { SuperHotbarStore.Decode(encoded, owner + 1); throw new Exception("Cross-character backup accepted"); }
        catch (SuperHotbarIncompatibleException) { check(true, "cross-character restoration requires a deliberate migration instead of silently changing identity"); }
        var path = Path.Combine(directory, "super", "SuperHotbar.json");
        SuperHotbarStore.Save(path, data, owner);
        data.Enabled = true; first[0].NativeId = 123;
        SuperHotbarStore.Save(path, data, owner);
        var reloaded = SuperHotbarStore.Load(path, owner, out var recovered);
        check(!recovered && reloaded.Enabled && reloaded.EditedBars["1:0"][0].NativeId == 123,
            "independent configuration loads directly from disk without any original game settings");
        check(File.Exists(path + ".bak") && Directory.GetFiles(path + ".history").Length == 1,
            "atomic replacement keeps both a previous backup and historical snapshot");
        File.WriteAllText(path, "broken main");
        var recoveredData = SuperHotbarStore.Load(path, owner, out recovered);
        check(recovered && recoveredData.EditedBars["1:0"][0].NativeId == 122 && File.ReadAllText(path) == "broken main",
            "backup recovery does not overwrite a damaged main file while reading");
        var priorBackup = File.ReadAllText(path + ".bak");
        SuperHotbarStore.Save(path, recoveredData, owner, true);
        check(File.ReadAllText(path + ".bak") == priorBackup && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*").Length == 1,
            "saving a recovered configuration preserves the good backup and archives the damaged main");
        File.Delete(path);
        check(SuperHotbarStore.Load(path, owner, out recovered).Groups.Count == 6 && recovered,
            "a missing primary file can recover from the backup without importing empty native bars");
        string Rewrap(SuperHotbarDocument d)
        {
            var payload = JsonSerializer.Serialize(d);
            return JsonSerializer.Serialize(new { Format = "SuperHotbar.Backup", Payload = payload,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))) });
        }
        var future = Fresh(); future.SchemaVersion = 2; File.WriteAllText(path, Rewrap(future));
        try { SuperHotbarStore.Load(path, owner, out _); throw new Exception("Future archive fell back"); }
        catch (SuperHotbarIncompatibleException) { check(true, "future schema never silently rolls back to an older backup"); }
        var other = Fresh(); other.Character = (owner + 1).ToString("X16"); File.WriteAllText(path, Rewrap(other));
        try { SuperHotbarStore.Load(path, owner, out _); throw new Exception("Wrong identity fell back"); }
        catch (SuperHotbarIncompatibleException) { check(true, "another character's valid primary archive cannot roll back to this character's older backup"); }
        void Bad(Action<SuperHotbarDocument> mutate, string label)
        { var invalid = Fresh(); mutate(invalid); reject(() => SuperHotbarRules.Validate(invalid, owner), label); }
        Bad(d => d.Groups.RemoveAt(0), "missing job archive is rejected");
        Bad(d => d.Groups[1].Index = 0, "duplicate job groups are rejected");
        Bad(d => d.Groups[0].Bars.RemoveAt(0), "incomplete normal and cross archive is rejected");
        Bad(d => d.Groups[0].Bars[0].RemoveAt(0), "missing native reserved slots are rejected");
        Bad(d => d.Shared = new bool[10], "truncated sharing flags are rejected");
        Bad(d => d.Bindings.RemoveAll(b => b.InputId == 57), "incomplete ordinary binding table is rejected");
        Bad(d => d.Bindings[0].InputId = 1, "unrelated movement or UI input cannot be injected into a restore archive");
        Bad(d => d.Bindings[0].NativeData = "not base64", "invalid native keybind data is rejected");
        Bad(d => d.Bindings[0].NativeData = Convert.ToBase64String(new byte[15]), "wrong native keybind structure size is rejected");
        Bad(d => d.EditedBars["999:0"] = Enumerable.Range(0, 12).Select(_ => new HotbarEntry()).ToList(), "out-of-range edited job mapping is rejected");
        Bad(d => d.EditedBars["1:0"] = [new()], "incomplete edited job contents are rejected");
        Bad(d => d.Appearance[0].Columns = 13, "replacement ordinary bar must remain mapped to exactly twelve slots");
        Bad(d => d.NativeOptions["UnrelatedSetting"] = 1, "non-hotbar config cannot be injected into restored game settings");
        Bad(d => d.ControlOptions["Movement"] = 1, "non-hotbar control config is rejected");
        Bad(d => d.Macros[0].Lines = [], "macro backup requires all fifteen lines");
        Bad(d => d.Macros.Add(d.Macros[0]), "duplicate macro slots are rejected");
        Bad(d => d.PvpGroups[1] = 128, "invalid PvP group mapping is rejected");
        Bad(d => d.FirstPage = 10, "out-of-range primary hotbar page is rejected");
        Bad(d => d.NativeStructsId = "unknown", "restore archives require a native structure identity");
        var edges = new SuperKeyEdges();
        check(edges.Press(65, true, true) && !edges.Press(65, true, true), "one physical press triggers once without repeated frame execution");
        check(!edges.Press(65, false, true) && edges.Press(65, true, true), "release rearms a physical shortcut");
        edges.Press(65, false, false);
        check(!edges.Press(65, true, false) && !edges.Press(65, true, true), "keys pressed during chat recording or a blocked state cannot fire when that state ends");
        edges.BlockHeld(k => k == 66);
        check(!edges.Press(66, true, true), "role changes and foreground recovery do not synthesize an already held shortcut");
        edges.Press(66, false, true);
        check(edges.Press(66, true, true), "a newly pressed key works after a context change");
        check(HotbarRules.IsSupportedType(25) && HotbarRules.IsSupportedType(26) && HotbarRules.IsSupportedType(35),
            "additional stable native action types include PvP quick chat combo and quick panels");
    }
}
