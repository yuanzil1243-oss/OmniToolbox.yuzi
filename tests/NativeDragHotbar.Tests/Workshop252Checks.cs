using System.Numerics;
using System.Reflection;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class Workshop252Checks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var state = new TakeoverViewportState(); var reference = new Vector2(2560, 1440);
        check(!state.Update(true,70,reference,reference,0),"normal viewport starts visible");
        check(state.Update(true,70,reference,new(1791,1440),1),"either narrow width or short height hides takeover immediately");
        check(state.Update(true,70,reference,new(1850,1440),1000),"restore margin prevents flicker near hiding threshold");
        check(state.Update(true,70,reference,new(1920,1080),1100)
            && state.Update(true,70,reference,new(1920,1080),1349)
            && !state.Update(true,70,reference,new(1920,1080),1350),"restoration requires 250 ms of stable size above the hysteresis margin");
        check(state.Update(true,70,reference,new(2560,1000),1400),"height reduction alone hides takeover");
        state.Update(true,70,reference,reference,1500); state.Update(true,70,reference,new(2560,1000),1600);
        state.Update(true,70,reference,reference,1700);
        check(state.Update(true,70,reference,reference,1800)
            && !state.Update(false,70,reference,reference,1801),"shrinking resets restore timer and disabling the option clears transient hiding");
        state.Update(true,70,reference,new(1),2000);
        check(!state.Update(true,70,Vector2.Zero,new(1),2001),"unknown reference cannot strand bars in hidden state");
        check(TakeoverViewportRules.Intersects(new(90),new(110),Vector2.Zero,new(100))
            && !TakeoverViewportRules.Intersects(new(100),new(110),Vector2.Zero,new(100))
            && !TakeoverViewportRules.Intersects(new(-20),new(-1),Vector2.Zero,new(100))
            && TakeoverViewportRules.Intersects(new(-1900,100),new(-1800,200),new(-1920,0),new(1920,1080)),
            "viewport clipping allows partial intersections, rejects offscreen windows and respects negative monitor origin");
        var bar = new DragHotbar { X=1200, Y=900, Visible=false, AnchorX=null, AnchorY=null };
        var saved=JsonSerializer.Serialize(bar);
        for(var i=0;i<1000;i++)state.Update(true,70,reference,i%2==0?new(1200,600):reference,3000+i*100);
        check(saved==JsonSerializer.Serialize(bar),"temporary resizing never mutates saved positions or a manually hidden bar");
        var defaults=JsonSerializer.Deserialize<SuperHotbarDocument>("{}")!;
        var explicitValues=JsonSerializer.Deserialize<SuperHotbarDocument>("{\"BuiltInRepeat\":false,\"RepeatCps\":3}")!;
        check(defaults.BuiltInRepeat && defaults.RepeatCps==7,"missing repeat settings use enabled and seven CPS defaults");
        check(!explicitValues.BuiltInRepeat && explicitValues.RepeatCps==3,"saved repeat switch and speed override new defaults");
        check(defaults.HideInSmallWindow && defaults.SmallWindowPercent==70,"small-window takeover hiding defaults to seventy percent");
        var template=GameSettingsChecks.Fresh(); var original=WorkshopGameRules.Fingerprint(template);
        var layout=WorkshopGameRules.LayoutOnly(template);
        check(layout.Scope==new WorkshopGameScope(true,false,false,false) && layout.Keys.Count==0 && layout.Options.Count==0
            && layout.Windows.SequenceEqual(template.Windows) && layout.LocalHudSnapshot is null,
            "layout-only projection retains four-layout positions and excludes keys, character/system options and private rollback blobs");
        layout.Windows[0]=layout.Windows[0] with { X=77 };
        check(WorkshopGameRules.Fingerprint(template)==original,"preview projection cannot edit the saved source template");
        var updated=WorkshopGameRules.UpdateLayout(template,layout);
        check(updated.Windows[0].X==77 && updated.Keys.SequenceEqual(template.Keys) && updated.Options.SequenceEqual(template.Options)
            && updated.Id!=template.Id && WorkshopGameRules.Fingerprint(template)==original,
            "updating only layout preserves existing keys/options and changes template identity without recapturing hotbar commands");
        var noLayout=WorkshopProfiles.Copy(template); noLayout.Scope=noLayout.Scope with { Layout=false }; noLayout.Windows.Clear();
        reject(()=>WorkshopGameRules.LayoutOnly(noLayout),"layout application rejects templates that never captured layouts");
        var beforeLive=new List<WorkshopLiveHudWindow>{new("_ParameterWidget",100,200,1)};
        var liveCheck=new WorkshopLiveHudCheck(layout,template,beforeLive);
        check(WorkshopLiveHudRules.Report(liveCheck,beforeLive).Contains("尚未观察到")
            && WorkshopLiveHudRules.Report(liveCheck,beforeLive).Contains("_ParameterWidget"),
            "HUD readback explicitly warns when saved position changes but the live visible widget did not move");
        check(WorkshopLiveHudRules.Report(liveCheck,[new("_ParameterWidget",120,250,1.2f)]).Contains("1 个位置或缩放发生变化")
            && WorkshopLiveHudRules.Report(liveCheck,[]).Contains("没有可核对"),
            "HUD verification distinguishes observed node movement from unavailable windows instead of claiming cache equality proves a visual update");
        check(NativeIconVisualRules.ChargeRecast(0,0,2,0,45,17)==(45,17)
            && NativeIconVisualRules.ChargeRecast(0,0,2,1,45,17)==(0,0),
            "unavailable recast group preserves depleted native animation without a misleading partial-charge timer");
        foreach(var (elapsed,charges,percent,seconds,part) in new[]{
            (0f,0u,1,30,4),(15f,0u,50,15,4),(29.9f,0u,99,1,4),
            (30f,1u,1,30,1),(45f,1u,50,15,1),(60f,2u,0,0,1)})
        {
            var next=NativeIconVisualRules.ChargeRecast(60,elapsed,2,charges,99,99);
            var visual=new NativeSlotState(next.Percent,next.Seconds,charges,2,false,charges>0);
            check(next==(percent,seconds) && NativeIconVisualRules.RecastPartList(visual)==part
                && NativeIconVisualRules.Cooling(visual)==(charges<2),
                $"two-charge continuous depletion/recovery: elapsed={elapsed}, charges={charges}, atlas={part}");
        }
    }
    internal static void Compiled(Assembly assembly, Action<bool,string> check)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        var module=assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar",true)!;
        string[] Calls(string method)=>ItemAndFontChecks.Calls(module.GetMethod(method,flags)!).ToArray();
        check(Calls("DrawSuperBar").Contains("SetNextWindowViewport") && Calls("DrawSuperBar").Contains("Intersects")
            && !Calls("DrawBar").Contains("SetNextWindowViewport") && !Calls("DrawBar").Contains("Intersects"),
            "only takeover carriers get main-viewport pinning/culling; extension renderer stays independent");
        check(Calls("DrawSuperRestoreSettings").Contains("BuildSuperRestorePreview")
            && Calls("BuildSuperRestorePreview").Contains("SavedOtherLayout") && Calls("ConfirmSuperRestore").Contains("Apply"),
            "compiled restore page previews and applies saved non-hotbar HUD layouts");
        check(Calls("ConfirmSuperRestore").Contains("ApplyWorkshopGameTemplate")
            && Calls("BeginWorkshopGameCapture").Contains("Capture") && !Calls("BeginWorkshopGameCapture").Contains("ExitSuperTakeover"),
            "layout-only restore retains takeover through isolated game application and backup capture cannot exit takeover");
        check(Calls("UpdateWorkshopGameTemplate").Contains("Report") && Calls("UpdateSuperRestoreSaveWatch").Contains("Report"),
            "both application paths check loaded HUD nodes after allowing a frame refresh");
    }
}
