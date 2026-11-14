using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;
using OmniToolbox.SuperHotbarInterop;

internal static class KeyboardAndVisualChecks
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        // Captured through Dalamud's current SE index table, without pressing any key.
        var keys = JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(root,
            "tests/NativeDragHotbar.Tests/Fixtures/windows-se-keys.json")))!;
        check(keys.Length == 240 && keys[192] == 136 && keys[222] == 140 && keys[223] == 141,
            "read-only client fixture identifies OEM_3 and quote as distinct SE indices");
        var pairs = keys.Select((native, windows) => (Windows: windows, Native: native))
            .Where(p => p.Windows >= 8 && p.Native != 0 && p.Windows is not (>= 160 and <= 165)).ToArray();
        var map = new SuperHotbarKeyMap(pairs);
        foreach (var (windows, native) in pairs)
            check(map.Native(windows) == native && map.Native(map.Windows(native)) == native,
                $"client key mapping round-trips Windows {windows} / SE {native}");
        check(map.Windows(0) == 0 && map.Native(0) == 0 && map.Windows(136) == 192 && map.Windows(140) == 222
            && map.Windows(141) == 223,
            "unbound, dot/backtick, and quote retain separate physical key identities");
        reject(() => map.Windows(255), "unknown SE keys cannot silently become another physical key");
        reject(() => map.Native(254), "unsupported Windows keys cannot be written back as guessed SE values");
        check(!map.TryNative(254, out _) && map.TryNative(192, out var se) && se == 136,
            "native-conflict lookup uses the same verified conversion without throwing for independent keys");

        foreach (var (mods, label) in new[] { (0, "1"), (1, "s1"), (2, "c1"), (4, "a1"),
            (3, "cs1"), (5, "as1"), (6, "ca1"), (7, "cas1") })
            check(new SuperShortcut { Key = 49, Modifiers = mods }.Compact() == label,
                $"compact modifier mask {mods} displays {label}");
        check(new SuperShortcut { Key = 192, Modifiers = 2 }.Compact() == "c·"
            && new SuperShortcut { Key = 192, Modifiers = 2 }.ToString() == "Ctrl+·"
            && new SuperShortcut { Key = 112, Modifiers = 1 }.Compact() == "sF1"
            && new SuperShortcut().Compact() == "", "compact slot labels preserve readable full editor names and empty bindings");

        const ulong owner = 0x1122334455667788;
        SuperHotbarDocument Fresh() => new()
        {
            Character = owner.ToString("X16"), NativeGroupCount = 1, NativeKeybindSize = 16,
            NativeStructsId = "00000000-0000-0000-0000-000000000001",
            Appearance = Enumerable.Range(0, 10).Select(_ => new DragHotbar()).ToList(),
            Groups = [new() { Index = 0, Bars = Enumerable.Range(0, 18).Select(_ =>
                Enumerable.Range(0, 16).Select(_ => new SuperSavedSlot()).ToList()).ToList() }],
            Bindings = Enumerable.Range(57, 120).Select(id => new SuperBinding { InputId = id,
                NativeData = Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()) }).ToList(),
        };
        var archive = Fresh();
        var binding = archive.Bindings[0];
        binding.Primary = new() { Key = 136, Modifiers = 2 };
        binding.Secondary = new() { Key = 140, Modifiers = 1 };
        var recorded = archive.Bindings[1];
        recorded.Primary = new() { Key = 192 }; recorded.PrimaryEdited = true;
        recorded.Secondary = new() { Key = 0 }; recorded.SecondaryEdited = true;
        archive.Appearance[1].Opacity = 0.4f; archive.Appearance[2].Opacity = 1;
        var raw = binding.NativeData;
        check(SuperHotbarRules.UpgradePresentation(archive, map)
            && binding.Primary.Key == 192 && binding.Secondary.Key == 222
            && binding.Primary.Modifiers == 2 && binding.Secondary.Modifiers == 1,
            "legacy imported primary and secondary keys migrate SE indices to Windows VK atomically");
        check(recorded.Primary.Key == 192 && recorded.Secondary.Key == 0 && recorded.PrimaryEdited
            && recorded.SecondaryEdited && binding.NativeData == raw,
            "migration preserves previously recorded or cleared keys and exact original native bytes");
        check(archive.Appearance[0].Opacity == 0 && archive.Appearance[1].Opacity == 0.4f
            && archive.Appearance[2].Opacity == 1 && archive.Appearance.Skip(3).All(b => b.Opacity == 0),
            "transparent takeover default upgrades old default only and preserves customized opacity");
        var encoded = SuperHotbarStore.Encode(archive, owner);
        check(!SuperHotbarRules.UpgradePresentation(archive, map) && SuperHotbarStore.Encode(archive, owner) == encoded,
            "presentation migration is idempotent and cannot double-convert punctuation");
        var roundtrip = SuperHotbarStore.Decode(encoded, owner);
        roundtrip.BuiltInRepeat = true; roundtrip.RepeatCps = 12.5f; roundtrip.Appearance[0].HideEmpty = false;
        roundtrip.Bindings[2].Primary = new() { Key = 5, Modifiers = 2 }; roundtrip.Bindings[2].PrimaryEdited = true;
        var featureRoundtrip = SuperHotbarStore.Decode(SuperHotbarStore.Encode(roundtrip, owner), owner);
        check(featureRoundtrip.BuiltInRepeat && featureRoundtrip.RepeatCps == 12.5f && !featureRoundtrip.Appearance[0].HideEmpty
            && featureRoundtrip.Bindings[2].Primary.Key == 5 && featureRoundtrip.Bindings[2].PrimaryEdited,
            "built-in repeat, per-bar empty-slot choice and edited side mouse binding survive full checksum-protected backup/import");
        foreach (var invalidCps in new[] { float.NaN, float.PositiveInfinity, 0f, 31f })
        {
            var invalid = Fresh(); invalid.RepeatCps = invalidCps;
            reject(() => SuperHotbarRules.Validate(invalid, owner), "invalid built-in repeat CPS rejected " + invalidCps);
        }
        check(roundtrip.KeyboardEncodingVersion == 1 && roundtrip.AppearanceDefaultsVersion == 1
            && roundtrip.Bindings[0].Primary.Key == 192 && roundtrip.Bindings[0].NativeData == raw,
            "converted encoding, transparency and native restore bytes survive hashed archive backup");
        check(SuperHotbarRules.RepeatChords(roundtrip).Contains(192 | 2 << 8)
            && SuperHotbarRules.FirstBinding(roundtrip, 192, 2)?.InputId == 57,
            "physical dot key reaches both the takeover dispatcher and repeat IPC chord list");
        for (var i = 0; i < 10; i++)
        { roundtrip.Appearance[i].X = i * 100; roundtrip.Appearance[i].Y = i * 20; roundtrip.Appearance[i].Scale = 1.1f;
            roundtrip.Appearance[i].Columns = 6; roundtrip.Appearance[i].Rows = 2; }
        var geometries = roundtrip.Appearance.Select(b => NativeBarGeometry.FromRoot(new(b.X, b.Y), b.Scale, b.Columns, b.Rows)).ToArray();
        var beforeGeometry = SuperHotbarStore.Encode(roundtrip,owner);
        var badGeometry = WorkshopProfiles.Copy(roundtrip); badGeometry.Appearance[9].Scale = float.NaN;
        reject(() => NativeBarGeometryMigration.Upgrade(badGeometry), "invalid tenth saved bar rejects whole geometry migration");
        check(SuperHotbarStore.Encode(roundtrip,owner)==beforeGeometry, "rejected geometry migration preserves all ten bars and archive bytes");
        var incomplete = WorkshopProfiles.Copy(roundtrip); incomplete.Appearance.RemoveAt(9);
        reject(() => NativeBarGeometryMigration.Upgrade(incomplete), "incomplete saved layout cannot partially migrate geometry");
        var savedEntries=roundtrip.Appearance.Select(b=>b.Slots).ToArray();
        var savedKeys=JsonSerializer.Serialize(roundtrip.Bindings);var savedShared=roundtrip.Shared.ToArray(); var mapping = roundtrip.AppearanceMappingVersion;
        check(NativeBarGeometryMigration.Upgrade(roundtrip) && roundtrip.NativeGeometryVersion==1
            && roundtrip.AppearanceMappingVersion==mapping && !NativeBarGeometryMigration.Upgrade(roundtrip)
            && roundtrip.Appearance.Select((b, i) => NativeBarGeometry.FromSaved(b) == geometries[i]).All(v => v)
            && roundtrip.Appearance.Select(b=>b.Slots).SequenceEqual(savedEntries)
            && JsonSerializer.Serialize(roundtrip.Bindings)==savedKeys && roundtrip.Shared.SequenceEqual(savedShared),
            "all-ten geometry migration is atomic, idempotent, and preserves content, key bindings and shared/job ownership");
        roundtrip.Locked=true;
        var geometryRestored=SuperHotbarStore.Decode(SuperHotbarStore.Encode(roundtrip,owner),owner);
        check(geometryRestored.Locked
            && geometryRestored.NativeGeometryVersion==1 && geometryRestored.Appearance[0].X==roundtrip.Appearance[0].X
            && geometryRestored.Bindings[0].NativeData==raw,
            "lock, native/client-pixel geometry and independent placement survive checksum-protected backup and restore");
        roundtrip.Appearance[0].Opacity = 0.85f;
        check(!SuperHotbarRules.UpgradePresentation(roundtrip, map) && roundtrip.Appearance[0].Opacity == 0.85f,
            "an intentional opacity chosen after migration is never reset on subsequent frames");
        var unknown = Fresh(); unknown.Bindings[0].Primary.Key = 136; unknown.Bindings[1].Secondary.Key = 254;
        check(SuperHotbarRules.UpgradePresentation(unknown, map) && unknown.Bindings[0].Primary.Key == 192
            && unknown.Bindings[1].Secondary.Key == 0 && unknown.Bindings[1].Secondary.UnmappedNativeKey == 254
            && unknown.Bindings[1].NativeData == raw && unknown.KeyboardEncodingVersion == 1,
            "unsupported legacy key keeps a visible restore-only identity without blocking known key migration");
        var failed = Fresh(); failed.Bindings[0].Primary.Key = 136; failed.Bindings[1].Secondary.Key = 256;
        reject(() => SuperHotbarRules.UpgradePresentation(failed, map), "malformed legacy key rejects complete migration");
        check(failed.Bindings[0].Primary.Key == 136 && failed.KeyboardEncodingVersion == 0
            && failed.AppearanceDefaultsVersion == 0 && failed.Appearance[0].Opacity == 0.85f,
            "failed migration leaves all keys, defaults and markers untouched");
        foreach (var marker in new[] { -1, 2 })
        {
            var invalid = Fresh(); invalid.KeyboardEncodingVersion = marker;
            reject(() => SuperHotbarRules.Validate(invalid, owner), "unknown keyboard encoding marker rejected " + marker);
            invalid.KeyboardEncodingVersion = 0; invalid.AppearanceDefaultsVersion = marker;
            reject(() => SuperHotbarRules.Validate(invalid, owner), "unknown appearance default marker rejected " + marker);
            invalid.AppearanceDefaultsVersion=0;invalid.NativeGeometryVersion=marker;
            reject(() => SuperHotbarRules.Validate(invalid, owner), "unknown native geometry marker rejected " + marker);
        }
        var replayPath = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_REPLAY_PROFILE");
        if (replayPath is not null)
        {
            var original = File.ReadAllBytes(replayPath);
            using var envelope = JsonDocument.Parse(original);
            using var payload = JsonDocument.Parse(envelope.RootElement.GetProperty("Payload").GetString()!);
            var replayOwner = Convert.ToUInt64(payload.RootElement.GetProperty("Character").GetString(), 16);
            var replay = SuperHotbarStore.Decode(System.Text.Encoding.UTF8.GetString(original), replayOwner);
            var rawKeys = replay.Bindings.Select(b => b.NativeData).ToArray();
            var expectedKeys = replay.Bindings.Select(b => (
                Primary: replay.KeyboardEncodingVersion != 0 || b.PrimaryEdited ? b.Primary.Key : map.Windows(b.Primary.Key),
                Secondary: replay.KeyboardEncodingVersion != 0 || b.SecondaryEdited ? b.Secondary.Key : map.Windows(b.Secondary.Key))).ToArray();
            SuperHotbarRules.UpgradePresentation(replay, map);
            var reloaded = SuperHotbarStore.Decode(SuperHotbarStore.Encode(replay, replayOwner), replayOwner);
            check(reloaded.Bindings.Select(b => b.NativeData).SequenceEqual(rawKeys)
                && reloaded.Bindings.Select(b => (b.Primary.Key, b.Secondary.Key)).SequenceEqual(expectedKeys)
                && File.ReadAllBytes(replayPath).SequenceEqual(original),
                "real character archive migrates every primary/secondary mapping and round-trips without writing live config");
        }

        var neutral = NativeSlotState.Neutral;
        check(!NativeIconVisualRules.Cooling(neutral) && !NativeIconVisualRules.ShowCharges(neutral),
            "noncombat/invalid actions remain neutral without unsafe charge or cooldown presentation");
        for (uint charges = 0; charges <= 3; charges++)
        {
            var state = new NativeSlotState(50, 38, charges, 3, false, charges > 0);
            check(NativeIconVisualRules.ShowCharges(state) && NativeIconVisualRules.ChargeDigit(state) == charges
                && NativeIconVisualRules.RecastPartList(state) == (charges == 0 ? 4 : 1)
                && NativeIconVisualRules.RecastFrame(state) == 40,
                $"charge count {charges} uses native digit including zero/one and correct depleted/available atlas");
        }
        foreach (var percent in Enumerable.Range(0, 101))
        {
            var ordinary = new NativeSlotState(percent, 10, 0, 1, false, true);
            var depleted = ordinary with { MaxCharges = 2 };
            if (NativeIconVisualRules.RecastFrame(ordinary) is < 1 or > 79
                || NativeIconVisualRules.RecastFrame(depleted) is < 1 or > 79) throw new Exception("Cooldown atlas boundary");
        }
        check(NativeIconVisualRules.RecastPartList(new(50, 11, 0, 1, false, true)) == 4,
            "all ordinary/depleted cooldown percentages stay in verified native ULD frame ranges");
        check(Enumerable.Range(0, 24).Select(i => NativeIconVisualRules.AntsFrame(i / 30.0 + 0.00001))
            .SequenceEqual(Enumerable.Range(0, 24).Select(i => 6 + i % 8)), "gold highlight cycles all eight native frames at timeline rate");
        var animation = new NativeIconAnimation();
        var cooling = new NativeSlotState(90, 1, 0, 2, false, false);
        check(animation.Observe(1, cooling, 10) == 0 && animation.Observe(1, cooling, 10.1) == 0,
            "first render and unchanged cooldown cannot synthesize completion flash");
        check(animation.Observe(1, cooling with { Charges = 1 }, 10.2) == 1
            && Math.Abs(animation.Observe(1, cooling with { Charges = 1 }, 10.5) - 0.5) < 0.0001,
            "restoring one charge triggers a fading completion flash while another charge can still cool");
        check(animation.Observe(2, cooling, 10.6) == 0 && animation.Observe(2, neutral, 10.7) == 1,
            "changing action clears old flash and cooldown completion starts a new native flash");
        check(animation.Observe(2, neutral, 11.4) == 0 && animation.Observe(2, cooling, 9) == 0,
            "hidden/reopened slots and clock reversal clear animation history");
    }
}
