using System.Reflection;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class TemplateCompatibilityChecks
{
    private const string Old = "3026f17c-0e2a-49fe-9842-6b41f2b2a2bf";
    private const string Current = "11763456-61fc-4b0e-9e27-7db51fd84797";
    internal static void Run(string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        var template = GameSettingsChecks.Fresh(); template.StructsId = Old;
        var bytes = JsonSerializer.Serialize(template, WorkshopProfiles.Json);
        void Preflight(WorkshopGameTemplate t, string schema = WorkshopGameRules.VerifiedKeySchema, int size = 11, int count = 512)
            => WorkshopGameRules.CheckKeyCompatibility(t, Current, schema, size, count);
        Preflight(template);
        check(JsonSerializer.Serialize(template, WorkshopProfiles.Json) == bytes,
            "verified old dependency build accepts identical key ABI without recapture or metadata mutation");
        Preflight(template, count: 600);
        check(true, "added unused target key slots do not invalidate known action identities");
        reject(() => Preflight(template, new string('A', 64)), "changed current key schema rejects legacy raw key bytes");
        reject(() => Preflight(template, size: 12), "actual key byte-size mismatch fails closed");
        reject(() => Preflight(template, count: 1), "requested key absent from current client fails before application");
        var unknown = WorkshopProfiles.Copy(template); unknown.StructsId = Guid.NewGuid().ToString("D");
        reject(() => Preflight(unknown), "unknown legacy build is never accepted based only on byte count");
        unknown.KeySchema = WorkshopGameRules.VerifiedKeySchema; Preflight(unknown);
        check(true, "new template fingerprint supports another build with identical full key layout and identities");
        unknown.KeySchema = "bad";
        reject(() => WorkshopGameRules.Validate(unknown), "malformed schema fingerprint cannot enter a profile");
        unknown.KeySchema = ""; unknown.Scope = new(true, true, false); unknown.Keys.Clear();
        Preflight(unknown, new string('B', 64), 20, 1);
        check(true, "layout and named options are not blocked by unrelated key ABI metadata when bindings are excluded");

        var takeover = WorkshopProfileChecks.Fresh();
        takeover.Shared[1] = true;
        foreach (var key in new[] { "0:1", "1:0" })
            takeover.EditedBars[key] = Enumerable.Range(0, 12).Select(i => new HotbarEntry
                { Mode = HotbarEntryMode.Command, Command = $"/echo 自定义 {key}-{i}\n/echo 第二行", NativeType = 1, NativeId = 119 }).ToList();
        takeover.Appearance[7].X = 789; takeover.Appearance[7].Y = 456;
        takeover.Bindings[0].PrimaryEdited = true; takeover.Bindings[0].Primary = new() { Key = 65, Modifiers = 2 };
        var library = new WorkshopLibrary();
        WorkshopProfiles.Add(library, new() { Takeover = WorkshopProfiles.Portable(takeover), GameSettings = template,
            Extensions = new() { Owner = "0000000000000000", Bars = [new() { Name = "测试", Columns = 1, Slots = [new()
                { Mode = HotbarEntryMode.Command, Command = "/echo 扩展指令" }] }] } }, "旧配置");
        var original = JsonSerializer.Serialize(library, WorkshopProfiles.Json);
        Preflight(library.Current.GameSettings!);
        var directory = Path.Combine(temp, "compatible-template-preservation");
        WorkshopLibraryStore.Save(directory, library, false);
        var loaded = WorkshopLibraryStore.Load(directory, 0x8877665544332211, true, out _, out _);
        check(JsonSerializer.Serialize(loaded, WorkshopProfiles.Json) == original,
            "compatibility check then durable save/load preserves entire profile including custom multiline commands, layout, macros, bindings and extensions");
        foreach (var actor in new[] { 0x1122334455667788UL, 0x8877665544332211UL })
        {
            var bound = WorkshopProfiles.Bind(loaded.Current, actor)!;
            check(JsonSerializer.Serialize(bound.EditedBars, WorkshopProfiles.Json) == JsonSerializer.Serialize(takeover.EditedBars, WorkshopProfiles.Json)
                && bound.Appearance[7].X == 789 && bound.Bindings[0].Primary.Key == 65,
                "binding another actor retains job/shared custom commands and independent coordinates");
        }
        reject(() => Preflight(library.Current.GameSettings!, new string('C', 64)), "incompatible template rejects without recapturing any hotbars");
        check(JsonSerializer.Serialize(library, WorkshopProfiles.Json) == original,
            "rejected compatibility leaves every existing profile byte unchanged");
        template.KeySchema = WorkshopGameRules.VerifiedKeySchema;
        library.Current.GameSettings!.KeySchema = template.KeySchema;
        var backup = WorkshopGameBackupStore.Save(directory, template);
        check(WorkshopGameBackupStore.Load(backup, template.Source).KeySchema == template.KeySchema
            && WorkshopProfileTransfer.Decode(WorkshopProfileTransfer.Encode(library.Current), loaded.Current.Extensions, true).Profile!.GameSettings!.KeySchema == template.KeySchema,
            "new stable key schema survives local backup and complete configuration export/import");
    }

    internal static void Compiled(Assembly assembly, Assembly structs, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var ns = "OmniToolbox.NativeDragHotbarLocal.";
        var module = assembly.GetType(ns + "NativeDragHotbar", true)!;
        var native = assembly.GetType(ns + "WorkshopGameNative", true)!;
        var actual = WorkshopGameKeySchema.Fingerprint(structs.GetType("FFXIVClientStructs.FFXIV.Client.System.Input.Keybind", true)!,
            structs.GetType("FFXIVClientStructs.FFXIV.Client.System.Input.InputId", true)!);
        check(actual == WorkshopGameRules.VerifiedKeySchema, "actual current dependency key ABI matches verified legacy dependency fingerprint");
        var preflight = ItemAndFontChecks.Calls(native.GetMethod("Preflight", flags)!).ToArray();
        check(preflight.Contains("CheckKeyCompatibility") && preflight.Contains("GetKeybind") && preflight.Contains("HudPlan"),
            "actual preflight checks stable key layout, requested target bindings and HUD merge capacity");
        var instance = Activator.CreateInstance(module)!;
        module.GetField("workshopReadOverwrite", flags)!.SetValue(instance, true);
        module.GetMethod("ClearWorkshopOperations", flags)!.Invoke(instance, null);
        check(!(bool)module.GetField("workshopReadOverwrite", flags)!.GetValue(instance)!,
            "cancel/profile/actor/tab cleanup resets destructive reread acknowledgement");
        check(!ItemAndFontChecks.Calls(module.GetMethod("SaveWorkshop", flags)!).Contains("Capture")
            && !ItemAndFontChecks.Calls(module.GetMethod("SaveWorkshop", flags)!).Contains("BeginSuperNativeRead"),
            "actual save path preserves workshop edits and never recaptures native bars");
    }
}
