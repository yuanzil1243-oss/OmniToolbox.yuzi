using System;

namespace OmniToolbox.NativeDragHotbarLocal;

internal readonly record struct NativeSlotState(int CooldownPercent, int Seconds, uint Charges, uint MaxCharges, bool Highlighted, bool Usable)
{
    internal uint? ItemCount { get; init; }
    internal static NativeSlotState Neutral => new(0, 0, 0, 1, false, true);
}

internal static class NativeIconVisualRules
{
    // Total is the level-corrected full-charge recast, elapsed includes already restored charges.
    public static (int Percent, int Seconds) NextCharge(float total, float elapsed, uint maxCharges, uint charges)
    {
        if (!float.IsFinite(total) || !float.IsFinite(elapsed) || total <= 0 || elapsed < 0
            || maxCharges is < 1 or > 10 || charges >= maxCharges || elapsed >= total) return (0, 0);
        var duration = (double)total / maxCharges;
        var progress = Math.Max(0, elapsed - Math.Floor(elapsed / duration) * duration);
        // Snap float rounding at a charge boundary to the start of the NEXT missing charge.
        if (duration - progress < 0.0001 || progress < 0.0001) progress = 0;
        var remaining = duration - progress;
        return (Math.Clamp((int)(progress / duration * 100), 1, 99), (int)Math.Ceiling(remaining - 0.00001));
    }
    // ActionBar.uld: timelines 9 (ants 6..13), 11 (charge rings 1..79 / 82..160), 13 (ordinary 1..79).
    public static int AntsFrame(double seconds) => 6 + (int)(Math.Max(0, seconds) * 30 % 8);
    public static int RecastFrame(NativeSlotState state) => 1 + (int)Math.Clamp(state.CooldownPercent * 78 / 100f, 0, 78)
        + (state.MaxCharges > 1 && state.Charges == 0 ? 81 : 0);
    public static int RecastPartList(NativeSlotState state) => state.MaxCharges > 1 ? 1 : 4;
    public static bool ShowCharges(NativeSlotState state) => state.MaxCharges > 1;
    public static int ChargeDigit(NativeSlotState state) => (int)Math.Min(state.Charges, 9);
    public static bool Cooling(NativeSlotState state) => state.CooldownPercent is > 0 and <= 100;
}

internal sealed class NativeIconAnimation
{
    private ulong identity;
    private NativeSlotState prior;
    private double lastSeen = -10;
    private double flash = -10;
    private double pulse = -10;
    private ulong pulseIdentity;
    public void Trigger(ulong action, double seconds)
    {
        // Very high CPS cannot pin the feedback permanently at its first frame.
        if (pulseIdentity != action || seconds < pulse || seconds - pulse >= 0.10)
        { pulseIdentity = action; pulse = seconds; }
    }
    public float Pulse(ulong action, double seconds)
        => pulseIdentity != action || seconds < pulse ? 0 : (float)Math.Clamp(1 - (seconds - pulse) / 0.24, 0, 1);
    public float Observe(ulong action, NativeSlotState state, double seconds)
    {
        if (identity != action || seconds - lastSeen > 0.5 || seconds < lastSeen)
            flash = -10;
        else if (NativeIconVisualRules.Cooling(prior)
            && (!NativeIconVisualRules.Cooling(state) || state.MaxCharges > 1 && state.Charges > prior.Charges)) flash = seconds;
        identity = action; prior = state; lastSeen = seconds;
        return (float)Math.Clamp(1 - (seconds - flash) / 0.6, 0, 1);
    }
}
