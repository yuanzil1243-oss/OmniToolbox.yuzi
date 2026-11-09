using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniToolbox.SuperHotbarInterop;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed class SuperShortcut
{
    public int Key { get; set; }
    public int Modifiers { get; set; }
    // Key remains zero for unsupported native inputs; never poll a guessed physical key.
    // SuperBinding.NativeData retains the complete untouched binding for native restoration.
    public int UnmappedNativeKey { get; set; }
    public SuperShortcut Copy() => new() { Key = Key, Modifiers = Modifiers, UnmappedNativeKey = UnmappedNativeKey };
    public bool Matches(int modifiers, Func<int, bool> down) => Key != 0 && modifiers == Modifiers && down(Key);
    public static bool Valid(SuperShortcut? key) => key is not null && key.Modifiers is >= 0 and <= 7
        && (key.Key == 0 || SuperInputKeys.Valid(key.Key)) && key.UnmappedNativeKey is >= 0 and <= 255
        && (key.UnmappedNativeKey == 0 || key.Key == 0);
    public override string ToString()
    {
        if (UnmappedNativeKey != 0) return $"未识别原生键码 {UnmappedNativeKey}（修饰码 {Modifiers}，仅保留还原）";
        if (Key == 0) return "未绑定";
        var name = Key switch { >= 48 and <= 57 or >= 65 and <= 90 => ((char)Key).ToString(),
            >= 112 and <= 135 => "F" + (Key - 111), >= 96 and <= 105 => "Num" + (Key - 96),
            4 => "M3", 5 => "M4", 6 => "M5", 32 => "Space", 9 => "Tab", 27 => "Esc", 192 => "·", 189 => "-", 187 => "=",
            186 => ";", 188 => ",", 190 => ".", 191 => "/", 219 => "[", 220 => "\\", 221 => "]", 222 => "'",
            _ => "VK " + Key };
        return ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 4) != 0 ? "Alt+" : "")
            + ((Modifiers & 1) != 0 ? "Shift+" : "") + name;
    }
    public string Compact() => UnmappedNativeKey != 0 ? "?" : Key == 0 ? "" : ((Modifiers & 2) != 0 ? "c" : "")
        + ((Modifiers & 4) != 0 ? "a" : "") + ((Modifiers & 1) != 0 ? "s" : "")
        + new SuperShortcut { Key = Key }.ToString();
}

internal sealed class SuperBinding
{
    public int InputId { get; set; }
    public SuperShortcut Primary { get; set; } = new();
    public SuperShortcut Secondary { get; set; } = new();
    // A native keybind includes gamepad settings. Preserve those bytes while editing only keyboard settings.
    public string NativeData { get; set; } = "";
    public bool PrimaryEdited { get; set; }
    public bool SecondaryEdited { get; set; }
}

internal sealed class SuperSavedSlot
{
    public byte Type { get; set; }
    public uint Id { get; set; }
    public SuperSavedSlot Copy() => new() { Type = Type, Id = Id };
}

internal sealed class SuperSavedGroup
{
    public int Index { get; set; }
    public List<List<SuperSavedSlot>> Bars { get; set; } = [];
}

internal sealed class SuperMacro
{
    public int Set { get; set; }
    public int Index { get; set; }
    public uint Icon { get; set; }
    public uint IconRow { get; set; }
    public string Name { get; set; } = "";
    public List<string> Lines { get; set; } = [];
}

internal sealed class SuperHotbarDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Character { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public string MacroBackupCharacter { get; set; } = "";
    public string MacroBackupName { get; set; } = "";
    public string MacroBackupAt { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Show { get; set; } = true;
    public bool HideNative { get; set; } = true;
    public bool Locked { get; set; }
    public bool AllowKeyRepeater { get; set; } = true;
    public bool BuiltInRepeat { get; set; }
    public float RepeatCps { get; set; } = 10;
    public int EmptySlotDefaultsVersion { get; set; }
    public int FirstPage { get; set; }
    // Zero identifies archives captured before the secondary addon naming was corrected.
    public int AppearanceMappingVersion { get; set; }
    public int KeyboardEncodingVersion { get; set; }
    public int AppearanceDefaultsVersion { get; set; }
    public int NativeGeometryVersion { get; set; }
    public int NativeGroupCount { get; set; }
    public int NativeKeybindSize { get; set; }
    public string NativeStructsId { get; set; } = "";
    public bool[] Shared { get; set; } = new bool[18];
    public List<SuperSavedGroup> Groups { get; set; } = [];
    public List<SuperBinding> Bindings { get; set; } = [];
    public Dictionary<string, uint> NativeOptions { get; set; } = [];
    public Dictionary<string, uint> ControlOptions { get; set; } = [];
    public Dictionary<int, int> PvpGroups { get; set; } = [];
    public List<DragHotbar> Appearance { get; set; } = [];
    // Only edited jobs are materialized; untouched jobs remain in the complete native archive above.
    public Dictionary<string, List<HotbarEntry>> EditedBars { get; set; } = [];
    public List<SuperMacro> Macros { get; set; } = [];
}

internal static class SuperHotbarRules
{
    public static bool UpgradePresentation(SuperHotbarDocument data, SuperHotbarKeyMap map)
    {
        if (data.KeyboardEncodingVersion != 0 && data.AppearanceDefaultsVersion != 0 && data.EmptySlotDefaultsVersion != 0) return false;
        // Resolve every conversion before mutating anything; recorded/edited keys were already Windows VKs.
        var converted = data.KeyboardEncodingVersion == 0 ? data.Bindings.Select(b =>
            (Binding: b, Primary: b.PrimaryEdited ? b.Primary.Copy() : SuperInputKeys.Read(map, b.Primary.Key, b.Primary.Modifiers),
                Secondary: b.SecondaryEdited ? b.Secondary.Copy() : SuperInputKeys.Read(map, b.Secondary.Key, b.Secondary.Modifiers))).ToArray() : [];
        foreach (var (binding, primary, secondary) in converted)
        { binding.Primary = primary; binding.Secondary = secondary; }
        data.KeyboardEncodingVersion = 1;
        if (data.AppearanceDefaultsVersion == 0)
        {
            foreach (var bar in data.Appearance) if (Math.Abs(bar.Opacity - 0.85f) < 0.0001f) bar.Opacity = 0;
            data.AppearanceDefaultsVersion = 1;
        }
        if (data.EmptySlotDefaultsVersion == 0)
        {
            foreach (var bar in data.Appearance) bar.HideEmpty = true;
            data.EmptySlotDefaultsVersion = 1;
        }
        return true;
    }
    public const int FirstSlotInput = NativeHotbarIdentity.FirstSlotInput;
    public static bool HotbarInput(int id) => id is >= 45 and <= 190 or 447;
    public static IEnumerable<string> UnsupportedBindings(SuperHotbarDocument data)
    {
        foreach (var binding in data.Bindings)
        {
            var slot = binding.InputId - FirstSlotInput;
            var label = slot is >= 0 and < 120 ? $"第 {slot / 12 + 1} 栏第 {slot % 12 + 1} 格" : $"原生输入 {binding.InputId}";
            if (binding.Primary.UnmappedNativeKey != 0) yield return label + " 主键：" + binding.Primary;
            if (binding.Secondary.UnmappedNativeKey != 0) yield return label + " 副键：" + binding.Secondary;
        }
    }
    public static int GroupFor(SuperHotbarDocument data, int bar, int jobGroup) => data.Shared[bar]
        ? data.PvpGroups.ContainsValue(jobGroup) && data.PvpGroups.TryGetValue(0, out var sharedPvp) ? sharedPvp : 0 : jobGroup;
    public static string BarKey(int group, int bar) => $"{group}:{bar}";
    public static List<HotbarEntry> Resolve(SuperHotbarDocument data, int bar, int jobGroup, Func<SuperSavedSlot, HotbarEntry> describe)
    {
        var group = GroupFor(data, bar, jobGroup);
        var key = BarKey(group, bar);
        if (data.EditedBars.TryGetValue(key, out var existing)) return existing;
        var saved = data.Groups.Single(g => g.Index == group).Bars[bar];
        var entries = saved.Take(12).Select(describe).ToList();
        data.EditedBars.Add(key, entries);
        return entries;
    }

    public static void Validate(SuperHotbarDocument data, ulong character)
    {
        if (data.SchemaVersion != 1 || data.Character != character.ToString("X16") || character == 0)
            throw new InvalidDataException("热键栏工坊配置版本或角色身份不匹配。");
        if (data.MacroBackupCharacter is null || data.MacroBackupName is null || data.MacroBackupAt is null
            || data.MacroBackupName.Length > 256 || data.MacroBackupAt.Length > 64
            || data.MacroBackupCharacter.Length != 0 && (data.MacroBackupCharacter.Length != 16
                || !ulong.TryParse(data.MacroBackupCharacter, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var macroOwner) || macroOwner == 0))
            throw new InvalidDataException("用户宏备份来源无效。");
        if (!Guid.TryParse(data.NativeStructsId, out _) || data.FirstPage is < 0 or > 9 || data.AppearanceMappingVersion is < 0 or > 1
            || data.KeyboardEncodingVersion is < 0 or > 1 || data.AppearanceDefaultsVersion is < 0 or > 1
            || data.EmptySlotDefaultsVersion is < 0 or > 1
            || data.NativeGeometryVersion is < 0 or > 1 || data.NativeGroupCount is < 1 or > 128 || data.Groups is null || data.Groups.Count != data.NativeGroupCount
            || data.Shared is null || data.Shared.Length != 18 || data.NativeKeybindSize is < 1 or > 128)
            throw new InvalidDataException("原生配置档案结构无效。");
        if (!float.IsFinite(data.RepeatCps) || data.RepeatCps is < 1 or > 30)
            throw new InvalidDataException("内置连点速度应为 1～30 CPS。");
        var indices = new HashSet<int>();
        foreach (var group in data.Groups)
        {
            if (group is null || group.Index < 0 || group.Index >= data.NativeGroupCount || !indices.Add(group.Index)
                || group.Bars is null || group.Bars.Count != 18) throw new InvalidDataException("职业档案缺失或重复。");
            foreach (var bar in group.Bars)
                if (bar is null || bar.Count != 16 || bar.Any(s => s is null || (s.Type == 0 && s.Id != 0)))
                    throw new InvalidDataException("原生格子档案无效。");
        }
        if (data.Appearance is null || data.Appearance.Count != 10 || data.Appearance.Any(b => b is null || b.Slots is null || b.SlotCount != 12 || b.Slots.Count != 12))
            throw new InvalidDataException("显示栏档案无效。");
        HotbarRules.Validate(new() { Owner = character.ToString("X16"), Bars = data.Appearance }, character);
        var inputIds = new HashSet<int>();
        if (data.Bindings is null || data.Bindings.Count > 200) throw new InvalidDataException("绑定档案无效。");
        foreach (var binding in data.Bindings)
            if (binding is null || !HotbarInput(binding.InputId) || !inputIds.Add(binding.InputId)
                || !SuperShortcut.Valid(binding.Primary) || !SuperShortcut.Valid(binding.Secondary)
                || binding.PrimaryEdited && binding.Primary.UnmappedNativeKey != 0
                || binding.SecondaryEdited && binding.Secondary.UnmappedNativeKey != 0
                || binding.NativeData is null || !ValidNativeBytes(binding.NativeData, data.NativeKeybindSize))
                throw new InvalidDataException("快捷键档案损坏。");
        for (var id = FirstSlotInput; id < FirstSlotInput + 120; id++)
            if (!inputIds.Contains(id)) throw new InvalidDataException("普通栏主、副绑定档案不完整。");
        if (data.EditedBars is null || data.EditedBars.Count > data.NativeGroupCount * 10)
            throw new InvalidDataException("编辑档案无效。");
        foreach (var (key, slots) in data.EditedBars)
        {
            var parts = key.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var group) || !int.TryParse(parts[1], out var bar)
                || group < 0 || group >= data.NativeGroupCount || bar is < 0 or >= 10 || slots is null || slots.Count != 12)
                throw new InvalidDataException("编辑格子映射无效。");
            var test = new DragHotbar { Slots = slots };
            HotbarRules.Validate(new() { Owner = character.ToString("X16"), Bars = [test] }, character);
        }
        if (data.NativeOptions is null || data.NativeOptions.Count > 1000
            || data.NativeOptions.Keys.Any(k => !k.StartsWith("Hotbar", StringComparison.Ordinal) || k.Length > 100))
            throw new InvalidDataException("原生栏选项无效。");
        if (data.ControlOptions is null || data.ControlOptions.Count > 300 || data.ControlOptions.Keys.Any(k =>
            !(k.StartsWith("Hotbar", StringComparison.Ordinal) || k == "ExHotbarSetting")))
            throw new InvalidDataException("原生控制选项无效。");
        if (data.PvpGroups is null || data.PvpGroups.Count > 100 || data.PvpGroups.Any(p =>
            p.Key is < 0 or > 100 || p.Value < 0 || p.Value >= data.NativeGroupCount))
            throw new InvalidDataException("PvP 职业索引无效。");
        if (data.Macros is null || data.Macros.Count > 200) throw new InvalidDataException("宏档案无效。");
        var macros = new HashSet<string>();
        foreach (var macro in data.Macros)
            if (macro is null || macro.Set is < 0 or > 1 || macro.Index is < 0 or >= 100
                || !macros.Add($"{macro.Set}:{macro.Index}") || macro.Name is null || macro.Name.Length > 256
                || macro.Lines is null || macro.Lines.Count != 15 || macro.Lines.Any(l => l is null || l.Length > 2048))
                throw new InvalidDataException("宏内容档案损坏。");
    }

    private static bool ValidNativeBytes(string text, int size)
    { try { return Convert.FromBase64String(text).Length == size; } catch (FormatException) { return false; } }

    public static IEnumerable<(int Bar, int Slot, SuperBinding Binding)> SlotBindings(SuperHotbarDocument data) =>
        data.Bindings.Where(b => b.InputId is >= FirstSlotInput and < FirstSlotInput + 120)
            .Select(b => ((b.InputId - FirstSlotInput) / 12, (b.InputId - FirstSlotInput) % 12, b));

    public static SuperSavedSlot EffectiveSlot(SuperHotbarDocument data, int group, int bar, int slot)
    {
        if (bar < 10 && slot < 12 && data.EditedBars.TryGetValue(BarKey(group, bar), out var entries))
        {
            var entry = entries[slot];
            return new() { Type = entry.PreservedNativeType != 0 ? entry.PreservedNativeType : entry.NativeType,
                Id = entry.PreservedNativeType != 0 ? entry.PreservedNativeId : entry.NativeId };
        }
        return data.Groups.Single(g => g.Index == group).Bars[bar][slot].Copy();
    }

    public static int Conflict(SuperHotbarDocument data, int exceptId, SuperShortcut shortcut) => shortcut.Key == 0 ? -1
        : data.Bindings.Where(b => b.InputId != exceptId).FirstOrDefault(b =>
            b.Primary.Key == shortcut.Key && b.Primary.Modifiers == shortcut.Modifiers
            || b.Secondary.Key == shortcut.Key && b.Secondary.Modifiers == shortcut.Modifiers)?.InputId ?? -1;

    public static SuperBinding? FirstBinding(SuperHotbarDocument data, int key, int modifiers) =>
        data.Bindings.OrderBy(b => b.InputId).FirstOrDefault(b =>
            b.Primary.Key == key && b.Primary.Modifiers == modifiers || b.Secondary.Key == key && b.Secondary.Modifiers == modifiers);

    public static int[] RepeatChords(SuperHotbarDocument data) => data.Bindings
        .SelectMany(b => new[] { b.Primary, b.Secondary }).Where(s => s.Key != 0)
        .Select(s => s.Key | s.Modifiers << 8).Distinct()
        .Where(chord => FirstBinding(data, chord & 255, chord >> 8) is { } b && NativeHotbarIdentity.TryInput(b.InputId, out _, out _))
        .Order().ToArray();
}

internal sealed class SuperHotbarIncompatibleException(string message) : IOException(message);

internal static class SuperHotbarStore
{
    private sealed class Envelope { public string Format { get; set; } = ""; public string Sha256 { get; set; } = ""; public string Payload { get; set; } = ""; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 32 };
    public static string Encode(SuperHotbarDocument data, ulong owner)
    {
        SuperHotbarRules.Validate(data, owner);
        var payload = JsonSerializer.Serialize(data, Json);
        var text = JsonSerializer.Serialize(new Envelope { Format = "SuperHotbar.Backup", Payload = payload, Sha256 = Hash(payload) }, Json);
        if (text.Length > 32_000_000) throw new InvalidDataException("完整档案超过 32 MB 字符限制，未写入。");
        return text;
    }
    public static SuperHotbarDocument Decode(string text, ulong owner)
    {
        if (text.Length > 32_000_000) throw new InvalidDataException("备份过大。");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(text, Json) ?? throw new InvalidDataException("备份为空。");
            if (envelope.Format != "SuperHotbar.Backup" || envelope.Payload is null || envelope.Sha256 != Hash(envelope.Payload))
                throw new InvalidDataException("备份格式或 SHA256 校验失败。");
            var data = JsonSerializer.Deserialize<SuperHotbarDocument>(envelope.Payload, Json) ?? throw new InvalidDataException("配置为空。");
            if (data.SchemaVersion != 1 || data.Character != owner.ToString("X16"))
                throw new SuperHotbarIncompatibleException("档案属于另一角色或较新版本；禁止回退旧备份覆盖。");
            SuperHotbarRules.Validate(data, owner); return data;
        }
        catch (JsonException e) { throw new InvalidDataException("备份 JSON 无效。", e); }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static SuperHotbarDocument Load(string path, ulong owner, out bool recovered)
    {
        recovered = false;
        if (!File.Exists(path))
        {
            if (!File.Exists(path + ".bak")) throw new FileNotFoundException("尚未建立此角色的热键栏工坊档案。", path);
            recovered = true; return Decode(File.ReadAllText(path + ".bak"), owner);
        }
        try { return Decode(File.ReadAllText(path), owner); }
        catch (InvalidDataException)
        {
            // Never replace an invalid main file during loading; recovery is explicit in the caller.
            if (!File.Exists(path + ".bak")) throw;
            var data = Decode(File.ReadAllText(path + ".bak"), owner);
            recovered = true; return data;
        }
    }
    public static void Save(string path, SuperHotbarDocument data, ulong owner, bool recovered = false)
    {
        var text = Encode(data, owner);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                var history = path + ".history";
                Directory.CreateDirectory(history);
                File.Copy(path, Path.Combine(history, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".json"));
                if (recovered) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                File.Replace(temporary, path, recovered ? null : path + ".bak");
                foreach (var old in new DirectoryInfo(history).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(30)) old.Delete();
            }
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

// Edge-triggered physical input. Blocked input and context changes require release before rearming.
internal sealed class SuperKeyEdges
{
    private readonly HashSet<int> held = [];
    private readonly Dictionary<int, int> repeatable = [];
    public bool Press(int key, bool down, bool allowed, int modifiers = 0)
    {
        if (!down) { held.Remove(key); repeatable.Remove(key); return false; }
        var fresh = held.Add(key);
        if (!allowed || repeatable.TryGetValue(key, out var prior) && prior != modifiers) repeatable.Remove(key);
        if (fresh && allowed) repeatable[key] = modifiers;
        return fresh && allowed;
    }
    public bool CanRepeat(int key, int modifiers) => repeatable.TryGetValue(key, out var pressed) && pressed == modifiers;
    public void BlockHeld(Func<int, bool> down)
    { for (var key = 4; key <= 254; key++) Press(key, down(key), false); }
}
