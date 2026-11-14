using System;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private bool restoreHudLayout = true;
    private WorkshopGameTemplate? superRestoreLayoutTarget, superRestoreLayoutBefore;
    private string superRestoreLayoutBackupPath = "";

    private bool HasSavedOtherLayout => workshopLibrary?.Current.GameSettings is { Scope.Layout: true } saved
        && saved.Windows.Exists(w => w.Layout == saved.CurrentLayout);

    private WorkshopGameTemplate SavedOtherLayout() => WorkshopGameRules.LayoutOnly(
        workshopLibrary?.Current.GameSettings ?? throw new InvalidOperationException("请先读取其他界面布局。"));

}
