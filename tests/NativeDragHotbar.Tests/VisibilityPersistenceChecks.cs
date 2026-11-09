using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using OmniToolbox.NativeDragHotbarLocal;

internal static class VisibilityPersistenceChecks
{
    private const ulong A = 0x1122334455667788, B = 0x8877665544332211;
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, WorkshopProfiles.Json);
    private static WorkshopLibrary Fresh()
    {
        var library = new WorkshopLibrary();
        var takeover = WorkshopProfileChecks.Fresh(A); takeover.Enabled = true;
        foreach (var bar in takeover.Appearance) bar.Visible = false;
        WorkshopProfiles.Add(library, new() { Takeover = WorkshopProfiles.Portable(takeover),
            Extensions = new() { Owner = "0000000000000000", Bars = [new() { Name = "扩展测试", Visible = false }] } }, "显隐测试");
        return library;
    }
    internal static void Run(string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        var directory = Path.Combine(temp, "visibility-record");
        var library = Fresh(); var saved = library.Current;
        WorkshopLibraryStore.Save(directory, library, false);
        var mainPath = WorkshopLibraryStore.PathFor(directory); var mainBefore = File.ReadAllBytes(mainPath);
        var runtime = WorkshopProfiles.Bind(saved, A)!;
        var contentsBefore = Json(runtime.Groups); var macrosBefore = Json(runtime.Macros);
        var bindingBefore = Json(runtime.Bindings); var writes = 0;
        WorkshopVisibilityState? latest = null;
        var elapsed = Stopwatch.StartNew();
        for (var i = 1; i <= 5; i++)
        {
            check(WorkshopVisibilityCommands.Apply(saved, runtime, saved.Extensions, true, HotbarVisibilityAction.Show, "#" + i,
                state => { check(!runtime.Appearance[i - 1].Visible && saved.VisibilityRevision == i - 1,
                    "visibility and revision remain unchanged until durable write for bar #" + i);
                    latest = state; WorkshopVisibilityStore.Save(directory, state); writes++; }, out var index, out var changed)
                && index == i - 1 && changed && runtime.Appearance[i - 1].Visible,
                "takeover visibility command commits a tiny durable record before exposing bar #" + i);
        }
        elapsed.Stop();
        check(writes == 5 && saved.VisibilityRevision == 5 && File.ReadAllBytes(mainPath).SequenceEqual(mainBefore),
            "five commands leave the large library untouched while committing five small state changes");
        var tiny = new FileInfo(WorkshopVisibilityStore.PathFor(directory, saved.Id)).Length;
        check(tiny < mainBefore.Length / 10 && !WorkshopVisibilityStore.Encode(latest!).Contains("Macros"),
            "small visibility record contains no macro/slot/key archive and is below one tenth of the test library");
        Console.WriteLine($"INFO synthetic five-command record writes: {elapsed.Elapsed.TotalMilliseconds:F2} ms; record {tiny} bytes; library {mainBefore.Length} bytes");

        // No graceful shutdown or full-library save occurs before this fresh load.
        var afterExit = WorkshopLibraryStore.Load(directory, B, true, out _, out _);
        check(WorkshopVisibilityStore.Replay(directory, afterExit)
            && WorkshopProfiles.Bind(afterExit.Current, B)!.Appearance.Take(5).All(b => b.Visible)
            && afterExit.Current.Takeover!.Appearance.Skip(5).All(b => !b.Visible),
            "restart on another character replays only the latest visible states without any exit callback");
        check(Json(runtime.Groups) == contentsBefore && Json(runtime.Macros) == macrosBefore && Json(runtime.Bindings) == bindingBefore,
            "visibility command burst leaves saved content, macro backup and key bindings untouched");
        check(WorkshopVisibilityCommands.Apply(saved, runtime, saved.Extensions, true, HotbarVisibilityAction.Show, "#1",
            _ => throw new Exception("No-op must not write"), out _, out var noop) && !noop && saved.VisibilityRevision == 5,
            "repeated on for an already shown bar skips disk work and revision changes");
        check(!WorkshopVisibilityCommands.Apply(saved, runtime, saved.Extensions, true, HotbarVisibilityAction.Hide, "#99",
            _ => throw new Exception("Invalid selector must not write"), out _, out _) && saved.VisibilityRevision == 5,
            "invalid visibility selector cannot write or mutate a profile");
        var beforeFailure = Json(runtime); var revision = saved.VisibilityRevision;
        try
        {
            WorkshopVisibilityCommands.Apply(saved, runtime, saved.Extensions, true, HotbarVisibilityAction.Hide, "#1",
                _ => throw new IOException("simulated disk failure"), out _, out _);
            throw new Exception("Expected persistence failure");
        }
        catch (IOException) { check(Json(runtime) == beforeFailure && saved.VisibilityRevision == revision,
            "failed small-record write preserves the live visible state and revision"); }
        check(WorkshopVisibilityCommands.Apply(saved, runtime, saved.Extensions, false, HotbarVisibilityAction.Show, "扩展测试",
            state => { latest = state; WorkshopVisibilityStore.Save(directory, state); }, out var extraIndex, out var extraChanged)
            && extraIndex == 0 && extraChanged && saved.Extensions.Bars[0].Visible,
            "extension command uses the same durable record path and accepts a bar name");
        afterExit = WorkshopLibraryStore.Load(directory, A, true, out _, out _);
        WorkshopVisibilityStore.Replay(directory, afterExit);
        check(afterExit.Current.Extensions.Bars[0].Visible && afterExit.Current.Takeover!.Appearance.Take(5).All(b => b.Visible),
            "combined extension/takeover visibility states survive a restart together");
        WorkshopVisibilityCommands.Apply(saved, runtime, saved.Extensions, true, HotbarVisibilityAction.Hide, "",
            state => WorkshopVisibilityStore.Save(directory, state), out _, out _);
        afterExit = WorkshopLibraryStore.Load(directory, B, true, out _, out _);
        WorkshopVisibilityStore.Replay(directory, afterExit);
        check(!afterExit.Current.Takeover!.Show && afterExit.Current.Takeover.Appearance.Take(5).All(b => b.Visible),
            "global hide replays without erasing per-bar visibility preferences");
        afterExit.Current.Takeover.Appearance[0].X = 712;
        WorkshopLibraryStore.Save(directory, afterExit, false);
        var compacted = WorkshopLibraryStore.Load(directory, A, true, out _, out _);
        check(!WorkshopVisibilityStore.Replay(directory, compacted) && compacted.Current.Takeover!.Appearance[0].X == 712,
            "full-library compaction checkpoints the revision and does not replay its old record");
        compacted.Current.Takeover!.Show = true; compacted.Current.Takeover.Appearance[0].Visible = false;
        WorkshopLibraryStore.Save(directory, compacted, false);
        var later = WorkshopLibraryStore.Load(directory, A, true, out _, out _);
        check(!WorkshopVisibilityStore.Replay(directory, later) && later.Current.Takeover!.Show && !later.Current.Takeover.Appearance[0].Visible,
            "later UI edits in a full save take precedence over old command records");

        var path = WorkshopVisibilityStore.PathFor(directory, saved.Id);
        var complete = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { WorkshopVisibilityStore.Save(directory, latest!); throw new Exception("Expected locked-file rejection"); }
            catch (IOException) { check(File.ReadAllBytes(path).SequenceEqual(complete)
                && File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "locked record replacement preserves both committed files"); }
        }
        check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Any(), "failed record replacement cleans up its own temporary file");
        File.WriteAllText(path + ".tmp-interrupted", "partial write");
        check(!WorkshopVisibilityStore.Replay(directory, later), "an interrupted temporary write is never treated as committed state");
        File.WriteAllText(path, "corrupt");
        var recover = WorkshopLibraryStore.Load(directory, B, true, out _, out _);
        recover.Current.VisibilityRevision = 0;
        check(WorkshopVisibilityStore.Replay(directory, recover) && recover.Current.Extensions.Bars[0].Visible,
            "corrupt current small record falls back to its last valid atomic backup");
        File.WriteAllText(path + ".bak", "corrupt too");
        reject(() => WorkshopVisibilityStore.Replay(directory, recover), "unreadable record and backup fail closed rather than overwriting last state");

        var different = Fresh(); var otherId = different.Current.Id;
        check(!WorkshopVisibilityStore.Replay(directory, different) && otherId != saved.Id,
            "another profile cannot consume the first profile's visibility record");
        reject(() => WorkshopVisibilityStore.PathFor(directory, "../../bad"), "record path rejects non-profile identifiers");
        reject(() => WorkshopVisibilityStore.Decode(WorkshopVisibilityStore.Encode(latest!).Replace("HotbarWorkshop.Visibility", "Unknown.Visibility")),
            "unknown small-record format is rejected");
        reject(() => WorkshopVisibilityStore.Decode(WorkshopVisibilityStore.Encode(latest!).Replace("Sha256", "BrokenHash")),
            "small-record checksum prevents partial/corrupt state replay");
        var envelope = JsonNode.Parse(WorkshopVisibilityStore.Encode(latest!))!;
        var payload = JsonNode.Parse(envelope["Payload"]!.GetValue<string>())!;
        payload["Takeover"]!["Bars"]![0]!["Id"] = "bad";
        var encoded = payload.ToJsonString(); envelope["Payload"] = encoded;
        envelope["Sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)));
        reject(() => WorkshopVisibilityStore.Decode(envelope.ToJsonString()), "checksummed small record still validates every bar identity");
        latest!.Revision = long.MaxValue; saved.VisibilityRevision = long.MaxValue;
        try
        {
            WorkshopVisibilityCommands.Apply(saved, runtime, saved.Extensions, true, HotbarVisibilityAction.Show, "#8", _ => { }, out _, out _);
            throw new Exception("Expected revision overflow");
        }
        catch (OverflowException) { check(!runtime.Appearance[7].Visible, "revision overflow cannot expose an unpersisted state"); }
    }

    internal static void Compiled(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        var type = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar", true)!;
        string[] Calls(string name) => ItemAndFontChecks.Calls(type.GetMethod(name, flags)!).ToArray();
        var command = Calls("TryHandleCommand");
        check(command.Contains("ApplyWorkshopVisibilityCommand") && !command.Contains("SaveSuper") && !command.Contains("Save"),
            "actual visibility command dispatch no longer performs a full-library save per line");
        check(Calls("EnsureWorkshopLibrary").Contains("Replay"), "actual library startup replays durable visibility records before character binding");
        check(Calls("ApplyWorkshopVisibilityCommand").Contains("Apply") && !Calls("ApplyWorkshopVisibilityCommand").Contains("SaveWorkshop"),
            "actual visibility handler delegates to write-before-apply persistence and schedules compaction");
        check(Calls("CommitWorkshopEdit").Contains("SaveWorkshop") && Calls("SaveWorkshop").Contains("Save"),
            "configuration switches retain full save and failure checks before replacing runtime references");
    }
}
