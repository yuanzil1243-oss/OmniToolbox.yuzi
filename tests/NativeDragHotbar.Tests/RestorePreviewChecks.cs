using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class RestorePreviewChecks
{
    internal static void Run(Action<bool,string> check)
    {
        SuperHotbarDocument Fresh()=>new() {
            Shared=new bool[18], Appearance=Enumerable.Range(0,10).Select(_=>new DragHotbar()).ToList(),
            Groups=Enumerable.Range(0,2).Select(g=>new SuperSavedGroup {Index=g,
                Bars=Enumerable.Range(0,18).Select(_=>Enumerable.Range(0,16).Select(_=>new SuperSavedSlot()).ToList()).ToList()}).ToList(),
            Bindings=Enumerable.Range(57,120).Select(id=>new SuperBinding {InputId=id,NativeData=Convert.ToBase64String(new byte[11])}).ToList(),
            NativeOptions=new() {{"HotbarLock",0},{"HotbarCommon01",0}},ControlOptions=new(){{"HotbarDispEmpty",0}},
            Macros=[new() {Set=0,Index=0,Name="原生宏",Lines=Enumerable.Repeat("",15).ToList()}] };
        SuperHotbarDocument Clone(SuperHotbarDocument d)=>JsonSerializer.Deserialize<SuperHotbarDocument>(JsonSerializer.Serialize(d))!;
        byte[] Bytes(SuperBinding b) {
            var raw=Convert.FromBase64String(b.NativeData);
            if(b.PrimaryEdited){raw[0]=(byte)b.Primary.Key;raw[1]=(byte)b.Primary.Modifiers;}
            if(b.SecondaryEdited){raw[2]=(byte)b.Secondary.Key;raw[3]=(byte)b.Secondary.Modifiers;}
            return raw;
        }
        var all=new SuperRestoreScope(true,true,true,true,false);
        var current=Fresh();var target=Clone(current);target.Enabled=true;target.CapturedAt="different capture time";
        var same=SuperRestorePreview.Build(current,target,all,Bytes);
        check(same.Changes==0 && same.ComparedFields>400 && same.SameAs(SuperRestorePreview.Build(Clone(current),Clone(target),all,Bytes)),
            "restore preview compares actual fields, ignores capture/activation metadata and identifies no-op restores");
        target.Groups[1].Bars[2][4]=new(){Type=2,Id=1039876};
        target.Shared[2]=true;target.Bindings[3].Primary=new(){Key=49,Modifiers=2};target.Bindings[3].PrimaryEdited=true;
        target.Appearance[2].Visible=false;target.Appearance[2].X=700;
        target.ControlOptions["HotbarDispEmpty"]=1;target.Macros[0].Lines[0]="/echo changed";
        var original=JsonSerializer.Serialize(current);var source=JsonSerializer.Serialize(target);
        var preview=SuperRestorePreview.Build(current,target,all,Bytes);
        check(preview.Changes==6 && preview.Rows.Any(r=>r.Category=="内容"&&r.Location.Contains("职业组 1")&&r.AfterSlot?.Id==1039876)
            && preview.Rows.Any(r=>r.Category=="快捷键"&&r.After.Contains("Ctrl+1"))
            && preview.Rows.Any(r=>r.Category=="HUD 显示"&&r.After.Contains("隐藏")),
            "restore preview lists exact slot IDs, edited main/alternate bindings, share flags, HUD, options and macro changes");
        check(preview.SameAs(SuperRestorePreview.Build(Clone(current),Clone(target),all,Bytes))
            && original==JsonSerializer.Serialize(current) && source==JsonSerializer.Serialize(target),
            "preview is stable across fresh snapshots and never mutates either archive");
        current.Groups[0].Bars[0][0]=new(){Type=17,Id=84};
        check(!preview.SameAs(SuperRestorePreview.Build(current,target,all,Bytes)),
            "a native slot change after preview invalidates confirmation before any game write");
        var content=SuperRestorePreview.Build(current,target,new(true,false,false,false,false),Bytes);
        check(content.Rows.All(r=>r.Category is "内容" or "公用属性") && content.Rows.All(r=>r.Category!="HUD 显示"),
            "content/key restore cannot misleadingly claim to change unselected native HUD visibility or placement");
        target.Groups[1].Bars[17][15]=new(){Type=1,Id=119};
        var normal=SuperRestorePreview.Build(current,target,new(true,false,false,false,false),Bytes);
        var cross=SuperRestorePreview.Build(current,target,new(true,false,false,false,true),Bytes);
        check(cross.Changes==normal.Changes+1 && cross.Rows.Any(r=>r.Location.Contains("第 18 栏 / 格 16")),
            "cross scope expands exact saved fields while ordinary scope preserves excluded cross slots");
        var entry=new HotbarEntry {Command="/echo module-only",Mode=HotbarEntryMode.Command};
        target.EditedBars["0:1"]=Enumerable.Range(0,12).Select(_=>new HotbarEntry()).ToList();target.EditedBars["0:1"][0]=entry;
        check(SuperRestorePreview.Build(current,target,all,Bytes).UnsupportedCommands==1,
            "unsupported module-only command slots are explicitly counted before restoration");
        target.Bindings[7].NativeData=Convert.ToBase64String(new byte[]{0,0,0,0,7,0,0,0,0,0,0});
        check(SuperRestorePreview.Build(current,target,all,Bytes).Rows.Any(r=>r.After.Contains("完整原生绑定数据不同")),
            "opaque/gamepad binding differences remain visible even when displayed keyboard chords match");
        check(!SuperRestorePreview.OptionIncluded("HotbarCommon01",false,false)
            && !SuperRestorePreview.OptionIncluded("HotbarCrossDisp",false,true)
            && SuperRestorePreview.OptionIncluded("HotbarCrossDisp",true,true),
            "preview option scope matches shared-property and cross-option restoration boundaries");
    }
}
