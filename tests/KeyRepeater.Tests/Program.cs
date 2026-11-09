using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using OmniToolbox.KeyRepeaterOnline;

var checks = 0;
void Check(bool valid, string name)
{
    if (!valid) throw new Exception(name);
    checks++; Console.WriteLine("PASS " + name);
}
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var fixture = JsonSerializer.Deserialize<int[][]>(File.ReadAllText(Path.Combine(root, "tests/KeyRepeater.Tests/keymap-fixture.json")))!;
var map = new KeyRepeatKeyMap(fixture.Select(p => (p[0], p[1])));
foreach (var pair in fixture)
    for (var mods = 0; mods <= 7; mods++)
        Check(map.TryChord(pair[1], mods, out var chord) && chord == (pair[0] | mods << 8),
            $"live-captured key mapping {pair[1]} -> Windows {pair[0]}, modifiers {mods}");
foreach (var (native, win) in new[] { (161, 4), (163, 5), (164, 6), (4, 4), (5, 5), (6, 6) })
    for (var mods = 0; mods <= 7; mods++)
        Check(map.TryChord(native, mods, out var chord) && chord == (win | mods << 8),
            $"middle/side mouse mapping {native} -> {win}, modifiers {mods}");
foreach (var native in new[] { -1, 0, 1, 2, 3, 7, 16, 17, 18, 91, 92, 160, 162, 165, 166, 182, 255, 999 })
    Check(!map.TryChord(native, 0, out _), "unbound/unsupported input cannot become a chord: " + native);
Check(!map.TryChord(136, 8, out _) && !map.TryChord(163, -1, out _), "malformed modifier bits rejected");
Check(!map.TryChord(250, 0, out _), "unknown key does not abort other valid bindings");

foreach (var key in new[] { 4, 5, 6 })
    for (var mods = 0; mods <= 7; mods++)
    {
        var held = new HashSet<int> { key, 1, 2 };
        if ((mods & 1) != 0) held.Add(16);
        if ((mods & 2) != 0) held.Add(17);
        if ((mods & 4) != 0) held.Add(18);
        var messages = KeyRepeatMouseInput.Messages(key, held.Contains);
        var bit = key == 4 ? 16u : key == 5 ? 32u : 64u;
        var high = key == 4 ? 0u : key == 5 ? 1u : 2u;
        var flags = 3u | ((mods & 1) != 0 ? 4u : 0) | ((mods & 2) != 0 ? 8u : 0);
        Check(messages.Length == 2 && messages[0].Id == (key == 4 ? 0x207 : 0x20B)
            && messages[1].Id == (key == 4 ? 0x208 : 0x20C)
            && messages[0].WParam == ((high << 16) | flags | bit)
            && messages[1].WParam == ((high << 16) | flags),
            $"mouse {key} uses matched mouse-down/up and correct XBUTTON/held modifier flags {mods}");
    }
try { KeyRepeatMouseInput.Messages(1, _ => false); throw new Exception("left mouse was accepted"); }
catch (ArgumentOutOfRangeException) { Check(true, "ordinary left click cannot be injected by mouse sender"); }

var down = new HashSet<int>();
var sent = new List<int>();
var controller = new KeyRepeatController();
controller.ReplaceBindings([81, 192, 5, 6], down.Contains);
down.Add(192);
Check(controller.Poll(0, 10, 0, down.Contains, sent.Add) == 1 && sent.Count == 0,
    "physical dot press owns first activation with no duplicate simulated down");
controller.Poll(99, 10, 0, down.Contains, sent.Add);
Check(sent.Count == 0, "no repeat before interval");
controller.Poll(100, 10, 0, down.Contains, sent.Add);
Check(sent.SequenceEqual(new[] { 192 }), "dot repeat emits Windows OEM_3 rather than game key 136");
controller.Poll(1000, 10, 0, down.Contains, sent.Add);
controller.Poll(1000, 10, 0, down.Contains, sent.Add);
Check(sent.Count == 2, "slow frames never generate catch-up bursts");
down.Clear(); controller.Poll(1100, 10, 0, down.Contains, sent.Add);
Check(controller.ScheduledCount == 0 && sent.Count == 2, "key release removes scheduled repeat immediately");

controller.Clear(); sent.Clear();
controller.ReplaceBindings([81 | 3 << 8], down.Contains);
foreach (var order in new[] { new[] { 81, 17, 16 }, new[] { 16, 17, 81 }, new[] { 17, 81, 16 } })
{
    down.Clear(); controller.Poll(0, 10, 0, down.Contains, sent.Add);
    var before = sent.Count;
    foreach (var key in order)
    {
        down.Add(key);
        var mods = (down.Contains(16) ? 1 : 0) | (down.Contains(17) ? 2 : 0);
        controller.Poll(0, 10, mods, down.Contains, sent.Add);
    }
    controller.Poll(100, 10, 3, down.Contains, sent.Add);
    Check(sent.Count == before + 1, "Ctrl+Shift+Q starts in any press order: " + string.Join(",", order));
    foreach (var released in new[] { 16, 17, 81 })
    {
        down.Remove(released);
        var mods = (down.Contains(16) ? 1 : 0) | (down.Contains(17) ? 2 : 0);
        Check(controller.Poll(200, 10, mods, down.Contains, sent.Add) == 0 && controller.ScheduledCount == 0,
            "release of chord component cancels repeat: " + released);
        down.Add(released);
    }
}
controller.Clear(); down.Clear(); sent.Clear();
controller.ReplaceBindings([5, 6, 192, 192], down.Contains);
Check(controller.BindingCount == 3, "duplicate primary/secondary bindings are deduplicated");
down.UnionWith([5, 6, 192]);
controller.Poll(0, 10, 0, down.Contains, sent.Add);
controller.Poll(100, 10, 0, down.Contains, sent.Add);
Check(sent.Order().SequenceEqual(new[] { 5, 6, 192 }), "mouse sides and dot can repeat independently together");
controller.Suspend(down.Contains); var pausedCount = sent.Count;
controller.Poll(1000, 10, 0, down.Contains, sent.Add);
Check(sent.Count == pausedCount && controller.ScheduledCount == 0, "focus/chat/config pause blocks still-held keys");
down.Clear(); controller.Poll(1001, 10, 0, down.Contains, sent.Add);
down.Add(5); controller.Poll(1002, 10, 0, down.Contains, sent.Add);
controller.Poll(1102, 10, 0, down.Contains, sent.Add);
Check(sent.Count == pausedCount + 1 && sent[^1] == 5, "real release and repress rearms after pause");
controller.ReplaceBindings([5 | 2 << 8], down.Contains);
controller.Poll(1202, 10, 2, down.Contains, sent.Add);
controller.Poll(1302, 10, 2, down.Contains, sent.Add);
Check(sent.Count == pausedCount + 1, "rebinding a held mouse cannot activate a new chord without release");
controller.Clear(); down.Clear(); sent.Clear();
controller.ReplaceBindings([5], down.Contains);
for (var i = 0; i < 100; i++)
{
    down.Add(5); controller.Poll(i * 200, 10, 0, down.Contains, sent.Add);
    controller.Poll(i * 200 + 100, 10, 0, down.Contains, sent.Add);
    down.Clear(); controller.Poll(i * 200 + 101, 10, 0, down.Contains, sent.Add);
    Check(sent.Count == i + 1 && controller.ScheduledCount == 0, "rapid side-key cycle has one repeat and no leftover timer: " + i);
}
foreach (var (cps, interval) in new[] { (1f, 1000L), (30f, 33L), (0f, 1000L), (100f, 33L), (float.NaN, 100L), (float.PositiveInfinity, 100L) })
{
    controller.Clear(); down.Clear(); sent.Clear(); controller.ReplaceBindings([192], down.Contains);
    down.Add(192); controller.Poll(0, cps, 0, down.Contains, sent.Add);
    controller.Poll(interval - 1, cps, 0, down.Contains, sent.Add);
    Check(sent.Count == 0, "speed validation maintains minimum interval for " + cps);
    controller.Poll(interval, cps, 0, down.Contains, sent.Add);
    Check(sent.Count == 1, "speed validation repeats at expected interval for " + cps);
}
controller.Clear(); down.Clear(); controller.ReplaceBindings([5], down.Contains);
down.Add(5); controller.Poll(0, 10, 0, down.Contains, _ => { });
try { controller.Poll(100, 10, 0, down.Contains, _ => throw new InvalidOperationException("sender fault")); }
catch (InvalidOperationException) { controller.Suspend(down.Contains); }
Check(controller.ScheduledCount == 0 && controller.Poll(1000, 10, 0, down.Contains, _ => throw new Exception("sent after failure")) == 0,
    "send failure can be suspended without a surviving timer or stuck synthetic state");
controller.Clear();
Check(controller.ScheduledCount == 0 && controller.BindingCount == 0, "unload clears every binding and timer");

// Inspect the actual compiled module with the installed host, without starting it or sending input.
var omni = Environment.GetEnvironmentVariable("KEY_REPEATER_OMNI_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/installedPlugins/OmniToolbox/1.1.9.9");
var dalamud = Environment.GetEnvironmentVariable("KEY_REPEATER_DALAMUD_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/addon/Hooks/26-11-06-02");
var context = new AssemblyLoadContext("key-repeater-contract", true);
context.Resolving += (_, name) =>
{
    foreach (var folder in new[] { omni, dalamud })
    {
        var path = Path.Combine(folder, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
var dll = Path.Combine(root, "build/KeyRepeater/bin/Release/net10.0-windows7.0/KeyRepeater.dll");
var assembly = context.LoadFromAssemblyPath(dll);
var module = assembly.GetType("OmniToolbox.KeyRepeaterOnline.KeyRepeater", true)!;
var host = context.LoadFromAssemblyPath(Path.Combine(omni, "OmniToolbox.Common.dll"));
host.GetType("OmniToolbox.Common.Module.Runtime.ModuleContract", true)!
    .GetMethod("ValidateType", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [module]);
Check(Activator.CreateInstance(module) is not null, "compiled module passes actual Omni API 1 contract and constructs without game services");
var versions = FileVersionInfo.GetVersionInfo(dll);
Check(assembly.GetName().Name == "KeyRepeater" && assembly.GetName().Version == new Version(0, 2, 6, 0)
    && versions.FileVersion == "0.2.6.0" && versions.ProductVersion == "0.2.6"
    && (string)module.GetField("Version")!.GetRawConstantValue()! == "0.2.6", "build, module, assembly and file versions agree");
var settings = module.GetNestedType("Settings", BindingFlags.NonPublic)!;
var config = module.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!;
var instance = Activator.CreateInstance(module)!;
Check(config.FieldType == settings && !config.IsInitOnly && settings.GetField("Enabled")!.FieldType == typeof(bool)
    && settings.GetField("Cps")!.FieldType == typeof(float), "host Settings/config identity and old persisted fields preserved");
Check(!(bool)settings.GetField("Enabled")!.GetValue(config.GetValue(instance))!
    && (float)settings.GetField("Cps")!.GetValue(config.GetValue(instance))! == 10, "fresh installs are disabled with 10 CPS");
var structs = context.LoadFromAssemblyPath(Path.Combine(dalamud, "FFXIVClientStructs.dll"));
var se = structs.GetType("FFXIVClientStructs.FFXIV.Client.System.Input.SeVirtualKey", true)!;
foreach (var (name, code) in new[] { ("PAD_MMB", 161), ("PAD_MB4", 163), ("PAD_MB5", 164) })
    Check(Convert.ToInt32(Enum.Parse(se, name)) == code, "current game struct confirms native mouse code " + name);
Check(!assembly.GetReferencedAssemblies().Any(a => a.Name == "NativeDragHotbar"), "standalone DLL has no Workshop dependency");
context.Unload();
Console.WriteLine($"{checks} checks passed. Real game input delivery requires in-game verification.");
