using System;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    // A reloaded module must not reuse input-window state from its previous instance.
    private readonly string hotbarInputEpoch = Guid.NewGuid().ToString("N");

    private string HotbarInputWindowName(DragHotbar bar)
        => BarLabel(bar) + "###NativeDragHotbar-" + hotbarInputEpoch + "-" + bar.Id;
}
