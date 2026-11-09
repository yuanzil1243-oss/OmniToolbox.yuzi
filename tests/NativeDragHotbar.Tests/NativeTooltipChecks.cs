using System.Numerics;
using System.Reflection;
using OmniToolbox.NativeDragHotbarLocal;

internal static class NativeTooltipChecks
{
    internal static void Run(Action<bool,string> check)
    {
        foreach(var (type,id,kind,mask) in new (byte,uint,byte,byte)[]{(1,119,30,5),(2,4566,11,3),
            (2,1046003,11,3),(4,2000010,8,3),(9,100001,31,5),(10,4,32,5),(13,1,36,5),(17,1,41,5)})
            check(NativeHotbarDetail.TryCreate(type,id,out var result) && result.Kind==kind
                && result.Mask==mask && result.Id==id && result.Flags==0,
                $"native detail dispatcher preserves apparent identity {type}:{id}, kind {kind} and name/detail mask {mask}");
        foreach(var type in new byte[]{0,3,5,6,7,8,11,12,14,15,16,18,19,20,35,255})
            check(!NativeHotbarDetail.TryCreate(type,119,out _),$"unsupported/macro/transient type {type} cannot be guessed from its icon");
        foreach(var id in new uint[]{0,1000000,2000000,uint.MaxValue})
            check(!NativeHotbarDetail.TryCreate(2,id,out _),$"invalid item detail identity {id} rejected");
        check(NativeHotbarDetail.ValidBounds(new(30,20),new(70,60))
            && !NativeHotbarDetail.ValidBounds(new(float.NaN,0),new(40))
            && !NativeHotbarDetail.ValidBounds(Vector2.Zero,new(float.PositiveInfinity,40))
            && !NativeHotbarDetail.ValidBounds(new(20),new(10))
            && !NativeHotbarDetail.ValidBounds(Vector2.Zero,new(1025)),"native anchor rejects invalid and oversized geometry before native calls");
        var owner=new NativeHotbarTooltipOwnership();
        check(!owner.Active&&!owner.EndFrameNeedsRelease&&!owner.Expired(200),"empty ownership needs no native cleanup");
        owner.BeginFrame();owner.Mark(100,200,3,1000);
        check(owner.Owns(100,200,3)&&!owner.Owns(101,200,3)&&!owner.Owns(100,201,3)&&!owner.Owns(100,200,4),
            "cleanup requires exact manager, private anchor AND parent, so native bag/other-addon tooltips are preserved");
        check(!owner.EndFrameNeedsRelease&&!owner.Expired(1150)&&owner.Expired(1151),"seen hover survives frame and expires only after watchdog threshold");
        owner.BeginFrame();check(owner.EndFrameNeedsRelease,"unhover/hidden/HUD blocked slot releases at end of current draw frame");
        owner.Mark(100,200,3,1200);check(!owner.EndFrameNeedsRelease&&owner.Expired(1199),"new hover renews ownership while a reversed clock expires safely");
        owner.Clear();owner.Clear();check(!owner.Active&&!owner.Owns(100,200,3),"repeated stop and character change clear all tooltip ownership");
    }

    internal static void Compiled(Assembly assembly,Action<bool,string> check)
    {
        const string ns="OmniToolbox.NativeDragHotbarLocal.";
        var structs=assembly.GetType(ns+"NativeHotbarDetailTooltips",true)!.GetMethod("BuildArgs",BindingFlags.NonPublic|BindingFlags.Static)!;
        var calls=ItemAndFontChecks.Calls(structs).ToArray();
        check(calls.Contains("Ctor")&&!calls.Contains("SetPositionFloat"),"compiled native args invoke real constructor and do not override native detail position");
        var bridge=assembly.GetType(ns+"NativeHotbarBridge",true)!;
        calls=ItemAndFontChecks.Calls(bridge.GetMethod("TryDetail",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(calls.Contains("PrepareAppearance")&&calls.Contains("TryCreate")&&calls.Contains("GetRowOrDefault")
            && !calls.Any(x=>x.Contains("Execute")||x.Contains("Charges")),"compiled descriptions resolve current native appearance and validate sheets without invoking actions or charges");
        var module=assembly.GetType(ns+"NativeDragHotbar",true)!;
        calls=ItemAndFontChecks.Calls(module.GetMethod("DrawSlot",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(calls.Contains("TryNativeSlotTooltip")&&calls.Contains("DrawSlotTooltip"),"both bar kinds use native descriptions with text-only fallback for unsupported entries");
        check(ItemAndFontChecks.Calls(module.GetMethod("Draw",BindingFlags.NonPublic|BindingFlags.Instance)!).Contains("EndFrame")
            &&ItemAndFontChecks.Calls(module.GetMethod("UpdateSuper",BindingFlags.NonPublic|BindingFlags.Instance)!).Contains("Tick"),"draw cleanup and framework watchdog both release stale native descriptions");
        check(new[]{"ExitSuperTakeover","PauseSuper","EnsureSuper"}.All(name=>ItemAndFontChecks.Calls(module.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!).Contains("Release")),
            "exit, fault and character transitions explicitly release owned native help");
        var native=assembly.GetType(ns+"NativeHotbarDetailTooltips",true)!;
        calls=ItemAndFontChecks.Calls(native.GetMethod("Show",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(calls.Contains("ShowTooltip")&&!calls.Any(x=>x is "Set" or "SetPosition" or "Restore" or "Execute"),
            "native details are shown by game tooltip manager without changing game options/HUD position or executing slots");
        calls=ItemAndFontChecks.Calls(native.GetMethod("Release",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(calls.Contains("Owns")&&calls.Contains("HideTooltip")&&calls.Contains("Clear"),"compiled release checks ownership before calling native hide and then clears its bookkeeping");
    }
}
