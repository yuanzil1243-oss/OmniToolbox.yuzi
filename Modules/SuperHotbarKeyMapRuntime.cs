using System.Collections.Generic;
using OmniToolbox.Host;

namespace OmniToolbox.SuperHotbarInterop;

internal static class SuperHotbarKeyMapRuntime
{
    private static SuperHotbarKeyMap? cached;
    public static SuperHotbarKeyMap Current
    {
        get
        {
            if (cached is not null) return cached;
            var pairs = new List<(int Windows, int Native)>();
            var state = DalamudServices.KeyState;
            for (var key = 8; key < 240; key++)
                if (state.TryGetSeVirtualKey(key, out var native)) pairs.Add((key, native));
            return cached = new(pairs);
        }
    }
}
