using System;
using System.Collections.Generic;

namespace OmniToolbox.KeyRepeaterOnline;

internal sealed class KeyRepeatTiming
{
    private readonly Dictionary<int, long> due = [];
    public void Clear() => due.Clear();
    public void Release(int chord) => due.Remove(chord);
    public bool Tick(int chord, long now, float cps, out bool first)
    {
        first = !due.TryGetValue(chord, out var next);
        if (!first && now < next) return false;
        if (!float.IsFinite(cps)) cps = 10;
        due[chord] = now + Math.Max(1, (long)(1000 / Math.Clamp(cps, 1, 30)));
        return true;
    }
}
