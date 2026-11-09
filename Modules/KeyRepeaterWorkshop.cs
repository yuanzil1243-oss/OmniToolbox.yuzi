using System;
using System.Collections.Generic;
using OmniToolbox.SuperHotbarInterop;

namespace OmniToolbox.KeyRepeaterOnline;

internal enum KeyRepeatMode { Native, Workshop, Unavailable }

// Subscribe from the standalone module only. Never load or alter the Workshop DLL.
internal sealed class KeyRepeatWorkshop(
    Func<int> providers, Func<bool> active, Func<int[]> bindings, Func<int, bool, bool> repeat)
{
    public KeyRepeatMode Mode { get; private set; } = KeyRepeatMode.Unavailable;
    public HashSet<int> Bindings { get; } = new();
    public int Skipped { get; private set; }
    private bool faulted;

    public void Reset()
    {
        faulted = false; Mode = KeyRepeatMode.Unavailable; Bindings.Clear(); Skipped = 0;
    }

    // Bit 0 = Active, bit 1 = Bindings, bit 2 = Repeat. Partial registration is
    // not evidence that takeover ended: pause instead of sending native input.
    public bool Refresh()
    {
        var previous = Mode;
        if (faulted) return false;
        try
        {
            var available = providers();
            if (available == 0) Mode = KeyRepeatMode.Native;
            else if (available != 7) Mode = KeyRepeatMode.Unavailable;
            else Mode = active() ? KeyRepeatMode.Workshop : KeyRepeatMode.Native;
            var current = new HashSet<int>();
            Skipped = 0;
            if (Mode == KeyRepeatMode.Workshop)
            {
                var exported = bindings() ?? throw new InvalidOperationException("工坊返回了空引用键位列表");
                foreach (var chord in new HashSet<int>(exported))
                {
                    // The published v1 execution endpoint rejects mouse VK 4/5/6.
                    // Do not bypass it with synthetic input or invoke the wrong native slot.
                    if (SuperHotbarRepeatProtocol.TryChord(chord, out _, out _)) current.Add(chord);
                    else Skipped++;
                }
            }
            var changed = previous != Mode || !Bindings.SetEquals(current);
            Bindings.Clear(); Bindings.UnionWith(current);
            return changed;
        }
        catch
        {
            Fail(); throw;
        }
    }

    public void Send(int chord, Action<int> native)
    {
        try
        {
            if (faulted) throw new InvalidOperationException("连点接口已暂停，请重新读取绑定");
            if (Mode == KeyRepeatMode.Native) { native(chord & 255); return; }
            // Physical input owns the first activation; only subsequent ticks use IPC.
            if (Mode == KeyRepeatMode.Workshop && Bindings.Contains(chord) && repeat(chord, false)) return;
            throw new InvalidOperationException("工坊连点接口不可用或拒绝执行");
        }
        catch
        {
            Fail(); throw;
        }
    }

    private void Fail()
    {
        faulted = true; Mode = KeyRepeatMode.Unavailable; Bindings.Clear(); Skipped = 0;
    }
}
