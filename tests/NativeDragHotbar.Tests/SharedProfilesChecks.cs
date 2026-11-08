using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class SharedProfilesChecks
{
    public static void Run(string temporary, Action<bool, string> check, Action<Action, string> reject)
    {
        const ulong a = 0x1122334455667788;
        const ulong b = a + 1;
        const ulong c = a + 2;
        string Area(string name)
        { var directory = Path.Combine(temporary, name); Directory.CreateDirectory(directory); return directory; }
        string Legacy(string directory, ulong owner) => Path.Combine(directory, $"NativeDragHotbar-{owner:X16}.json");
        HotbarProfile Config(ulong owner, string command = "")
        {
            var profile = new HotbarProfile { Owner = owner.ToString("X16") };
            if (command.Length > 0) profile.Bars[0].Slots[0] = new() { Mode = HotbarEntryMode.Command, Command = command };
            return profile;
        }
        void WriteLegacy(string directory, ulong owner, string command, int daysAgo)
        {
            var path = Legacy(directory, owner);
            HotbarProfileStore.Save(path, Config(owner, command), owner, false);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo));
        }

        var fresh = Area("shared-fresh");
        var initial = HotbarSharedProfiles.Load(fresh, a, out var recovered, out var migrated);
        var sharedPath = HotbarSharedProfiles.PathFor(fresh);
        check(initial.Owner == "0000000000000000" && initial.NeedsUpgradeSave && !recovered && migrated is null
            && initial.Bars.Count == 1 && sharedPath == Path.Combine(fresh, "NativeDragHotbar-shared.json"),
            "fresh installation uses a stable local shared identity and filename");
        initial.Bars[0].Name = "所有角色共用";
        initial.Bars[0].Opacity = 0;
        initial.Bars[0].Scale = 1.5f;
        initial.Bars[0].AnchorX = 0.25f; initial.Bars[0].AnchorY = 0.75f;
        HotbarRules.Resize(initial.Bars[0], 10, 12);
        initial.Bars[0].Slots[119] = new() { NativeType = 2, NativeId = 1004551, Name = "隐藏物品" };
        initial.Bars[0].Slots[0] = new() { Mode = HotbarEntryMode.Command, Command = "/echo shared", IconId = 234010 };
        HotbarRules.Resize(initial.Bars[0], 2, 2);
        HotbarProfileStore.Save(sharedPath, initial, HotbarSharedProfiles.Owner, false);
        var fromB = HotbarSharedProfiles.Load(fresh, b, out recovered, out migrated);
        check(!recovered && migrated is null && fromB.Bars[0].Slots[0].Command == "/echo shared"
            && fromB.Bars[0].Slots[119].NativeId == 1004551 && fromB.Bars[0].SlotCount == 4
            && fromB.Bars[0].Opacity == 0 && fromB.Bars[0].Scale == 1.5f && fromB.Bars[0].AnchorY == 0.75f,
            "another account loads the same layout, icons, commands, opacity, anchors and hidden slots");
        fromB.Bars[0].Slots[0].Command = "/echo changed by B";
        HotbarProfileStore.Save(sharedPath, fromB, HotbarSharedProfiles.Owner, false);
        check(HotbarSharedProfiles.Load(fresh, a, out _, out _).Bars[0].Slots[0].Command == "/echo changed by B",
            "edits made on another character are visible when returning to the original character");
        WriteLegacy(fresh, b, "/echo old character", 0);
        check(HotbarSharedProfiles.Load(fresh, b, out _, out migrated).Bars[0].Slots[0].Command == "/echo changed by B"
            && migrated is null, "an existing shared profile always wins over a later legacy character file");
        var imported = HotbarTransfer.Parse(HotbarTransfer.Export(Config(a, "/echo imported"), null, true, "1.1.0"));
        HotbarTransfer.Append(fromB, HotbarSharedProfiles.Owner, imported);
        HotbarProfileStore.Save(sharedPath, fromB, HotbarSharedProfiles.Owner, false);
        check(HotbarSharedProfiles.Load(fresh, c, out _, out _).Bars[1].Slots[0].Command == "/echo imported",
            "existing exports append to the shared profile and become available to every character");

        var current = Area("shared-current-first");
        WriteLegacy(current, a, "/echo current", 10);
        WriteLegacy(current, b, "/echo newer other", 1);
        var original = File.ReadAllBytes(Legacy(current, a));
        var selected = HotbarSharedProfiles.Load(current, a, out recovered, out migrated);
        check(selected.Owner == "0000000000000000" && selected.NeedsUpgradeSave && !recovered
            && migrated == Legacy(current, a) && selected.Bars[0].Slots[0].Command == "/echo current",
            "the first logged-in character's configured hotbars take priority during migration");
        HotbarProfileStore.Save(HotbarSharedProfiles.PathFor(current), selected, HotbarSharedProfiles.Owner, false);
        check(File.ReadAllBytes(Legacy(current, a)).SequenceEqual(original)
            && !File.Exists(Legacy(current, a) + ".bak"), "migration never edits or replaces the legacy source file");

        var emptyCurrent = Area("shared-empty-current");
        var empty = Config(a); empty.Bars[0].AnchorX = 0.5f; empty.Bars[0].AnchorY = 0.5f;
        HotbarProfileStore.Save(Legacy(emptyCurrent, a), empty, a, false);
        WriteLegacy(emptyCurrent, b, "/echo older", 10);
        WriteLegacy(emptyCurrent, c, "/echo newest populated", 1);
        File.WriteAllText(Path.Combine(emptyCurrent, "NativeDragHotbar-not-a-character.json"), "invalid");
        File.WriteAllText(Path.Combine(emptyCurrent, "NativeDragHotbar-0000000000000000.json"), "invalid");
        selected = HotbarSharedProfiles.Load(emptyCurrent, a, out _, out migrated);
        check(selected.Bars[0].Slots[0].Command == "/echo newest populated" && migrated == Legacy(emptyCurrent, c),
            "an automatically positioned empty character profile cannot hide another account's configured bars");
        selected = HotbarSharedProfiles.Load(emptyCurrent, a + 9, out _, out migrated);
        check(migrated == Legacy(emptyCurrent, c), "a character with no legacy file migrates the most recent populated profile");
        var hidden = Config(b); HotbarRules.Resize(hidden.Bars[0], 10, 12);
        hidden.Bars[0].Slots[119] = new() { NativeType = 7, NativeId = 0x100, Name = "共享宏" };
        HotbarRules.Resize(hidden.Bars[0], 1, 1);
        HotbarProfileStore.Save(Legacy(emptyCurrent, a), new() { Owner = a.ToString("X16"), Bars = hidden.Bars }, a, false);
        selected = HotbarSharedProfiles.Load(emptyCurrent, a, out _, out migrated);
        check(migrated == Legacy(emptyCurrent, a) && selected.Bars[0].Slots[119].NativeId == 0x100,
            "temporarily hidden slots count as configured content and keep migration priority");

        var noContent = Area("shared-empty-custom-layout");
        var layoutOnly = Config(a); layoutOnly.Show = false; layoutOnly.Bars[0].Name = "空白自定布局";
        HotbarRules.Resize(layoutOnly.Bars[0], 7, 3);
        HotbarProfileStore.Save(Legacy(noContent, a), layoutOnly, a, false);
        selected = HotbarSharedProfiles.Load(noContent, a, out _, out migrated);
        check(selected.Bars[0].SlotCount == 21 && !selected.Show && migrated == Legacy(noContent, a),
            "if no profile contains entries, the current character's empty layout and visibility are preserved");
        check(HotbarSharedProfiles.Load(noContent, b, out _, out migrated).Bars[0].Name == "空白自定布局"
            && migrated == Legacy(noContent, a), "empty customized legacy layouts are still migrated when no current file exists");

        var backupOnly = Area("shared-legacy-backup-only");
        var old = Config(b, "/echo legacy schema"); old.SchemaVersion = 1; old.Bars[0].Columns = 3;
        var legacyBackup = Legacy(backupOnly, b) + ".bak";
        File.WriteAllText(legacyBackup, JsonSerializer.Serialize(old));
        var legacyBytes = File.ReadAllBytes(legacyBackup);
        selected = HotbarSharedProfiles.Load(backupOnly, a, out recovered, out migrated);
        check(!recovered && migrated == legacyBackup && selected.SchemaVersion == 2
            && selected.Bars[0].Columns == 3 && selected.Bars[0].Rows == 4,
            "backup-only legacy profiles are discovered and schema one layouts migrate without changing shape");
        HotbarProfileStore.Save(HotbarSharedProfiles.PathFor(backupOnly), selected, HotbarSharedProfiles.Owner, false);
        check(File.ReadAllBytes(legacyBackup).SequenceEqual(legacyBytes) && !File.Exists(Legacy(backupOnly, b)),
            "migration from a legacy backup keeps both the backup bytes and missing main file intact");
        var legacyCorrupt = Area("shared-legacy-recovered");
        WriteLegacy(legacyCorrupt, a, "/echo restored legacy", 1);
        File.Copy(Legacy(legacyCorrupt, a), Legacy(legacyCorrupt, a) + ".bak");
        File.WriteAllText(Legacy(legacyCorrupt, a), "broken");
        selected = HotbarSharedProfiles.Load(legacyCorrupt, a, out _, out migrated);
        check(selected.Bars[0].Slots[0].Command == "/echo restored legacy"
            && migrated == Legacy(legacyCorrupt, a) + ".bak" && File.ReadAllText(Legacy(legacyCorrupt, a)) == "broken",
            "a valid current-character backup can seed sharing without replacing its damaged original");

        var invalidCurrent = Area("shared-invalid-current");
        File.WriteAllText(Legacy(invalidCurrent, a), "broken current");
        WriteLegacy(invalidCurrent, b, "/echo other", 0);
        reject(() => HotbarSharedProfiles.Load(invalidCurrent, a, out _, out _),
            "an unreadable current profile fails closed rather than silently replacing it with another account's settings");
        check(!File.Exists(HotbarSharedProfiles.PathFor(invalidCurrent)) && File.ReadAllText(Legacy(invalidCurrent, a)) == "broken current",
            "failed migration creates no shared file and leaves legacy bytes untouched");
        var invalidOther = Area("shared-invalid-other");
        WriteLegacy(invalidOther, b, "/echo valid older", 10);
        File.WriteAllText(Legacy(invalidOther, c), "broken other");
        check(HotbarSharedProfiles.Load(invalidOther, a, out _, out _).Bars[0].Slots[0].Command == "/echo valid older",
            "a damaged unrelated legacy file does not prevent migration from a readable populated profile");
        File.Delete(Legacy(invalidOther, b));
        reject(() => HotbarSharedProfiles.Load(invalidOther, a, out _, out _),
            "unreadable legacy files cannot be replaced by a newly initialized empty shared profile");

        var damagedShared = Area("shared-damaged");
        var damagedPath = HotbarSharedProfiles.PathFor(damagedShared);
        HotbarProfileStore.Save(damagedPath, Config(0, "/echo shared backup"), 0, false);
        File.Copy(damagedPath, damagedPath + ".bak");
        var sharedBackupBytes = File.ReadAllBytes(damagedPath + ".bak");
        File.WriteAllText(damagedPath, "broken shared");
        WriteLegacy(damagedShared, a, "/echo unrelated legacy", 0);
        selected = HotbarSharedProfiles.Load(damagedShared, a, out recovered, out migrated);
        check(recovered && migrated is null && selected.Bars[0].Slots[0].Command == "/echo shared backup"
            && Directory.GetFiles(damagedShared, "*.corrupt-*").Length == 1,
            "a damaged shared main file recovers its own backup instead of falling back to per-character settings");
        HotbarProfileStore.Save(damagedPath, selected, 0, recovered);
        check(File.ReadAllBytes(damagedPath + ".bak").SequenceEqual(sharedBackupBytes),
            "saving recovered shared settings preserves the valid shared backup");
        File.Delete(damagedPath);
        check(HotbarSharedProfiles.Load(damagedShared, b, out recovered, out migrated).Bars[0].Slots[0].Command == "/echo shared backup"
            && recovered && migrated is null, "a backup-only shared configuration still takes precedence over legacy files");
        File.WriteAllText(damagedPath, "broken shared"); File.WriteAllText(damagedPath + ".bak", "broken backup");
        reject(() => HotbarSharedProfiles.Load(damagedShared, a, out _, out _),
            "two unreadable shared files fail closed even if a usable legacy profile exists");
        selected.SchemaVersion = 99; File.WriteAllText(damagedPath, JsonSerializer.Serialize(selected));
        reject(() => HotbarSharedProfiles.Load(damagedShared, a, out _, out _),
            "a future shared schema cannot silently roll back to a legacy profile");
        var wrongIdentity = Config(a, "/echo wrong identity");
        File.WriteAllText(damagedPath, JsonSerializer.Serialize(wrongIdentity));
        reject(() => HotbarSharedProfiles.Load(damagedShared, a, out _, out _),
            "a character-owned file renamed as shared is rejected until explicitly migrated");
        var deliberatelyEmpty = Config(0); deliberatelyEmpty.Bars.Clear();
        File.WriteAllText(damagedPath, JsonSerializer.Serialize(deliberatelyEmpty));
        check(HotbarSharedProfiles.Load(damagedShared, a, out _, out migrated).Bars.Count == 0 && migrated is null,
            "intentionally empty shared settings never resurrect old character hotbars");
    }
}
