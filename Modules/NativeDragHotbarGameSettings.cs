using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed record WorkshopGameScope(bool Layout = true, bool Character = true, bool Bindings = true, bool System = false);
internal sealed record WorkshopGameOption(string Section, string Name, string Kind, uint UInt = 0, float Float = 0);
internal sealed record WorkshopGameKey(int Id, string Data);
// Only pointer-free, bounded values are portable. Native lists and serialized native file
// blobs are never imported, copied or passed back into an untrusted native parser.
internal sealed record WorkshopGameWindow(string Name, int Layout, uint Hash, float X, float Y, float Scale,
    uint Flags, ushort Width, ushort Height, byte Byte1, byte Byte2, byte Byte3, byte Alpha);
internal sealed class WorkshopGameTemplate
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Source { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public string StructsId { get; set; } = "";
    public string KeySchema { get; set; } = "";
    public int KeySize { get; set; }
    public int KeyCount { get; set; }
    public int CurrentLayout { get; set; }
    public WorkshopGameScope Scope { get; set; } = new();
    public List<WorkshopGameWindow> Windows { get; set; } = [];
    public List<WorkshopGameOption> Options { get; set; } = [];
    public List<WorkshopGameKey> Keys { get; set; } = [];
    // Only local, same-character application-before backups may carry the complete typed
    // HUD entry prefix, including default/empty layouts. Never included in portable profiles.
    public List<WorkshopGameWindow>? LocalHudSnapshot { get; set; }
}
internal sealed record WorkshopGameDifference(string Category, string Name, string Before, string After);

internal static class WorkshopGameRules
{
    // Verified from the actual dependency assemblies, not inferred from byte size.
    internal const string VerifiedKeySchema = "1D7E818F44469AE5119574860FEAEC0FB2B5ED0637AB6EF8E62373BCE74E951D";
    private static readonly HashSet<Guid> VerifiedLegacyBuilds = [
        new("3026f17c-0e2a-49fe-9842-6b41f2b2a2bf"),
        new("11763456-61fc-4b0e-9e27-7db51fd84797"),
        new("d5a6c4cf-8f1a-4d66-bbcb-130678162b0b"),
    ];
    internal static void CheckKeyCompatibility(WorkshopGameTemplate template, string currentBuild,
        string currentSchema, int keySize, int keyCount)
    {
        Validate(template);
        if (!template.Scope.Bindings) return;
        var compatible = template.KeySchema.Length > 0
            ? template.KeySchema.Equals(currentSchema, StringComparison.OrdinalIgnoreCase)
            : Guid.Parse(template.StructsId) == Guid.Parse(currentBuild)
                || VerifiedLegacyBuilds.Contains(Guid.Parse(template.StructsId)) && currentSchema == VerifiedKeySchema;
        if (!compatible || template.KeySize != keySize || keyCount is < 1 or > 4096
            || template.Keys.Any(k => k.Id >= keyCount))
            throw new InvalidDataException("模板中的其他快捷键尚未确认兼容当前客户端，未应用。已保留原配置和自定义指令，请保留配置并反馈此提示。");
    }

    // A native default can intentionally sit outside the ordinary editable range
    // (LockonDefaultZoom: default .61, min .69, max .78). SetValue restores that
    // default for out-of-range input. Equal live values require no native write.
    internal static bool UIntCompatible(uint value, uint current, uint fallback, uint minimum, uint maximum)
        => value == current || minimum <= maximum && (value == fallback || value >= minimum && value <= maximum);
    internal static bool FloatCompatible(float value, float current, float fallback, float minimum, float maximum)
        => float.IsFinite(value) && (value == current || float.IsFinite(fallback) && float.IsFinite(minimum)
            && float.IsFinite(maximum) && minimum <= maximum
            && (value == fallback || value >= minimum && value <= maximum));

    internal static bool NeedsAutomaticApply(WorkshopProfile profile, ulong owner) => owner != 0
        && profile is { AutoApplyGameSettings: true, GameSettings: { } template }
        && !template.Source.Equals(owner.ToString("X16"), StringComparison.OrdinalIgnoreCase)
        && profile.AppliedGameSettings.GetValueOrDefault(owner.ToString("X16")) != template.Id;

    internal static WorkshopGameTemplate LayoutOnly(WorkshopGameTemplate template)
    {
        Validate(template);
        if (!template.Scope.Layout || !template.Windows.Any(w => w.Layout == template.CurrentLayout))
            throw new InvalidDataException("当前配置没有保存其他界面布局，请先读取界面布局。");
        var result = WorkshopProfiles.Copy(template);
        result.Scope = new(true, false, false, false);
        result.Options.Clear(); result.Keys.Clear(); result.KeySchema = ""; result.LocalHudSnapshot = null;
        Validate(result); return result;
    }

    internal static WorkshopGameTemplate UpdateLayout(WorkshopGameTemplate? previous, WorkshopGameTemplate captured)
        => UpdateSelected(previous, LayoutOnly(captured));

    internal static WorkshopGameTemplate UpdateSelected(WorkshopGameTemplate? previous, WorkshopGameTemplate captured)
    {
        Validate(captured);
        var update = WorkshopProfiles.Copy(captured); update.LocalHudSnapshot = null;
        if (previous is null) return update;
        Validate(previous);
        var result = WorkshopProfiles.Copy(previous);
        result.Id = Guid.NewGuid().ToString("N"); result.Source = captured.Source;
        result.SourceName = captured.SourceName; result.CapturedAt = captured.CapturedAt;
        if (update.Scope.Layout) { result.CurrentLayout = update.CurrentLayout; result.Windows = update.Windows; }
        if (update.Scope.Bindings)
        {
            result.StructsId = update.StructsId; result.KeySchema = update.KeySchema;
            result.KeySize = update.KeySize; result.KeyCount = update.KeyCount; result.Keys = update.Keys;
        }
        result.Options.RemoveAll(o => o.Section == "System" ? update.Scope.System : update.Scope.Character);
        result.Options.AddRange(update.Options);
        result.Scope = new(result.Scope.Layout || update.Scope.Layout, result.Scope.Character || update.Scope.Character,
            result.Scope.Bindings || update.Scope.Bindings, result.Scope.System || update.Scope.System);
        result.LocalHudSnapshot = null;
        Validate(result); return result;
    }
    private static readonly HashSet<string> SystemNames = new(StringComparer.Ordinal)
    {
        "Gamma", "CharaLight", "MouseSpeed", "CameraZoom", "MouseAutoFocus", "ColorThemeType",
        "TextureFilterQuality", "TextureAnisotropicQuality", "SSAO", "Glare", "DistortionWater",
        "DepthOfField", "RadialBlur", "Vignetting", "GrassQuality", "TranslucentQuality",
        "LodType", "OcclusionCulling", "ShadowLOD", "MapResolution", "ReflectionType", "WaterWet",
        "AntiAliasing", "DisplayObjectLimitType", "DisplayObjectLimitType2", "DisplayHouseIndoorPcLimitType",
        "GraphicsRezoScale", "GraphicsRezoUpscaleType", "GrassEnableDynamicInterference", "TextureRezoType",
        "Is3DAudio", "BattleEffect", "BGEffect", "SystemMouseOperationSoftOn", "SystemMouseOperationTrajectory",
        "SystemMouseOperationCursorScaling", "HardwareCursorSize", "TextPasteEnable",
    };
    internal static bool OptionAllowed(string section, string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) return false;
        if (section is "Ui" or "Control") return !name.StartsWith("Hotbar", StringComparison.Ordinal)
            && !name.StartsWith("ExHotbar", StringComparison.Ordinal) && !name.StartsWith("ActiveLS", StringComparison.Ordinal)
            && !name.Contains("Progress", StringComparison.Ordinal) && !name.Contains("Mirage", StringComparison.Ordinal);
        if (section != "System") return false;
        var basic = name.EndsWith("_DX11", StringComparison.Ordinal) ? name[..^5] : name;
        return SystemNames.Contains(basic) || basic.StartsWith("Sound", StringComparison.Ordinal)
            || basic.StartsWith("IsSound", StringComparison.Ordinal) || basic.StartsWith("IsSnd", StringComparison.Ordinal)
            || basic.StartsWith("Accessibility", StringComparison.Ordinal) || basic.StartsWith("ShadowVisibilityType", StringComparison.Ordinal)
            || basic.StartsWith("ShadowSoftShadowType", StringComparison.Ordinal) || basic.StartsWith("ShadowTextureSizeType", StringComparison.Ordinal)
            || basic.StartsWith("ShadowCascadeCountType", StringComparison.Ordinal) || basic.StartsWith("PhysicsType", StringComparison.Ordinal);
    }

    internal static bool WindowAllowed(string name) => !string.IsNullOrEmpty(name) && name.Length <= 64
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
        && !name.StartsWith("_Action", StringComparison.Ordinal) && !name.StartsWith("_Cross", StringComparison.Ordinal);
    internal static bool KeyAllowed(int id) => id >= 0 && id < 4096 && !SuperHotbarRules.HotbarInput(id);
    internal static uint NameHash(string name)
    {
        uint crc = uint.MaxValue;
        foreach (var b in Encoding.UTF8.GetBytes(name + "_a"))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return ~crc;
    }
    internal static void Validate(WorkshopGameTemplate value)
    {
        if (value is null || value.Version != 1 || !Guid.TryParse(value.Id, out _) || !Guid.TryParse(value.StructsId, out _)
            || value.Source is null || value.Source.Length != 16 || !ulong.TryParse(value.Source, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var owner) || owner == 0 || value.SourceName is null || value.SourceName.Length > 128
            || !DateTimeOffset.TryParse(value.CapturedAt, out _) || value.CurrentLayout is < 0 or > 3 || value.Scope is null
            || value.Windows is null || value.Options is null || value.Keys is null || value.Windows.Count > 1500
            || value.Options.Count > 2048 || value.Keys.Count > 4096 || value.KeySize is < 1 or > 64 || value.KeyCount is < 1 or > 4096
            || value.KeySchema is null || value.KeySchema.Length != 0 && (value.KeySchema.Length != 64 || !value.KeySchema.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("游戏设置模板的版本、身份或范围无效。");
        if (!value.Scope.Layout && value.Windows.Count != 0 || !value.Scope.Bindings && value.Keys.Count != 0
            || !value.Scope.Layout && !value.Scope.Character && !value.Scope.Bindings && !value.Scope.System)
            throw new InvalidDataException("游戏设置模板的内容与范围不符。");
        var windows = new HashSet<(int, string)>();
        foreach (var w in value.Windows)
        {
            if (w is null || !WindowAllowed(w.Name) || w.Layout is < -1 or > 3 || w.Hash != NameHash(w.Name))
                throw new InvalidDataException("游戏界面条目标识无效：" + (w?.Name ?? "空条目"));
            if (!windows.Add((w.Layout, w.Name)))
                throw new InvalidDataException($"游戏界面条目重复：{w.Name}（{(w.Layout < 0 ? "窗口" : "HUD " + (w.Layout + 1))}）。");
            if (!float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Scale)
                || Math.Abs(w.X) > 100_000 || Math.Abs(w.Y) > 100_000 || w.Scale is < 0 or > 10)
                throw new InvalidDataException($"游戏界面数值无效：{w.Name}（位置 {w.X:R}, {w.Y:R}；缩放 {w.Scale:R}）。");
        }
        for (var layout = 0; layout < 4; layout++)
            if (value.Windows.Count(w => w.Layout == layout) > 112) throw new InvalidDataException("HUD 布局条目过多。");
        if (value.LocalHudSnapshot is { } snapshot)
        {
            if (!value.Scope.Layout || snapshot.Count > 448) throw new InvalidDataException("本地 HUD 备份范围无效。");
            var identities = new HashSet<(int, uint)>();
            foreach (var w in snapshot)
                if (w is null || w.Name != "" || w.Layout is < 0 or > 3 || w.Hash == 0
                    || !float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Scale)
                    || Math.Abs(w.X) > 100_000 || Math.Abs(w.Y) > 100_000 || w.Scale is < 0 or > 10
                    || !identities.Add((w.Layout, w.Hash))) throw new InvalidDataException("本地 HUD 备份条目无效。");
            for (var layout = 0; layout < 4; layout++)
                if (snapshot.Count(w => w.Layout == layout) > 112) throw new InvalidDataException("本地 HUD 备份条目过多。");
        }
        var options = new HashSet<(string, string)>();
        foreach (var o in value.Options)
            if (o is null || !OptionAllowed(o.Section, o.Name) || o.Kind is not ("UInt" or "Float") || !float.IsFinite(o.Float)
                || o.Kind == "UInt" && o.Float != 0 || o.Kind == "Float" && o.UInt != 0
                || o.Section == "System" && !value.Scope.System || o.Section != "System" && !value.Scope.Character
                || !options.Add((o.Section, o.Name))) throw new InvalidDataException("模板含有不支持的游戏选项。");
        var keys = new HashSet<int>();
        foreach (var k in value.Keys)
        {
            if (k is null || !KeyAllowed(k.Id) || k.Id >= value.KeyCount || !keys.Add(k.Id) || k.Data is null || k.Data.Length > 128)
                throw new InvalidDataException("模板包含重复或不支持的快捷键。");
            try { if (Convert.FromBase64String(k.Data).Length != value.KeySize) throw new InvalidDataException("模板按键大小无效。"); }
            catch (FormatException error) { throw new InvalidDataException("模板按键编码无效。", error); }
        }
    }

    internal static List<WorkshopGameDifference> Compare(WorkshopGameTemplate current, WorkshopGameTemplate target)
    {
        Validate(current); Validate(target);
        var result = new List<WorkshopGameDifference>();
        string WindowText(WorkshopGameWindow w) => $"位置 {w.X:0.##}, {w.Y:0.##} · 缩放 {w.Scale:0.##} · 显示/样式 {w.Byte1}/{w.Byte2}/{w.Byte3}";
        var windows = current.Windows.ToDictionary(w => (w.Layout, w.Name));
        foreach (var w in target.Windows)
            if (!windows.TryGetValue((w.Layout, w.Name), out var before) || before != w)
                result.Add(new(w.Layout < 0 ? "窗口" : $"HUD {w.Layout + 1}", w.Name, before is null ? "默认／未保存" : WindowText(before), WindowText(w)));
        if (target.Scope.Layout && current.CurrentLayout != target.CurrentLayout)
            result.Add(new("HUD", "当前界面布局", (current.CurrentLayout + 1).ToString(), (target.CurrentLayout + 1).ToString()));
        if (target.LocalHudSnapshot is { } savedHud)
        {
            if (current.LocalHudSnapshot is null) throw new InvalidDataException("缺少当前角色的完整 HUD 对照备份。");
            for (var layout = 0; layout < 4; layout++)
                if (!current.LocalHudSnapshot.Where(w => w.Layout == layout).SequenceEqual(savedHud.Where(w => w.Layout == layout)))
                    result.Add(new($"HUD {layout + 1}", "应用前完整布局", "当前布局", "恢复原有布局（含默认／空布局）"));
        }
        var options = current.Options.ToDictionary(o => (o.Section, o.Name));
        string Value(WorkshopGameOption o) => o.Kind == "UInt" ? o.UInt.ToString(CultureInfo.InvariantCulture) : o.Float.ToString("R", CultureInfo.InvariantCulture);
        foreach (var o in target.Options)
        {
            if (!options.TryGetValue((o.Section, o.Name), out var before) || before.Kind != o.Kind)
                throw new InvalidDataException("当前客户端不支持模板选项：" + o.Name);
            if (before != o) result.Add(new(o.Section == "System" ? "系统设置" : "角色设置", o.Name, Value(before), Value(o)));
        }
        var keys = current.Keys.ToDictionary(k => k.Id);
        foreach (var k in target.Keys)
        {
            if (!keys.TryGetValue(k.Id, out var before)) throw new InvalidDataException("当前客户端缺少模板按键：" + k.Id);
            if (before.Data != k.Data) result.Add(new("其他快捷键", "按键 " + k.Id, "当前绑定", "模板绑定"));
        }
        return result;
    }
    internal static string Fingerprint(WorkshopGameTemplate value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { value.StructsId, value.Scope, value.CurrentLayout, value.Windows, value.Options, value.Keys, value.LocalHudSnapshot }, WorkshopProfiles.Json))));
    internal static string Summary(WorkshopGameTemplate t) => $"{t.Windows.Count} 项界面布局、{t.Options.Count} 项选项、{t.Keys.Count} 组其他快捷键";
}

internal static class WorkshopGameBackupStore
{
    private sealed record Envelope(string Format, string Payload, string Sha256);
    internal static string Save(string directory, WorkshopGameTemplate value)
    {
        WorkshopGameRules.Validate(value);
        var folder = Path.Combine(directory, "HotbarWorkshopGameSettingsBackups"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, value.Source + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        var payload = JsonSerializer.Serialize(value, WorkshopProfiles.Json);
        var envelope = new Envelope("HotbarWorkshop.GameSettingsBackup", payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, WorkshopProfiles.Json));
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
            if (WorkshopGameRules.Fingerprint(Load(path, value.Source)) != WorkshopGameRules.Fingerprint(value))
                throw new IOException("游戏设置应用前备份读回不一致。");
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static WorkshopGameTemplate Load(string path, string owner)
    {
        if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("游戏设置备份过大。");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), WorkshopProfiles.Json);
            if (envelope is null || envelope.Format != "HotbarWorkshop.GameSettingsBackup" || envelope.Payload is null
                || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload))))
                throw new InvalidDataException("游戏设置备份校验失败。");
            var result = JsonSerializer.Deserialize<WorkshopGameTemplate>(envelope.Payload, WorkshopProfiles.Json)!;
            WorkshopGameRules.Validate(result);
            if (result.Source != owner) throw new InvalidDataException("应用前备份不属于当前角色。");
            return result;
        }
        catch (JsonException error) { throw new InvalidDataException("游戏设置备份格式无效。", error); }
    }
}
