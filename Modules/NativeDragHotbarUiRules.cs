namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarUiRules
{
    internal static bool Visible(bool uiVisible, bool barsHidden, bool cutscene, bool betweenAreas)
        => uiVisible && !barsHidden && !cutscene && !betweenAreas;

    internal static bool ExtensionsVisible(bool available, bool nativeVisible, bool followNativeHide)
        => available && (!followNativeHide || nativeVisible);
}
