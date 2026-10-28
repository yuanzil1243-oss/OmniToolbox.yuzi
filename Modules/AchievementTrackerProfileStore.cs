using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OmniToolbox.AchievementTrackerOnline;

// Independent of game services so disk behavior can be verified without running the game.
internal sealed class AchievementTrackerProfileStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> corruptPrimaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<Exception, string> reportError;
    private Task pending = Task.CompletedTask;

    internal string? LastError
    {
        get { lock (gate) { foreach (var error in errors.Values) return error; return null; } }
    }

    internal string? GetError(string path)
    {
        lock (gate) return errors.TryGetValue(path, out var error) ? error : null;
    }

    internal AchievementTrackerProfileStore(Action<Exception, string> reportError)
        => this.reportError = reportError;

    internal bool TryGetSnapshot(string path, out string json)
    {
        lock (gate) return snapshots.TryGetValue(path, out json!);
    }

    internal void PreserveBackup(string path)
    {
        lock (gate) corruptPrimaries.Add(path);
    }

    internal void Save(string path, string json, string? migrationMarker, string? migrationOwner)
    {
        // The caller supplies immutable text and paths, never a mutable active-character field.
        lock (gate)
        {
            snapshots[path] = json;
            pending = pending.ContinueWith(_ =>
            {
                try
                {
                    bool preserveBackup;
                    lock (gate) preserveBackup = corruptPrimaries.Contains(path);
                    if (preserveBackup && File.Exists(path))
                        File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    WriteAtomically(path, json, !preserveBackup);
                    lock (gate) corruptPrimaries.Remove(path);
                    if (migrationMarker is not null && migrationOwner is not null)
                        WriteAtomically(migrationMarker, migrationOwner, false);
                    lock (gate) errors.Remove(path);
                }
                catch (Exception error)
                {
                    lock (gate) errors[path] = error.Message;
                    reportError(error, path);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    internal Task FlushAsync()
    {
        lock (gate) return pending;
    }

    private static void WriteAtomically(string path, string content, bool createBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, createBackup ? path + ".bak" : null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
