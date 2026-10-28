using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OmniToolbox.AchievementTrackerOnline;

var root = Path.Combine(Path.GetTempPath(), "omni-achievement-storage-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var errors = new List<Exception>();
var store = new AchievementTrackerProfileStore((error, _) => errors.Add(error));
var checks = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
void Reject<T>(Action action, string message) where T : Exception
{
    try { action(); throw new Exception("Expected rejection: " + message); }
    catch (T) { Check(true, message); }
}
try
{
    const ulong owner = 0x123456789ABCDEF0;
    var oldJson = """{"Goals":[{"Id":3672,"Name":"成就","Note":"保留备注","RegionId":1252,"Region":"地区","RegionGroup":"区域","ShowCompleted":false}],"ShowOverlay":false,"Opacity":0.75}""";
    var old = AchievementTrackerProfileJson.Deserialize(oldJson, owner);
    Check(old.Goals.Count == 1 && old.Goals[0].Note == "保留备注" && old.Goals[0].Regions[0].Id == 1252, "legacy goals, notes and region migrate");
    Check(!old.ShowOverlay && !old.Goals[0].ShowCompleted && old.Opacity == 0.75f, "legacy per-character switches survive");
    var json = AchievementTrackerProfileJson.Serialize(old, owner);
    var roundTrip = AchievementTrackerProfileJson.Deserialize(json, owner);
    Check(roundTrip.ContentId == "123456789ABCDEF0" && roundTrip.SchemaVersion == 1 && roundTrip.Goals[0].Note == old.Goals[0].Note, "schema and 64-bit character ID round-trip");
    Reject<InvalidDataException>(() => AchievementTrackerProfileJson.Deserialize(json, owner + 1), "wrong-character archive rejected");
    Reject<NotSupportedException>(() => AchievementTrackerProfileJson.Deserialize("""{"SchemaVersion":999}""", owner), "future schema rejected without downgrade");
    Reject<JsonException>(() => AchievementTrackerProfileJson.Deserialize("{truncated", owner), "truncated JSON rejected");
    var cleaned = AchievementTrackerProfileJson.Deserialize("""{"Goals":[null,{"Id":3672,"Regions":[null,{"Id":1252,"Name":null}]}],"Opacity":99}""", owner);
    Check(cleaned.Goals.Count == 1 && cleaned.Goals[0].Regions.Count == 1 && cleaned.Goals[0].Regions[0].Name == "" && cleaned.Opacity == 1f, "malformed optional entries normalized");
    var a = Path.Combine(root, "AchievementTracker-123456789ABCDEF0.json");
    var b = Path.Combine(root, "AchievementTracker-223456789ABCDEF0.json");
    store.Save(a, json, null, null);
    Check(store.TryGetSnapshot(a, out var queued) && queued == json, "character switch can read pending snapshot");
    await store.FlushAsync();
    Check(File.ReadAllText(a) == json, "first save persisted");
    store.Save(a, "second", null, null);
    await store.FlushAsync();
    Check(File.ReadAllText(a) == "second" && File.ReadAllText(a + ".bak") == json, "atomic replacement keeps previous backup");
    for (var i = 0; i < 80; i++)
    {
        store.Save(a, "A" + i, null, null);
        store.Save(b, "B" + i, null, null);
    }
    await store.FlushAsync();
    Check(File.ReadAllText(a) == "A79" && File.ReadAllText(b) == "B79", "queued writes preserve latest data for both characters");
    File.WriteAllText(a, "broken primary");
    File.WriteAllText(a + ".bak", "good backup");
    store.PreserveBackup(a);
    store.Save(a, "recovered", null, null);
    await store.FlushAsync();
    Check(File.ReadAllText(a) == "recovered" && File.ReadAllText(a + ".bak") == "good backup", "recovering a primary does not replace good backup with corruption");
    Check(Directory.GetFiles(root, "*.corrupt-*").Any(path => File.ReadAllText(path) == "broken primary"), "corrupt original retained for recovery");
    var migration = Path.Combine(root, "AchievementTracker.legacy-migrated");
    store.Save(b, "migrated archive", migration, "223456789ABCDEF0");
    await store.FlushAsync();
    Check(File.ReadAllText(b) == "migrated archive" && File.ReadAllText(migration) == "223456789ABCDEF0", "migration marker follows successful archive write");
    var blocker = Path.Combine(root, "blocked-directory");
    File.WriteAllText(blocker, "keep");
    var failedMarker = Path.Combine(root, "must-not-exist");
    store.Save(Path.Combine(blocker, "profile.json"), "unsaved", failedMarker, "owner");
    await store.FlushAsync();
    Check(errors.Count == 1 && store.LastError is not null && !File.Exists(failedMarker) && File.ReadAllText(blocker) == "keep", "failed save preserves existing files and does not mark migration complete");
    store.Save(b, "retry after failure", null, null);
    await store.FlushAsync();
    Check(File.ReadAllText(b) == "retry after failure" && store.GetError(b) is null && store.LastError is not null, "other characters save without hiding a failed profile");
    File.Delete(blocker);
    store.Save(Path.Combine(blocker, "profile.json"), "recovered write", failedMarker, "owner");
    await store.FlushAsync();
    Check(store.LastError is null && File.ReadAllText(Path.Combine(blocker, "profile.json")) == "recovered write" && File.Exists(failedMarker), "failed profile retries and clears its own error");
    Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "no unfinished temporary files");
    Console.WriteLine($"All {checks} storage checks passed.");
}
finally
{
    await store.FlushAsync();
    Directory.Delete(root, true);
}
