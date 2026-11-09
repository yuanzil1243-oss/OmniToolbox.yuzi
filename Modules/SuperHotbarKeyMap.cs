using System;
using System.Collections.Generic;
using System.IO;

namespace OmniToolbox.SuperHotbarInterop;

// Native KeySetting.Key is a SE key index, not a Windows virtual key.
internal sealed class SuperHotbarKeyMap
{
    private readonly Dictionary<int,int> toNative = new() { [0] = 0 };
    private readonly Dictionary<int,int> toWindows = new() { [0] = 0 };
    public SuperHotbarKeyMap(IEnumerable<(int Windows, int Native)> pairs)
    {
        foreach (var (win, native) in pairs)
        {
            if (win is < 1 or > 254 || native is < 1 or > 255) continue;
            toNative[win] = native;
            toWindows.TryAdd(native, win);
        }
    }
    public int Windows(int native) => toWindows.TryGetValue(native, out var key) ? key
        : throw new InvalidDataException($"无法映射游戏键码 {native}，未更改原绑定。");
    public int Native(int windows) => toNative.TryGetValue(windows, out var key) ? key
        : throw new InvalidDataException($"游戏不支持此键码 {windows}，未写回绑定。");
    public bool TryNative(int windows, out int native) => toNative.TryGetValue(windows, out native);
}
