using System.Reflection;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class Workshop253Checks
{
    internal static void Run(Action<bool,string> check)
    {
        var saved = GameSettingsChecks.Fresh();
        saved.Scope = new(true,true,true,true);
        saved.Options.Add(new("System","SoundMaster","UInt",70));
        var original = JsonSerializer.Serialize(saved,WorkshopProfiles.Json);
        for(var mask=1;mask<16;mask++)
        {
            var update = WorkshopProfiles.Copy(saved);
            update.Id=Guid.NewGuid().ToString("N"); update.Source="8877665544332211";
            update.Scope=new((mask&1)!=0,(mask&2)!=0,(mask&4)!=0,(mask&8)!=0);
            update.CurrentLayout=2;
            update.Windows=update.Scope.Layout ? update.Windows.Select(w=>w with { X=87 }).ToList() : [];
            update.Options=update.Options.Where(o=>o.Section=="System" ? update.Scope.System : update.Scope.Character)
                .Select(o=>o with { UInt=2 }).ToList();
            update.Keys=update.Scope.Bindings ? [new(1,Convert.ToBase64String(Enumerable.Repeat((byte)3,11).ToArray()))] : [];
            update.KeySchema=update.Scope.Bindings ? new string('A',64) : "";
            var snapshot=JsonSerializer.Serialize(update,WorkshopProfiles.Json);
            var merged=WorkshopGameRules.UpdateSelected(saved,update);
            check(merged.Scope==saved.Scope && merged.Id!=saved.Id && merged.Source==update.Source
                && merged.CurrentLayout== (update.Scope.Layout ? 2 : 0)
                && merged.Windows.SequenceEqual(update.Scope.Layout ? update.Windows : saved.Windows)
                && merged.Keys.SequenceEqual(update.Scope.Bindings ? update.Keys : saved.Keys)
                && merged.KeySchema==(update.Scope.Bindings ? update.KeySchema : saved.KeySchema)
                && merged.Options.Where(o=>o.Section!="System").SequenceEqual((update.Scope.Character ? update : saved).Options.Where(o=>o.Section!="System"))
                && merged.Options.Where(o=>o.Section=="System").SequenceEqual((update.Scope.System ? update : saved).Options.Where(o=>o.Section=="System")),
                $"selective backup mask {mask} updates only checked scopes and preserves all other settings");
            check(JsonSerializer.Serialize(saved,WorkshopProfiles.Json)==original
                && JsonSerializer.Serialize(update,WorkshopProfiles.Json)==snapshot,
                $"selective backup mask {mask} never modifies either source or preview");
            var first=WorkshopGameRules.UpdateSelected(null,update);
            first.Windows.Clear();first.Keys.Clear();first.Options.Clear();
            check(JsonSerializer.Serialize(update,WorkshopProfiles.Json)==snapshot,$"first backup mask {mask} is an independent copy");
        }
        var layout=WorkshopGameRules.LayoutOnly(saved);layout.LocalHudSnapshot=[];
        check(WorkshopGameRules.UpdateSelected(null,layout).LocalHudSnapshot is null,
            "portable selected-scope backup strips character-local rollback snapshot");
    }
    internal static void Compiled(Assembly assembly,Action<bool,string> check)
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var type=assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar",true)!;
        string[] Calls(string name)=>ItemAndFontChecks.Calls(type.GetMethod(name,flags)!).ToArray();
        var archive=Calls("DrawWorkshopArchiveSettings");var restore=Calls("DrawSuperRestoreSettings");
        check(archive.Contains("DrawWorkshopGameCaptureSettings") && archive.Contains("DrawWorkshopGameBackupDetails")
            && !archive.Contains("BeginWorkshopGameApply") && !archive.Contains("BeginWorkshopGameRestore")
            && !archive.Contains("DrawWorkshopGameWriteActions"),
            "archive page consolidates saving and reading without direct manual game-write actions");
        check(restore.Contains("DrawWorkshopGameWriteActions") && !restore.Contains("BeginWorkshopGameCapture")
            && !restore.Contains("DrawWorkshopGameCaptureSettings") && type.GetMethod("DrawOtherLayoutActions",flags) is null,
            "restore page contains only write/preview/revert actions and removes duplicate layout read/apply buttons");
        check(type.GetNestedTypes(BindingFlags.NonPublic).SelectMany(t=>t.GetMethods(BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static))
            .Where(m=>m.Name.Contains("<DrawWorkshopGameOperation>")).Any(m=>ItemAndFontChecks.Calls(m).Contains("UpdateSelected")),
            "actual confirmation commits selective backup merge instead of replacing unchecked saved scopes");
    }
}
