using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniToolbox.NativeDragHotbarLocal;

internal static class TakeoverPlacementChecks
{
    private const ulong A = 0x1122334455667788, B = 0x8877665544332211;
    private static string Geometry(SuperHotbarDocument document) => JsonSerializer.Serialize(document.Appearance.Select(bar =>
        new { bar.Id, bar.X, bar.Y, bar.Scale, bar.Columns, bar.Rows, bar.Visible, bar.Opacity, bar.HideEmpty }));
    private static WorkshopProfile Profile()
    {
        var source = WorkshopProfileChecks.Fresh();
        for (var i = 0; i < 10; i++)
        {
            var bar = source.Appearance[i]; bar.X = 85 + 79 * i; bar.Y = 150 + 43 * i;
            bar.Scale = i % 2 == 0 ? .8f : 1.2f; bar.Columns = new[] { 12, 6, 4, 3, 2, 1 }[i % 6];
            bar.Rows = 12 / bar.Columns; bar.Visible = i != 8; bar.Opacity = .25f;
        }
        return new() { Takeover = WorkshopProfiles.Portable(source) };
    }
    // An old follow=true is real legacy input, not a new setting that can remain enabled.
    private static WorkshopProfile LegacyFollowProfile()
    {
        var node = JsonSerializer.SerializeToNode(Profile(), WorkshopProfiles.Json)!;
        foreach (var bar in node["Takeover"]!["Appearance"]!.AsArray()) bar!["FollowNativeHud"] = true;
        return JsonSerializer.Deserialize<WorkshopProfile>(node.ToJsonString(), WorkshopProfiles.Json)!;
    }
    internal static void Run(string temp, Action<bool, string> check)
    {
        var profile = LegacyFollowProfile(); var original = Geometry(profile.Takeover!);
        foreach (var actor in new[] { A, B, 0x0102030405060708UL })
        {
            var bound = WorkshopProfiles.Bind(profile, actor)!;
            check(Geometry(bound) == original, "all ten saved takeover layouts survive binding to actor " + actor.ToString("X16"));
        }
        check(!JsonSerializer.Serialize(profile, WorkshopProfiles.Json).Contains("FollowNativeHud"),
            "old follow=true is ignored on load and cannot return in exported or saved configuration");
        var folder = Path.Combine(temp, "placement"); var library = new WorkshopLibrary();
        var selected = WorkshopProfiles.Add(library, profile, "位置测试");
        WorkshopLibraryStore.Save(folder, library, false);
        var loaded = WorkshopLibraryStore.Load(folder, B, true, out _, out _);
        check(Geometry(WorkshopProfiles.Bind(loaded.Current, B)!) == original,
            "source actor geometry persists through durable library save/reload on another account");
        var sourceRuntime = WorkshopProfiles.Bind(loaded.Current, A)!;
        sourceRuntime.Appearance[7].X = 1701; sourceRuntime.Appearance[7].Y = 93;
        sourceRuntime.Appearance[7].Scale = .9f; sourceRuntime.Appearance[7].Columns = 2; sourceRuntime.Appearance[7].Rows = 6;
        loaded.Current.Takeover = WorkshopProfiles.Portable(sourceRuntime);
        WorkshopLibraryStore.Save(folder, loaded, false);
        var next = WorkshopLibraryStore.Load(folder, B, true, out _, out _);
        check(Geometry(WorkshopProfiles.Bind(next.Current, B)!) == Geometry(sourceRuntime),
            "moving/resizing a source actor takeover bar becomes the same saved placement on the next actor");
        var encoded = WorkshopProfileTransfer.Encode(next.Current);
        var imported = WorkshopProfileTransfer.Decode(encoded, new(), true).Profile!;
        check(Geometry(WorkshopProfiles.Bind(imported, B)!) == Geometry(sourceRuntime),
            "configuration clipboard export/import carries every takeover position, shape and scale");
        var alternate = WorkshopProfiles.Add(next, Profile(), "另一套位置");
        alternate.Takeover!.Appearance[0].X = 1200; var alternateGeometry = Geometry(alternate.Takeover);
        next.SelectedId = loaded.SelectedId;
        check(Geometry(WorkshopProfiles.Bind(next.Current, B)!) == Geometry(sourceRuntime)
            && alternateGeometry != Geometry(sourceRuntime), "switching selected configurations retains independent placement sets");
        var old = Profile(); old.Takeover!.NativeGeometryVersion = 0;
        var before = Geometry(old.Takeover); var expected = old.Takeover.Appearance.Select(bar => NativeBarGeometry.FromRoot(
            new(bar.X, bar.Y), bar.Scale, bar.Columns, bar.Rows)).ToArray();
        var oldA = WorkshopProfiles.Bind(old, A)!; var oldB = WorkshopProfiles.Bind(old, B)!;
        check(oldA.NativeGeometryVersion == 1 && Geometry(oldA) == Geometry(oldB) && Geometry(old.Takeover) == before
            && oldA.Appearance.Select((bar, i) => NativeBarGeometry.FromSaved(bar) == expected[i]).All(v => v),
            "legacy root-coordinate archives convert from their own saved positions identically on every actor, without recapture");
        check(typeof(DragHotbar).GetProperty("FollowNativeHud") is null, "takeover layout has no live native-follow setting");
    }
    internal static void Compiled(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        const string ns = "OmniToolbox.NativeDragHotbarLocal.";
        var type = assembly.GetType(ns + "NativeDragHotbar", true)!;
        string[] Calls(string name) => ItemAndFontChecks.Calls(type.GetMethod(name, flags)!).ToArray();
        check(!Calls("DrawSuperBar").Contains("Read") && !Calls("DrawSuperBar").Contains("Store")
            && !Calls("UpgradeSuperGeometry").Contains("Read"),
            "actual compiled draw and automatic migration cannot overwrite saved geometry by reading native addons");
        var native = assembly.GetType(ns + "SuperNativeArchive", true)!;
        check(ItemAndFontChecks.Calls(native.GetMethod("CaptureAppearance", flags)!).Contains("Read")
            && Calls("ConfirmSuperAppearanceRepair").Contains("CaptureAppearance")
            && !Calls("BeginSuperAppearanceRepair").Contains("CaptureAppearance"),
            "explicit confirmed native layout import remains available without restoring automatic following");
        var library = new WorkshopLibrary(); WorkshopProfiles.Add(library, LegacyFollowProfile(), "编译产物位置测试");
        var module = Activator.CreateInstance(type)!;
        var libraryField = type.GetField("workshopLibrary", flags)!;
        var compiledLibrary = JsonSerializer.Deserialize(JsonSerializer.Serialize(library, WorkshopProfiles.Json), libraryField.FieldType)!;
        libraryField.SetValue(module, compiledLibrary);
        string RuntimeGeometry()
        {
            var saved = type.GetField("super", flags)!.GetValue(module)!;
            return Geometry(JsonSerializer.Deserialize<SuperHotbarDocument>(JsonSerializer.Serialize(saved, saved.GetType()), WorkshopProfiles.Json)!);
        }
        var original = Geometry(library.Current.Takeover!);
        foreach (var actor in new[] { A, 0UL, B, A })
        {
            type.GetField("owner", flags)!.SetValue(module, actor);
            type.GetMethod("BindWorkshopCharacter", flags)!.Invoke(module, null);
            if (actor == 0) check(type.GetField("super", flags)!.GetValue(module) is null, "actual logout leaves the selected saved geometry intact");
            else
            {
                check(RuntimeGeometry() == original, "actual login/account switch binds the same ten saved bar geometries");
                type.GetMethod("UpgradeSuperGeometry", flags)!.Invoke(module, null);
                check(RuntimeGeometry() == original, "actual framework geometry upgrade preserves current client-pixel positions");
            }
        }
        check(!JsonSerializer.Serialize(compiledLibrary, libraryField.FieldType).Contains("FollowNativeHud"),
            "compiled legacy configuration binding cannot reintroduce removed follow behavior");
    }
}
