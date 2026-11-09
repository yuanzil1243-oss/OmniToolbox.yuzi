using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;
using OmniToolbox.SuperHotbarInterop;

internal static class NativeReadChecks
{
    private static (int Windows, int Native)[] Pairs(string root) =>
        JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(root,
            "tests/NativeDragHotbar.Tests/Fixtures/windows-se-keys.json")))!
            .Select((native, windows) => (Windows: windows, Native: native))
            .Where(p => p.Windows >= 8 && p.Native != 0).ToArray();

    public static void Run(string root, string temp, Action<bool, string> check, Action<Action, string> reject)
    {
        var map = new SuperHotbarKeyMap(Pairs(root));
        foreach (var (native, windows) in new[] { (161, 4), (163, 5), (164, 6) })
            for (var mods = 0; mods < 8; mods++)
            {
                var value = SuperInputKeys.Read(map, native, mods);
                check(value.Key == windows && value.Modifiers == mods && value.UnmappedNativeKey == 0
                    && value.Matches(mods, key => key == windows) && SuperInputKeys.Native(map, value.Key) == native,
                    $"native mouse {native} / modifier {mods} reads, triggers and restores the same physical button");
            }
        for (var native = 0; native <= 255; native++)
        {
            var value = SuperInputKeys.Read(map, native, 2);
            check(SuperShortcut.Valid(value) && (value.Key != 0 || native == 0 || value.UnmappedNativeKey == native),
                $"every native byte captures a known shortcut or explicit unsupported identity: {native}");
        }
        reject(() => SuperInputKeys.Read(map, 256, 0), "malformed native keys still reject capture");
        reject(() => SuperInputKeys.Read(map, 164, 8), "malformed native modifiers still reject capture");

        const ulong owner = 0x1122334455667788;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "tests/NativeDragHotbar.Tests/Fixtures/native-bindings-20261109.json")));
        var data = new SuperHotbarDocument
        {
            Character = owner.ToString("X16"), NativeGroupCount = 1, NativeKeybindSize = 11,
            NativeStructsId = "00000000-0000-0000-0000-000000000001", KeyboardEncodingVersion = 1,
            AppearanceDefaultsVersion = 1, AppearanceMappingVersion = 1, NativeGeometryVersion = 1,
            Appearance = Enumerable.Range(0, 10).Select(_ => new DragHotbar()).ToList(),
            Groups = [new() { Index = 0, Bars = Enumerable.Range(0, 18).Select(_ =>
                Enumerable.Range(0, 16).Select(_ => new SuperSavedSlot()).ToList()).ToList() }],
        };
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var raw = Convert.FromHexString(item.GetProperty("RawHex").GetString()!);
            data.Bindings.Add(new() { InputId = item.GetProperty("InputId").GetInt32(),
                Primary = SuperInputKeys.Read(map, raw[0], raw[1]), Secondary = SuperInputKeys.Read(map, raw[2], raw[3]),
                NativeData = Convert.ToBase64String(raw) });
        }
        SuperHotbarRules.Validate(data, owner);
        check(data.Bindings.Count == 146 && data.Bindings.Single(b => b.InputId == 87).Primary.Key == 6
            && data.Bindings.Single(b => b.InputId == 88).Primary.Key == 5
            && !SuperHotbarRules.UnsupportedBindings(data).Any(),
            "all current read-only native bindings capture successfully, including the actual failing 164 and adjacent 163");
        var nativeBytes = data.Bindings.Select(b => b.NativeData).ToArray();
        var path = Path.Combine(temp, "read-after-clear", "SuperHotbar-1122334455667788.json");
        SuperHotbarStore.Save(path, data, owner, false);
        SuperHotbarResetStore.Clear(Path.GetDirectoryName(path)!, false);
        check(!File.Exists(path), "takeover clear removes the temporary character archive before re-reading");
        SuperHotbarStore.Save(path, data, owner, false);
        var reloaded = SuperHotbarStore.Load(path, owner, out var recovered);
        check(!recovered && reloaded.Bindings.Select(b => b.NativeData).SequenceEqual(nativeBytes)
            && reloaded.Bindings.Single(b => b.InputId == 87).Primary.Key == 6,
            "clear then capture/save/reload recreates the complete archive with exact original bindings");

        var unknown = data.Bindings.Single(b => b.InputId == 87);
        var rawUnknown = Convert.FromBase64String(unknown.NativeData); rawUnknown[0] = 250; rawUnknown[1] = 2;
        unknown.NativeData = Convert.ToBase64String(rawUnknown); unknown.Primary = SuperInputKeys.Read(map, 250, 2);
        reloaded = SuperHotbarStore.Decode(SuperHotbarStore.Encode(data, owner), owner);
        var restored = reloaded.Bindings.Single(b => b.InputId == 87);
        check(restored.NativeData == unknown.NativeData && restored.Primary.UnmappedNativeKey == 250
            && restored.Primary.Copy().ToString().Contains("250") && restored.Primary.Compact() == "?"
            && SuperHotbarRules.UnsupportedBindings(reloaded).Single().Contains("第 3 栏第 7 格")
            && !restored.Primary.Matches(2, _ => true) && !SuperHotbarRules.RepeatChords(reloaded).Contains(250 | 2 << 8),
            "unknown key survives checksum backup with a visible slot warning and never polls or repeats a guessed key");
        restored.PrimaryEdited = true;
        reject(() => SuperHotbarRules.Validate(reloaded, owner), "an unsupported original binding cannot masquerade as an edited keyboard key");
        restored.Primary = new() { Key = 6 }; SuperHotbarRules.Validate(reloaded, owner);
        check(restored.Primary.UnmappedNativeKey == 0 && restored.NativeData == unknown.NativeData,
            "re-recording replaces the warning while retaining the original full native backup");
    }

    public static void CheckCompiled(string root, Assembly assembly, Assembly structs, object info, Action<bool, string> check)
    {
        const string ns = "OmniToolbox.NativeDragHotbarLocal.";
        var mapType = assembly.GetType("OmniToolbox.SuperHotbarInterop.SuperHotbarKeyMap", true)!;
        var runtime = assembly.GetType("OmniToolbox.SuperHotbarInterop.SuperHotbarKeyMapRuntime", true)!;
        runtime.GetField("cached", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, Activator.CreateInstance(mapType, [Pairs(root)]));
        var se = structs.GetType("FFXIVClientStructs.FFXIV.Client.System.Input.SeVirtualKey", true)!;
        var setting = structs.GetType("FFXIVClientStructs.FFXIV.Client.System.Input.KeySetting", true)!;
        var archive = assembly.GetType(ns + "SuperNativeArchive", true)!;
        var read = archive.GetMethod("ReadKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var (name, native, windows) in new[] { ("PAD_MMB", 161, 4), ("PAD_MB4", 163, 5), ("PAD_MB5", 164, 6) })
        {
            check(Convert.ToInt32(Enum.Parse(se, name)) == native, "mouse code matches current client enum " + name);
            var input = Activator.CreateInstance(setting)!; setting.GetField("Key")!.SetValue(input, Enum.ToObject(se, native));
            var output = read.Invoke(null, [input])!;
            check((int)output.GetType().GetProperty("Key")!.GetValue(output)! == windows,
                "actual compiled native capture reads mouse " + native);
        }
        var keybind = structs.GetType("FFXIVClientStructs.FFXIV.Client.System.Input.Keybind", true)!;
        var bindingType = assembly.GetType(ns + "SuperBinding", true)!;
        var shortcutType = assembly.GetType(ns + "SuperShortcut", true)!;
        var edit = archive.GetMethod("ApplyEditedKeys", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pointer = Marshal.AllocHGlobal(11);
        try
        {
            byte[] original = [250, 2, 163, 1, 17, 3, 16, 2, 0xaa, 0xbb, 0xcc];
            var saved = Activator.CreateInstance(bindingType)!;
            byte[] Apply()
            {
                Marshal.Copy(original, 0, pointer, original.Length);
                object?[] args = [Marshal.PtrToStructure(pointer, keybind), saved]; edit.Invoke(null, args);
                Marshal.StructureToPtr(args[0]!, pointer, false); var bytes = new byte[11]; Marshal.Copy(pointer, bytes, 0, 11); return bytes;
            }
            check(Apply().SequenceEqual(original), "actual restore leaves unknown keys, secondary key, gamepad and opaque bytes untouched when unedited");
            var shortcut = Activator.CreateInstance(shortcutType)!;
            shortcutType.GetProperty("Key")!.SetValue(shortcut, 6); shortcutType.GetProperty("Modifiers")!.SetValue(shortcut, 4);
            bindingType.GetProperty("Primary")!.SetValue(saved, shortcut); bindingType.GetProperty("PrimaryEdited")!.SetValue(saved, true);
            var result = Apply();
            check(result[0] == 164 && result[1] == 4 && result.Skip(2).SequenceEqual(original.Skip(2)),
                "actual edited mouse restore writes PAD_MB5 and preserves every unrelated native byte");
            bindingType.GetProperty("NativeData")!.SetValue(saved,Convert.ToBase64String(original));
            var expected=archive.GetMethod("ExpectedBindingBytes",BindingFlags.Static|BindingFlags.NonPublic)!;
            check(((byte[])expected.Invoke(null,[saved])!).SequenceEqual(result),
                "preview expected-byte builder matches actual edited native restore output exactly");
            bindingType.GetProperty("PrimaryEdited")!.SetValue(saved,false);
            check(((byte[])expected.Invoke(null,[saved])!).SequenceEqual(original),
                "preview preserves unknown and opaque native bytes for unedited bindings");
        }
        finally { Marshal.FreeHGlobal(pointer); }
        var commands = ((IEnumerable)info.GetType().GetProperty("Commands")!.GetValue(info)!).Cast<object>().ToArray();
        check(commands.Length == 0, "host command footer is empty so commands are not repeated on every settings tab");
        var source = File.ReadAllText(Path.Combine(root, "Modules/NativeDragHotbar.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "Modules/NativeDragHotbarSettings.cs"));
        check(source.Contains("CommandManager.AddHandler(\"/superhotbar\", command)")
            && source.Contains("CommandManager.AddHandler(\"/draghotbar\", command)"),
            "both command entry points remain registered independently of host footer metadata");
        check(settings.Contains("(\"/superhotbar toggle\",")
            && settings.Contains("(\"/superhotbar takeover toggle\",")
            && settings.Contains("(\"/superhotbar native\",")
            && settings.Contains("if (DrawSuperHelpCommand(command, description)) ImGui.SetClipboardText(command)"),
            "operating instructions retain all three copyable commands");
    }
}
