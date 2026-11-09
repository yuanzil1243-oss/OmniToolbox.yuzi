using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;
using OmniToolbox.SuperHotbarInterop;

internal static class ItemAndFontChecks
{
    internal static void Run(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (var (item, icon, expected) in new (uint, uint, uint)[] {
            (1037844,1020708,20708), (1039731,1020708,20708),
            (1046003,1024051,24051), (1039876,1024097,24097), (4566,20643,20643) })
        {
            var image = NativeHotbarItems.Icon(icon);
            check(image.Id == expected && image.Hq == (item > 1_000_000)
                && NativeHotbarItems.TryItem(item, out var baseId, out var hq)
                && baseId == item % 1_000_000 && hq == image.Hq,
                $"real food/potion sample {item} decodes item identity and HQ image independently");
            check(NativeHotbarItems.SelectIcon(item, 0, expected) == icon
                && NativeHotbarItems.SelectIcon(item, 234001, expected) == icon
                && NativeHotbarItems.SelectIcon(item, icon, 1) == icon,
                $"real sample {item} recovers missing/placeholder image from Item sheet and preserves native icons");
        }
        foreach (var invalid in new uint[] {0,1_000_000,2_000_000,uint.MaxValue})
            check(!NativeHotbarItems.TryItem(invalid,out _,out _)
                && !NativeHotbarSafety.CanReadItemState(2,invalid,true), $"invalid item {invalid} cannot reach native state reader");
        check(!NativeHotbarSafety.CanReadItemState(1,4566,true)
            && !NativeHotbarSafety.CanReadItemState(2,4566,false)
            && NativeHotbarSafety.CanReadItemState(2,1046003,true)
            && !NativeHotbarSafety.CanReadCombatState(2,1046003,true),
            "HQ item recast/count path requires Item row and remains isolated from combat charges");
        foreach (var quantity in new uint[] {0,1,118,275,9999,uint.MaxValue})
        {
            var state=NativeHotbarItems.State(40,123,quantity);
            check(state.ItemCount==quantity && state.Seconds==123 && state.CooldownPercent==40
                && state.MaxCharges==1 && !NativeIconVisualRules.ShowCharges(state)
                && NativeIconVisualRules.Cooling(state) && state.Usable==(quantity>0)
                && NativeHotbarItems.CountText(quantity)=="x"+quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"quantity {quantity} survives item CD state without becoming charges");
        }
        var clamped=NativeHotbarItems.State(-10,-2,0);
        check(clamped.CooldownPercent==0 && clamped.Seconds==0 && !NativeIconVisualRules.Cooling(clamped)
            && NativeHotbarItems.State(150,3,1).CooldownPercent==100 && NativeSlotState.Neutral.ItemCount is null,
            "invalid CD bounds clamp and neutral/nonitem entries never acquire inventory labels");

        var data=JsonSerializer.Deserialize<SuperHotbarDocument>("{\"KeyboardEncodingVersion\":1,\"AppearanceDefaultsVersion\":1,\"FollowNativeEmptySlots\":true}")!;
        data.Appearance=Enumerable.Range(0,10).Select(_=>new DragHotbar {HideEmpty=false}).ToList();
        data.ControlOptions["HotbarDispEmpty"]=0;
        var map=new SuperHotbarKeyMap([(49,1)]);
        check(SuperHotbarRules.UpgradePresentation(data,map) && data.EmptySlotDefaultsVersion==1
            && data.Appearance.All(b=>b.HideEmpty) && data.ControlOptions["HotbarDispEmpty"]==0,
            "legacy system-follow archives migrate all ten bars to hidden empty cells without changing restore options");
        data.Appearance[3].HideEmpty=false;
        check(!SuperHotbarRules.UpgradePresentation(data,map) && !data.Appearance[3].HideEmpty
            && !JsonSerializer.Serialize(data).Contains("FollowNativeEmptySlots"),
            "empty-cell migration runs once and per-bar choices persist; retired follow option is omitted");
        var source=File.ReadAllText(Path.Combine(root,"Modules/NativeDragHotbarSettings.cs"));
        check(!source.Contains("跟随游戏原本的空格显示选项") && source.Contains("隐藏本栏空格"),
            "settings expose the per-bar visual choice with no system-follow switch");
        check(NativeHotbarTextStyles.Default.Shortcut==new NativeHotbarFontStyle(1,12)
            && NativeHotbarTextStyles.Default.Timer==new NativeHotbarFontStyle(0,12)
            && NativeHotbarTextStyles.Default.Quantity==new NativeHotbarFontStyle(0,12)
            && !new NativeHotbarFontStyle(6,12).Valid && !new NativeHotbarFontStyle(0,0).Valid,
            "font defaults match read-only native nodes and reject invalid families/sizes");
        check(NativeHotbarItems.State(0,0,628,false).Usable==false
            && NativeHotbarItems.State(0,0,628,false).ItemCount==628,
            "regional item restrictions darken icons without hiding available inventory counts");
        check(new NativeHotbarFontStyle(0,12).Pixels()==16 && new NativeHotbarFontStyle(0,12).Pixels(1.1f)==18
            && new NativeHotbarFontStyle(0,12).Pixels(2)==32,
            "native point sizes convert to integer physical-pixel rasters at actual hotbar scale");
    }

    internal static void Compiled(Assembly assembly,Action<bool,string> check)
    {
        const string ns="OmniToolbox.NativeDragHotbarLocal.";
        var fonts=assembly.GetType(ns+"NativeHotbarGameFonts",true)!;
        var family=fonts.GetMethod("Family",BindingFlags.NonPublic|BindingFlags.Static)!;
        check(Enumerable.Range(0,6).Select(i=>family.Invoke(null,[(byte)i])!.ToString())
            .SequenceEqual(new[]{"Axis","MiedingerMid","Meidinger","TrumpGothic","Jupiter","JupiterNumeric"}),
            "compiled native FontType mapping matches actual current Dalamud GameFontFamily enum");
        var instance=Activator.CreateInstance(fonts)!;
        var style=Activator.CreateInstance(assembly.GetType(ns+"NativeHotbarFontStyle",true)!,[(byte)0,(byte)12]);
        check(fonts.GetMethod("Push",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(instance,[style]) is null,
            "game fonts safely retain existing font before atlas startup");
        ((IDisposable)instance).Dispose();((IDisposable)instance).Dispose();
        check(true,"font manager dispose is safe before startup and on repeated stop");
        var bridge=assembly.GetType(ns+"NativeHotbarBridge",true)!;
        var calls=Calls(bridge.GetMethod("ItemState",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(calls.Contains("GetSlotActionCooldownPercentage") && calls.Contains("GetCostValueForSlot")
            && !calls.Any(n=>n.Contains("Charges")||n.Contains("CombatState")),
            "compiled ItemState calls native item cooldown/count readers with no combat charge getter");
        var pushCalls=Calls(fonts.GetMethod("PushPixels",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(pushCalls.Contains("Push") && !pushCalls.Contains("get_ImFont"),
            "compiled game text uses managed scoped font locks rather than caching unlocked pointers");
        var tooltipStyle=fonts.GetProperty("TooltipStyle",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        check((byte)tooltipStyle.GetType().GetProperty("Family")!.GetValue(tooltipStyle)! == 0
            && (byte)tooltipStyle.GetType().GetProperty("Size")!.GetValue(tooltipStyle)! == 14,
            "slot tooltip uses full-size Axis game body font, independent of icon shortcut font");
        var names=assembly.GetType(ns+"NativeHotbarNames",true)!;
        var plain=names.GetMethod("Plain",BindingFlags.NonPublic|BindingFlags.Static)!;
        var name="鱼子酱佐小饼干";
        var decorated="\u0002\u0013\u0002\u0005\u0003"+name+"\u0002\u0013\u0002\u0001\u0003";
        check((string)plain.Invoke(null,[decorated])! == name,
            "encoded colour payloads are decoded as SeString and never exposed as item-name symbols");
        var literal="药品 (HQ) +1 / 50% · Café — 药品";
        check((string)plain.Invoke(null,[literal])! == literal,
            "ordinary multilingual names and legitimate punctuation remain unchanged");
        var tooltip=assembly.GetType(ns+"NativeDragHotbar",true)!.GetMethod("DrawSlotTooltip",BindingFlags.NonPublic|BindingFlags.Instance)!;
        check(Calls(tooltip).Contains("PushTooltip") && Calls(tooltip).Contains("Tooltip")
            && Calls(assembly.GetType(ns+"NativeDragHotbar",true)!.GetMethod("DrawSlot",BindingFlags.NonPublic|BindingFlags.Instance)!).Contains("DrawSlotTooltip"),
            "native and extension DrawSlot share scoped game-font tooltip and plain-text resolver");
        check(calls.Contains("IsSlotUsable")
            && Calls(bridge.GetMethod("AvailabilityState",BindingFlags.NonPublic|BindingFlags.Instance)!).Contains("IsSlotUsable")
            && !Calls(bridge.GetMethod("AvailabilityState",BindingFlags.NonPublic|BindingFlags.Instance)!).Any(n=>n.Contains("Charges")),
            "compiled item and mount/other-slot paths use native regional usability without charge calls");
        var module=assembly.GetType(ns+"NativeDragHotbar",true)!;
        var describeCalls=Calls(bridge.GetMethod("Describe",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(describeCalls.Contains("ItemName") && describeCalls.Contains("ExtractText")
            && !describeCalls.Contains("ToString"),
            "compiled native capture reads canonical item names or decoded SeString, never raw pointer ToString");
        var lockCalls=Calls(module.GetMethod("DrawSuperBar",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(!lockCalls.Contains("SetTooltip") && lockCalls.Contains("InvisibleButton") && lockCalls.Contains("SetSuperLocked"),
            "compiled takeover lock keeps its button and toggle behavior without a hover text tooltip");
        var confirmCalls=Calls(module.GetMethod("ConfirmSuperRestore",BindingFlags.NonPublic|BindingFlags.Instance)!).ToArray();
        check(Array.IndexOf(confirmCalls,"Capture")<Array.IndexOf(confirmCalls,"SameAs")
            && Array.IndexOf(confirmCalls,"SameAs")<Array.IndexOf(confirmCalls,"ExitSuperTakeover")
            && Array.IndexOf(confirmCalls,"ExitSuperTakeover")<Array.IndexOf(confirmCalls,"Restore"),
            "compiled restore confirmation captures and checks stale-preview state before leaving takeover or writing");
        var saveArgs=new List<bool>();
        _=Calls(assembly.GetType(ns+"SuperNativeArchive",true)!.GetMethod("Restore")!,
            (called,previous)=>{if(called.Name=="SaveFile")saveArgs.Add(previous==OpCodes.Ldc_I4_1);}).ToArray();
        check(saveArgs.Count==3 && saveArgs.All(v=>v),
            "compiled hotbar/keybind/macro restoration explicitly requests forced native file saving");
    }
    internal static IEnumerable<string> Calls(MethodInfo method,Action<MethodBase,OpCode>? inspect=null)
    {
        var opcodes=typeof(OpCodes).GetFields().Where(f=>f.FieldType==typeof(OpCode))
            .Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>(ushort)o.Value);
        var il=method.GetMethodBody()!.GetILAsByteArray()!;
        var previous=default(OpCode);
        for(var i=0;i<il.Length;)
        {
            ushort code=il[i++];if(code==0xfe)code=(ushort)(0xfe00|il[i++]);var op=opcodes[code];
            if(op.OperandType==OperandType.InlineMethod)
            {
                var called=method.Module.ResolveMethod(BitConverter.ToInt32(il,i))!;
                inspect?.Invoke(called,previous);yield return called.Name;
            }
            i+=op.OperandType switch {
                OperandType.InlineNone=>0,OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,
                OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),_=>4};
            previous=op;
        }
    }
}

