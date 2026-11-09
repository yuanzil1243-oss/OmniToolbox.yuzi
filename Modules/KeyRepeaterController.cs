using System;
using System.Collections.Generic;

namespace OmniToolbox.KeyRepeaterOnline;

internal sealed class KeyRepeatController
{
    private readonly HashSet<int> bindings = new();
    private readonly HashSet<int> blocked = new();
    private readonly Dictionary<int, long> due = new();
    public int BindingCount => bindings.Count;
    internal int ScheduledCount => due.Count;

    public void Clear() { bindings.Clear(); blocked.Clear(); due.Clear(); }
    public void Suspend(Func<int, bool> down)
    {
        due.Clear();
        blocked.RemoveWhere(key => !down(key));
        foreach (var chord in bindings)
        {
            var key = chord & 255;
            if (down(key)) blocked.Add(key);
        }
    }
    public void ReplaceBindings(HashSet<int> current, Func<int, bool> down)
    {
        if (bindings.SetEquals(current)) return;
        bindings.Clear(); bindings.UnionWith(current); Suspend(down);
    }
    public int Poll(long now, float cps, int modifiers, Func<int, bool> down, Action<int> send)
    {
        blocked.RemoveWhere(key => !down(key));
        var interval = Math.Max(1, (long)(1000 / (float.IsFinite(cps) ? Math.Clamp(cps, 1, 30) : 10)));
        var running = 0;
        foreach (var chord in bindings)
        {
            var key = chord & 255;
            if (modifiers != chord >> 8 || !down(key) || blocked.Contains(key)) { due.Remove(chord); continue; }
            running++;
            // Physical down already owns the first activation. Schedule the first repeat only.
            if (!due.TryGetValue(chord, out var next)) { due[chord] = now + interval; continue; }
            if (now < next) continue;
            send(key);
            due[chord] = now + interval; // never catch up with a burst after a slow frame
        }
        return running;
    }
}
