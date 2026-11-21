using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class ExtensionHide257Checks
{
    internal static void Run(string temp, Action<bool, string> check)
    {
        for (var mask = 0; mask < 8; mask++)
        {
            var available = (mask & 1) != 0;
            var native = (mask & 2) != 0;
            var follow = (mask & 4) != 0;
            check(NativeHotbarUiRules.ExtensionsVisible(available, native, follow)
                == (available && (native || !follow)), "extension follow-native visibility: " + mask);
        }
        check(new HotbarProfile().FollowNativeHide
            && JsonSerializer.Deserialize<HotbarProfile>("{}")!.FollowNativeHide,
            "new and legacy extension profiles follow native hiding by default");
        foreach (var follow in new[] { false, true })
        {
            var profile = new WorkshopProfile { Extensions = new() { Owner = "0000000000000000", FollowNativeHide = follow } };
            profile.Extensions.Bars[0].Visible = false;
            var library = new WorkshopLibrary { Profiles = [profile], SelectedId = profile.Id };
            var directory = Path.Combine(temp, "extension-hide-" + follow);
            Directory.CreateDirectory(directory);
            WorkshopLibraryStore.Save(directory, library, false);
            var loaded = WorkshopLibraryStore.Load(directory, 99, true, out _, out _);
            check(loaded.Current.Extensions.FollowNativeHide == follow && !loaded.Current.Extensions.Bars[0].Visible,
                "follow-native and manual hidden bar survive cross-character reload: " + follow);
            var copy = WorkshopProfiles.Add(loaded, loaded.Current, "副本");
            check(copy.Extensions.FollowNativeHide == follow, "profile copy retains follow-native preference: " + follow);
            var decoded = WorkshopProfileTransfer.Decode(WorkshopProfileTransfer.Encode(profile), new(), true).Profile!;
            check(decoded.Extensions.FollowNativeHide == follow,
                "complete profile backup round-trips follow-native preference: " + follow);
            var exported = HotbarTransfer.Export(profile.Extensions, null, true, "2.5.7");
            var imported = HotbarTransfer.Parse(exported);
            check(imported.FollowNativeHide == follow, "all-extension export carries follow-native: " + follow);
            check(!HotbarTransfer.Export(profile.Extensions, 0, true, "2.5.7").Contains("FollowNativeHide"),
                "single-bar export omits the global follow-native setting: " + follow);
            foreach (var replace in new[] { false, true })
            foreach (var globals in new[] { false, true })
            {
                var target = new WorkshopProfile { Extensions = new() { Owner = "0000000000000000", FollowNativeHide = !follow } };
                WorkshopProfiles.ImportExtensions(target, imported, replace, globals);
                check(target.Extensions.FollowNativeHide == (globals ? follow : !follow),
                    $"extension import respects selected global scope: {follow}/{replace}/{globals}");
                imported.FollowNativeHide = null;
                target = new() { Extensions = new() { Owner = "0000000000000000", FollowNativeHide = follow } };
                WorkshopProfiles.ImportExtensions(target, imported, replace, globals);
                check(target.Extensions.FollowNativeHide == follow,
                    $"legacy extension import preserves existing follow preference: {follow}/{replace}/{globals}");
                imported.FollowNativeHide = follow;
            }
            profile.Extensions.Show = false;
            var before = JsonSerializer.Serialize(profile);
            _ = NativeHotbarUiRules.ExtensionsVisible(true, false, follow);
            check(JsonSerializer.Serialize(profile) == before && !profile.Extensions.Show && !profile.Extensions.Bars[0].Visible,
                "temporary native hide never rewrites saved bar visibility: " + follow);
        }
        Fallback(check);
    }

    private static void Fallback(Action<bool, string> check)
    {
        var source = new FrameSource();
        var builder = new Builder(source);
        var other = 0; var ours = 0; var errors = 0;
        source.Draw += () => other++;
        using var fallback = new NativeHotbarDrawFallback();
        check(fallback.TryStart(builder, () => ours++, out var error) && fallback.Available && error.Length == 0,
            "fallback attaches to the shared frame source through the inherited private field");
        source.Raise();
        check(ours == 1 && other == 1 && !builder.DisableAutomaticUiHide,
            "fallback receives one frame without changing host flags or other subscribers");
        check(fallback.TryStart(builder, () => ours++, out error), "fallback can restart without duplicate callbacks");
        source.Raise();
        check(ours == 2 && other == 2, "fallback restart removes the old subscription");
        fallback.Dispose(); fallback.Dispose(); source.Raise();
        check(!fallback.Available && ours == 2 && other == 3 && source.Subscribers == 1,
            "idempotent fallback disposal preserves unrelated frame callbacks");
        check(!fallback.TryStart(new object(), () => ours++, out error) && error.Length > 0 && !fallback.Available,
            "missing frame metadata fails closed with an explicit reason");
        check(!fallback.TryStart(new Builder(new WrongSource()), () => ours++, out error) && !fallback.Available,
            "incompatible frame event type is rejected");
        fallback.TryStart(builder, () => throw new InvalidOperationException("draw error"), out _, _ => errors++);
        source.Raise(); source.Raise();
        check(!fallback.Available && errors == 1 && other == 5,
            "a fallback error pauses its own callback once while the host continues drawing");
        fallback.TryStart(builder, () => throw new Exception(), out _, _ => throw new Exception("reporter"));
        source.Raise();
        check(!fallback.Available, "even error reporting cannot escape into the shared frame event");
        fallback.Dispose();
        var stubborn = new FrameSource { RefuseRemove = true };
        var stubbornBuilder = new Builder(stubborn);
        fallback.TryStart(stubbornBuilder, () => ours++, out _, _ => errors++);
        fallback.Dispose(); stubborn.Raise();
        check(ours == 2 && !fallback.Available, "a host that refuses removal retains only an inert callback");
        fallback.TryStart(stubbornBuilder, () => ours++, out _); stubborn.Raise();
        check(ours == 3, "a stale callback cannot reactivate after a later restart");
    }

    internal static void Compiled(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var module = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar", true)!;
        string[] Calls(string name) => ItemAndFontChecks.Calls(module.GetMethod(name, flags)!).ToArray();
        check(Calls("Draw").Contains("ExtensionsVisible") && Calls("Draw").Contains("SuspendHotbarUi")
            && Calls("DrawExtensionsWhenHostHidden").Contains("GetFrameCount"),
            "compiled extension draw uses independent visibility and per-frame fallback deduplication");
        var dalamud = AssemblyLoadContext.GetLoadContext(assembly)!.LoadFromAssemblyName(new AssemblyName("Dalamud"));
        var builder = dalamud.GetType("Dalamud.Interface.UiBuilder", true)!;
        var field = builder.GetField("interfaceManager", BindingFlags.Instance | BindingFlags.NonPublic);
        var evt = field?.FieldType.GetEvent("Draw", BindingFlags.Instance | BindingFlags.Public);
        var invoke = evt?.EventHandlerType?.GetMethod("Invoke");
        check(field is not null && evt is not null && invoke?.ReturnType == typeof(void) && invoke.GetParameters().Length == 0
            && evt.GetAddMethod() is not null && evt.GetRemoveMethod() is not null,
            "installed Dalamud metadata provides the guarded fallback field and compatible public draw delegate");
        check(!Calls("OnStart").Any(n => n.StartsWith("set_Disable") && n.EndsWith("UiHide")),
            "module startup never changes the shared host automatic-hide flags");
    }

    private class BuilderBase(object source) { private readonly object interfaceManager = source; }
    private sealed class Builder(object source) : BuilderBase(source) { public bool DisableAutomaticUiHide { get; set; } }
    public sealed class FrameSource
    {
        public delegate void BuildUi();
        private BuildUi? draw;
        public bool RefuseRemove { get; set; }
        public int Subscribers => draw?.GetInvocationList().Length ?? 0;
        public event BuildUi Draw
        {
            add => draw += value;
            remove { if (RefuseRemove) throw new InvalidOperationException("remove error"); draw -= value; }
        }
        public void Raise() => draw?.Invoke();
    }
    public sealed class WrongSource { public event EventHandler? Draw { add { } remove { } } }
}
