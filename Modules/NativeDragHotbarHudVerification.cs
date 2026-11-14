using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed record WorkshopLiveHudWindow(string Name, short X, short Y, float Scale);
internal sealed record WorkshopLiveHudCheck(WorkshopGameTemplate Target, WorkshopGameTemplate Before,
    List<WorkshopLiveHudWindow> Windows);

internal static class WorkshopLiveHudRules
{
    internal static string Report(WorkshopLiveHudCheck check, List<WorkshopLiveHudWindow> after)
    {
        var saved = check.Before.Windows.Where(w => w.Layout == check.Before.CurrentLayout).ToDictionary(w => w.Name);
        var prior = check.Windows.ToDictionary(w => w.Name);
        var live = after.ToDictionary(w => w.Name);
        var expected = check.Target.Windows.Where(w => w.Layout == check.Target.CurrentLayout
            && prior.ContainsKey(w.Name) && (!saved.TryGetValue(w.Name, out var old)
                || Math.Abs(w.X-old.X)>0.01f || Math.Abs(w.Y-old.Y)>0.01f || Math.Abs(w.Scale-old.Scale)>0.001f)).ToArray();
        var unchanged = expected.Where(w => live.TryGetValue(w.Name, out var now) && prior.TryGetValue(w.Name,out var old)
            && now.X==old.X && now.Y==old.Y && Math.Abs(now.Scale-old.Scale)<0.001f).Select(w => w.Name).ToArray();
        if (unchanged.Length>0) return "配置已写入，但尚未观察到这些已显示界面的位置或缩放变化："
            + string.Join("、",unchanged.Take(6)) + "。请核对实际界面；配置读回通过不代表画面已更新。";
        var moved = after.Count(w => prior.TryGetValue(w.Name,out var old)
            && (w.X!=old.X || w.Y!=old.Y || Math.Abs(w.Scale-old.Scale)>0.001f));
        return after.Count==0 ? "当前没有可核对的已显示 HUD，请打开相应界面核对位置。"
            : $"已检查 {after.Count} 个已显示 HUD，其中 {moved} 个位置或缩放发生变化；请核对是否符合保存的布局。";
    }
}
