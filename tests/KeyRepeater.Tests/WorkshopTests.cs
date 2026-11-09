using OmniToolbox.KeyRepeaterOnline;
using OmniToolbox.SuperHotbarInterop;

internal static class WorkshopTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        Check(SuperHotbarRepeatProtocol.Active == "OmniToolbox.Yuzi.SuperHotbar.v1.Active"
            && SuperHotbarRepeatProtocol.Bindings == "OmniToolbox.Yuzi.SuperHotbar.v1.Bindings"
            && SuperHotbarRepeatProtocol.Repeat == "OmniToolbox.Yuzi.SuperHotbar.v1.Repeat",
            "client uses the published Workshop v1 endpoint names");
        for (var mods = 0; mods <= 7; mods++)
        {
            Check(SuperHotbarRepeatProtocol.TryChord(192 | mods << 8, out var key, out var decoded)
                && key == 192 && decoded == mods, "Workshop dot chord retains all modifier bits: " + mods);
            foreach (var mouse in new[] { 4, 5, 6 })
                Check(!SuperHotbarRepeatProtocol.TryChord(mouse | mods << 8, out _, out _),
                    $"published Workshop endpoint rejects mouse {mouse}, modifiers {mods}");
        }

        var available = 0;
        var takingOver = false;
        int[] exported = [];
        var activeReads = 0; var bindingReads = 0;
        var throwActive = false; var throwBindings = false; var throwRepeat = false;
        var acceptRepeat = true;
        var calls = new List<(int Chord, bool First)>();
        var native = new List<int>();
        var route = new KeyRepeatWorkshop(() => available,
            () => { activeReads++; return throwActive ? throw new InvalidOperationException("active") : takingOver; },
            () => { bindingReads++; return throwBindings ? throw new InvalidOperationException("bindings") : exported; },
            (chord, first) =>
            {
                calls.Add((chord, first));
                return throwRepeat ? throw new InvalidOperationException("repeat") : acceptRepeat;
            });
        var controller = new KeyRepeatController();
        var down = new HashSet<int>();
        var nativeBindings = new HashSet<int> { 81, 5 | 2 << 8, 192 | 2 << 8 };
        void Frame(long now, int mods = 0)
        {
            if (route.Refresh()) controller.Suspend(down.Contains);
            if (route.Mode == KeyRepeatMode.Unavailable) { controller.Suspend(down.Contains); return; }
            controller.ReplaceBindings(route.Mode == KeyRepeatMode.Workshop ? route.Bindings : nativeBindings, down.Contains);
            controller.Poll(now, 10, mods, down.Contains, chord => route.Send(chord, native.Add));
        }
        void ExpectFault(Action action, string name)
        {
            var caught = false;
            try { action(); }
            catch (InvalidOperationException) { caught = true; controller.Suspend(down.Contains); }
            Check(caught && route.Mode == KeyRepeatMode.Unavailable && route.Bindings.Count == 0, name);
        }
        Frame(0);
        Check(route.Mode == KeyRepeatMode.Native && activeReads == 0 && bindingReads == 0,
            "uninstalled Workshop uses native mode without calling absent endpoints");
        down.UnionWith([5, 17]); Frame(1, 2); Frame(101, 2);
        Check(native.SequenceEqual(new[] { 5 }) && calls.Count == 0,
            "native Ctrl+side mouse sends only the Windows VK and keeps modifier context physical");
        down.Clear(); Frame(102);
        available = 7; Frame(103);
        Check(route.Mode == KeyRepeatMode.Native && bindingReads == 0,
            "installed Workshop with takeover disabled still uses native mode");

        takingOver = true; exported = [81, 81, 192 | 2 << 8, 5, -1, 16, 81 | 8 << 8];
        Frame(104);
        Check(route.Mode == KeyRepeatMode.Workshop && route.Bindings.SetEquals([81, 192 | 2 << 8]) && route.Skipped == 4,
            "Workshop primary/secondary chords deduplicate, mouse/malformed/modifier chords skip individually");
        down.UnionWith([192, 17]); Frame(105, 2);
        Check(calls.Count == 0, "Workshop retains physical first activation without an IPC first tick");
        Frame(205, 2);
        Check(calls.SequenceEqual(new[] { (192 | 2 << 8, false) }) && native.Count == 1,
            "Workshop Ctrl+dot repeat passes full chord with firstTick=false and no native simulation");
        Frame(206, 0); Frame(400, 0);
        Check(calls.Count == 1, "Workshop modifier mismatch immediately cancels the scheduled tick");
        down.Clear(); Frame(401);

        // Both provider-side switches intentionally export an empty set. Do not infer
        // that takeover ended or return to the original game bindings in either case.
        foreach (var cause in new[] { "built-in repeat enabled", "external compatibility disabled", "no repeatable binding" })
        {
            exported = []; Frame(500);
            down.Add(81); Frame(501); Frame(601);
            Check(route.Mode == KeyRepeatMode.Workshop && controller.BindingCount == 0 && native.Count == 1 && calls.Count == 1,
                "empty Workshop export suppresses native repeat: " + cause);
            down.Clear(); Frame(602);
        }
        exported = [81]; Frame(700);
        down.Add(81); Frame(701); Frame(801);
        Check(calls.Count == 2, "external repeat resumes when Workshop exports bindings again");
        exported = []; Frame(802); Frame(1000);
        Check(calls.Count == 2 && controller.ScheduledCount == 0, "enabling built-in repeat cancels external timer on next frame");
        exported = [81]; Frame(1001); Frame(1200);
        Check(calls.Count == 2, "returning from built-in to external repeat requires physical release");
        down.Clear(); Frame(1201); down.Add(81); Frame(1202); Frame(1302);
        Check(calls.Count == 3, "release/repress rearms after repeat ownership changes");

        takingOver = false; Frame(1303); Frame(1500);
        Check(native.Count == 1 && controller.ScheduledCount == 0,
            "Workshop-to-native transition with identical chord blocks held key");
        down.Clear(); Frame(1501); down.Add(81); Frame(1502); Frame(1602);
        Check(native.Count == 2 && native[^1] == 81, "native repeat resumes after release/repress");
        takingOver = true; Frame(1603); Frame(1800);
        Check(calls.Count == 3, "native-to-Workshop transition blocks held key even when bindings match");
        down.Clear(); Frame(1801);
        foreach (var mask in Enumerable.Range(1, 6))
        {
            available = mask; down.Add(81); Frame(1802); Frame(2000);
            Check(route.Mode == KeyRepeatMode.Unavailable && native.Count == 2 && calls.Count == 3,
                "partially registered IPC never falls back to native input: mask " + mask);
            down.Clear(); Frame(2001);
        }
        available = 7; Frame(2100);
        down.Add(81); Frame(2101); Frame(2201);
        Check(calls.Count == 4, "complete provider registration recovers from a transient partial state");
        available = 0; Frame(2202); Frame(2400);
        Check(route.Mode == KeyRepeatMode.Native && native.Count == 2,
            "provider unload restores native routing but blocks a held key");
        down.Clear(); Frame(2401); down.Add(81); Frame(2402); Frame(2502);
        Check(native.Count == 3, "native input rearms after provider unload and real repress");

        down.Clear(); available = 7; Frame(2600);
        // A new independent binding is not in the old set: it too must be blocked
        // when added while already physically held.
        down.Add(82); exported = [82]; Frame(2601); Frame(2800);
        Check(calls.Count == 4, "new Workshop binding held during editing cannot begin repeat");
        down.Clear(); Frame(2801); down.Add(82); Frame(2802); Frame(2902);
        Check(calls.Count == 5 && calls[^1] == (82, false), "edited binding works after release/repress");

        acceptRepeat = false;
        ExpectFault(() => Frame(3002), "Workshop rejection latches unavailable and clears exported keys");
        var rejectedCount = calls.Count;
        takingOver = false; available = 0; Frame(3300);
        Check(route.Mode == KeyRepeatMode.Unavailable && native.Count == 3 && calls.Count == rejectedCount,
            "rejected IPC stays paused even after provider disappears, without native fallback");
        acceptRepeat = true; route.Reset(); down.Clear(); Frame(3301);
        Check(route.Mode == KeyRepeatMode.Native, "explicit reload clears a latched routing fault");

        available = 7; takingOver = true; exported = [81]; Frame(3400);
        down.Add(81); Frame(3401); throwRepeat = true;
        ExpectFault(() => Frame(3501), "throwing Repeat endpoint pauses without native simulation");
        Check(native.Count == 3, "exception during IPC dispatch never invokes native fallback");
        throwRepeat = false; route.Reset(); down.Clear(); throwActive = true;
        ExpectFault(() => Frame(3600), "throwing Active endpoint fails closed");
        throwActive = false; route.Reset(); throwBindings = true;
        ExpectFault(() => Frame(3700), "throwing Bindings endpoint fails closed");
        throwBindings = false; route.Reset(); exported = null!;
        ExpectFault(() => Frame(3800), "null binding payload is an interface fault, not an empty compatibility export");
        route.Reset(); exported = [81]; Frame(3900);
        ExpectFault(() => route.Send(82, native.Add), "non-exported chord cannot bypass Workshop routing");

        route.Reset(); Frame(4000); controller.Clear(); route.Reset();
        Check(controller.BindingCount == 0 && controller.ScheduledCount == 0 && route.Bindings.Count == 0,
            "unload/reset leaves no bindings or scheduled repeats");
        return checks;
    }
}
