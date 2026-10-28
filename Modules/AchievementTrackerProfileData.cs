using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OmniToolbox.AchievementTrackerOnline;

internal static class AchievementTrackerProfileJson
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, WriteIndented = true };

    internal static AchievementTrackerProfileData Deserialize(string json, ulong contentId)
    {
        var saved = JsonSerializer.Deserialize<AchievementTrackerProfileData>(json, Options)
            ?? throw new InvalidDataException("The achievement profile contains no object.");
        if (saved.SchemaVersion > 1 || saved.SchemaVersion < 0)
            throw new NotSupportedException($"Unsupported achievement profile schema: {saved.SchemaVersion}");
        if (!string.IsNullOrWhiteSpace(saved.ContentId)
            && !string.Equals(saved.ContentId, contentId.ToString("X16"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The profile belongs to a different character.");
        saved.Goals ??= new List<AchievementGoal>();
        saved.Goals.RemoveAll(goal => goal is null || (goal.Id <= 0 && string.IsNullOrWhiteSpace(goal.Name)));
        foreach (var goal in saved.Goals)
        {
            goal.Name ??= "";
            goal.Note ??= "";
            goal.Region ??= "";
            goal.RegionGroup ??= "";
            goal.Regions ??= new List<AchievementRegion>();
            goal.Regions.RemoveAll(region => region is null);
            foreach (var region in goal.Regions)
            {
                region.Name ??= "";
                region.Group ??= "";
            }
            MigrateLegacyRegion(goal);
        }
        saved.Opacity = float.IsFinite(saved.Opacity) ? Math.Clamp(saved.Opacity, 0.1f, 1f) : 0.92f;
        saved.SchemaVersion = 1;
        return saved;
    }

    internal static string Serialize(AchievementTrackerProfileData profile, ulong contentId)
    {
        if (contentId == 0) throw new ArgumentOutOfRangeException(nameof(contentId));
        profile.SchemaVersion = 1;
        profile.ContentId = contentId.ToString("X16");
        return JsonSerializer.Serialize(profile, Options);
    }

    internal static void MigrateLegacyRegion(AchievementGoal goal)
    {
        if (goal.Regions.Count != 0 || (goal.RegionId <= 0 && string.IsNullOrWhiteSpace(goal.Region))) return;
        goal.Regions.Add(new AchievementRegion { Id = goal.RegionId, Group = goal.RegionGroup, Name = goal.Region });
    }
}

[Serializable]
public sealed class AchievementTrackerProfileData
{
    public int SchemaVersion = 1;
    public string ContentId = "";
    public List<AchievementGoal> Goals = new();
    public bool ShowOverlay = true;
    public bool ShowCompleted = true;
    public float Opacity = 0.92f;
}

[Serializable]
public sealed class AchievementGoal
{
    public int Id;
    public string Name = "";
    public string Region = "";
    public string RegionGroup = "";
    public int RegionId;
    public List<AchievementRegion> Regions = new();
    public string Note = "";
    public bool Enabled = true;
    public bool IgnoreRegion = true;
    public bool ShowCompleted = true;
    public bool Expanded = true;
}

[Serializable]
public sealed class AchievementRegion
{
    public int Id;
    public string Group = "";
    public string Name = "";
    public AchievementRegion Clone() => new() { Id = Id, Group = Group, Name = Name };
}

public readonly record struct AchievementProgress(uint Current, uint Max);
