using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed class WorkshopBarVisibility
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; }
}

internal sealed class WorkshopVisibilityGroup
{
    public bool Show { get; set; }
    public List<WorkshopBarVisibility> Bars { get; set; } = [];
    public static WorkshopVisibilityGroup Capture(bool show, IReadOnlyList<DragHotbar> bars) => new()
    { Show = show, Bars = bars.Select(b => new WorkshopBarVisibility { Id = b.Id, Visible = b.Visible }).ToList() };

    public bool Matches(bool show, IReadOnlyList<DragHotbar> bars) => Show == show && Bars.Count == bars.Count
        && Bars.Zip(bars).All(pair => pair.First.Id == pair.Second.Id && pair.First.Visible == pair.Second.Visible);

    public bool Apply(IReadOnlyList<DragHotbar> bars)
    {
        // A reset/import/new capture can replace identities. Never resurrect an old bar layout.
        if (Bars.Count != bars.Count || !Bars.Select(b => b.Id).ToHashSet(StringComparer.Ordinal)
            .SetEquals(bars.Select(b => b.Id))) return false;
        var visible = Bars.ToDictionary(b => b.Id, b => b.Visible, StringComparer.Ordinal);
        foreach (var bar in bars) bar.Visible = visible[bar.Id];
        return true;
    }
}

internal sealed class WorkshopVisibilityState
{
    public int Version { get; set; } = 1;
    public string ProfileId { get; set; } = "";
    public long Revision { get; set; }
    public WorkshopVisibilityGroup? Takeover { get; set; }
    public WorkshopVisibilityGroup Extensions { get; set; } = new();
}

internal static class WorkshopVisibilityCommands
{
    // Persist the tiny candidate before exposing the change. A failed write leaves live state intact.
    public static bool Apply(WorkshopProfile saved, SuperHotbarDocument? takeover, HotbarProfile extensions,
        bool native, HotbarVisibilityAction action, string selector, Action<WorkshopVisibilityState> persist,
        out int index, out bool changed)
    {
        changed = false; index = -1;
        if (native && takeover is null) return false;
        var bars = native ? takeover!.Appearance : extensions.Bars;
        var show = native ? takeover!.Show : extensions.Show;
        var candidate = new HotbarProfile { Show = show, Bars = bars.Select(b => new DragHotbar
            { Id = b.Id, Name = b.Name, Visible = b.Visible }).ToList() };
        if (!HotbarVisibilityCommands.Apply(candidate, action, selector, out index)) return false;
        var group = WorkshopVisibilityGroup.Capture(candidate.Show, candidate.Bars);
        if (group.Matches(show, bars)) return true;
        Commit(saved, takeover, extensions, native, group, persist);
        changed = true;
        return true;
    }

    // Settings checkboxes change only their own flag, unlike an "on" command which
    // may also reveal individual bars. Both paths use the same small durable state.
    public static bool Set(WorkshopProfile saved, SuperHotbarDocument? takeover, HotbarProfile extensions,
        bool native, int index, bool visible, Action<WorkshopVisibilityState> persist)
    {
        if (native && takeover is null) return false;
        var bars = native ? takeover!.Appearance : extensions.Bars;
        if (index < -1 || index >= bars.Count) return false;
        var show = native ? takeover!.Show : extensions.Show;
        var group = WorkshopVisibilityGroup.Capture(show, bars);
        if (index < 0) group.Show = visible; else group.Bars[index].Visible = visible;
        if (group.Matches(show, bars)) return true;
        Commit(saved, takeover, extensions, native, group, persist);
        return true;
    }

    private static void Commit(WorkshopProfile saved, SuperHotbarDocument? takeover, HotbarProfile extensions,
        bool native, WorkshopVisibilityGroup group, Action<WorkshopVisibilityState> persist)
    {
        var state = new WorkshopVisibilityState { ProfileId = saved.Id, Revision = checked(saved.VisibilityRevision + 1),
            Takeover = takeover is null ? null : WorkshopVisibilityGroup.Capture(takeover.Show, takeover.Appearance),
            Extensions = WorkshopVisibilityGroup.Capture(extensions.Show, extensions.Bars) };
        if (native) state.Takeover = group; else state.Extensions = group;
        persist(state);
        // No fallible native operations or full-archive work after the durable commit.
        var bars = native ? takeover!.Appearance : extensions.Bars;
        for (var i = 0; i < bars.Count; i++) bars[i].Visible = group.Bars[i].Visible;
        if (native)
        {
            takeover!.Show = group.Show;
            if (saved.Takeover is { } archive)
            {
                archive.Show = group.Show;
                foreach (var bar in archive.Appearance)
                    if (group.Bars.Find(b => b.Id == bar.Id) is { } entry) bar.Visible = entry.Visible;
            }
        }
        else
        {
            extensions.Show = group.Show;
            saved.Extensions.Show = group.Show;
            foreach (var bar in saved.Extensions.Bars)
                if (group.Bars.Find(b => b.Id == bar.Id) is { } entry) bar.Visible = entry.Visible;
        }
        saved.VisibilityRevision = state.Revision;
    }
}

internal static class WorkshopVisibilityStore
{
    private const string Format = "HotbarWorkshop.Visibility";
    private const int MaximumCharacters = 16_384;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12 };
    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public string Payload { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    public static string PathFor(string directory, string id)
    {
        if (!Guid.TryParse(id, out var guid)) throw new InvalidDataException("显隐记录的配置标识无效。");
        return Path.Combine(directory, "HotbarWorkshopVisibility", guid.ToString("N") + ".json");
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Validate(WorkshopVisibilityState state)
    {
        if (state.Version != 1 || !Guid.TryParse(state.ProfileId, out _) || state.Revision <= 0 || state.Extensions is null)
            throw new InvalidDataException("显隐记录版本或配置标识无效。");
        void Group(WorkshopVisibilityGroup value, int maximum)
        {
            if (value.Bars is null || value.Bars.Count > maximum || value.Bars.Any(b => b is null
                || !Guid.TryParse(b.Id, out _)) || value.Bars.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != value.Bars.Count)
                throw new InvalidDataException("显隐记录栏位标识无效。");
        }
        Group(state.Extensions, HotbarRules.MaxBars);
        if (state.Takeover is not null) { Group(state.Takeover, 10); if (state.Takeover.Bars.Count != 10)
                throw new InvalidDataException("接管栏显隐记录不完整。"); }
    }
    public static string Encode(WorkshopVisibilityState state)
    {
        Validate(state);
        var payload = JsonSerializer.Serialize(state, Json);
        var text = JsonSerializer.Serialize(new Envelope { Format = Format, Payload = payload, Sha256 = Hash(payload) }, Json);
        if (text.Length > MaximumCharacters) throw new InvalidDataException("显隐记录过大，未写入。");
        return text;
    }
    public static WorkshopVisibilityState Decode(string text)
    {
        if (text.Length > MaximumCharacters) throw new InvalidDataException("显隐记录过大。");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(text, Json);
            if (envelope is null || envelope.Format != Format || envelope.Payload is null || envelope.Sha256 != Hash(envelope.Payload))
                throw new InvalidDataException("显隐记录校验失败。");
            var state = JsonSerializer.Deserialize<WorkshopVisibilityState>(envelope.Payload, Json)
                ?? throw new InvalidDataException("显隐记录为空。");
            Validate(state); return state;
        }
        catch (JsonException error) { throw new InvalidDataException("显隐记录格式无效。", error); }
    }
    public static void Save(string directory, WorkshopVisibilityState state)
    {
        var text = Encode(state); var path = PathFor(directory, state.ProfileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static bool Replay(string directory, WorkshopLibrary library)
    {
        var applied = false;
        foreach (var profile in library.Profiles)
        {
            var path = PathFor(directory, profile.Id);
            if (!File.Exists(path) && !File.Exists(path + ".bak")) continue;
            WorkshopVisibilityState? state = null; Exception? first = null;
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    state = Decode(File.ReadAllText(candidate));
                    if (state.ProfileId != profile.Id) throw new InvalidDataException("显隐记录不属于此配置。");
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { state = null; first ??= error; }
            }
            if (state is null) throw new InvalidDataException("显隐记录与备份均不可读，已暂停载入，避免覆盖最近状态。", first);
            if (state.Revision <= profile.VisibilityRevision) continue;
            if (profile.Takeover is not null && state.Takeover is not null && state.Takeover.Apply(profile.Takeover.Appearance))
                profile.Takeover.Show = state.Takeover.Show;
            if (state.Extensions.Apply(profile.Extensions.Bars)) profile.Extensions.Show = state.Extensions.Show;
            profile.VisibilityRevision = state.Revision; applied = true;
        }
        return applied;
    }
}
