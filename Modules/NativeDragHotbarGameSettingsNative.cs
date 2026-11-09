using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static unsafe class WorkshopGameNative
{
    private static string StructsId => typeof(AddonConfig).Assembly.ManifestModule.ModuleVersionId.ToString("D");
    internal static void Require(ulong owner)
    {
        if (owner == 0) throw new InvalidOperationException("请先登录角色。");
        SuperNativeArchive.Require(owner);
        var addon = AddonConfig.Instance(); var atk = RaptureAtkModule.Instance();
        if (addon == null || !addon->IsLoaded || addon->ActiveDataSet == null || addon->CharacterContentId != owner
            || atk == null || !atk->IsUiVisible || NativeBarGeometryReader.HudEditing())
            throw new InvalidOperationException("等待角色界面加载完成，并先保存、关闭游戏界面设置。");
        var condition = DService.Instance().Condition;
        if (condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]
            || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
            || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene]
            || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent])
            throw new InvalidOperationException("请在非战斗、非过场且角色可操作时应用游戏设置。");
    }
    private static (Dictionary<uint, string> Names, HashSet<string> Hud) Names()
    {
        var atk = RaptureAtkModule.Instance();
        if (atk == null || atk->AddonNames.LongCount is < 1 or > 2048) throw new InvalidOperationException("游戏界面名称表未就绪。");
        var result = new Dictionary<uint, string>(); var hud = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (!WorkshopGameRules.WindowAllowed(name)) return;
            var hash = WorkshopGameRules.NameHash(name);
            if (result.TryGetValue(hash, out var old) && old != name) throw new InvalidOperationException("界面名称标识冲突。");
            result[hash] = name;
        }
        for (var i = 0; i < atk->AddonNames.LongCount; i++) Add(atk->AddonNames[i].ToString());
        var layouts = HudLayoutAddon.GetSpan();
        if (layouts.Length is < 1 or > 112) throw new InvalidOperationException("客户端 HUD 名称表不兼容。");
        foreach (var item in layouts)
        {
            var name = item.AddonName.ToString();
            Add(name); if (WorkshopGameRules.WindowAllowed(name)) hud.Add(name);
        }
        return (result, hud);
    }
    private static WorkshopGameWindow Read(AddonConfigEntry e, string name, int layout) => new(name, layout,
        e.AddonNameHash, e.X, e.Y, e.Scale, e.ElementFlags, e.Width, e.Height, e.ByteValue1, e.ByteValue2, e.ByteValue3, e.Alpha);
    private static void Write(ref AddonConfigEntry entry, WorkshopGameWindow value)
    {
        entry.AddonNameHash = value.Hash; entry.X = value.X; entry.Y = value.Y; entry.Scale = value.Scale;
        entry.ElementFlags = value.Flags; entry.Width = value.Width; entry.Height = value.Height;
        entry.ByteValue1 = value.Byte1; entry.ByteValue2 = value.Byte2; entry.ByteValue3 = value.Byte3;
        entry.Alpha = value.Alpha; entry.HasValue = true;
        // IsOpen is live state belonging to the target character, never portable.
    }
    internal static void CaptureWindows(WorkshopGameTemplate result, AddonConfigDataSet* set,
        Dictionary<uint, string> names, HashSet<string> hud, bool localBackup)
    {
        // ConfigEntries is storage, not the active entry list. The client can leave
        // HasValue/data in recycled cells; only UsedAddonConfigEntries is authoritative.
        var storage = set->ConfigEntries;
        var count = set->UsedAddonConfigEntries.LongCount;
        if (count < 0 || count > storage.Length) throw new InvalidOperationException("游戏窗口列表未就绪，请稍后重试。");
        var visited = new HashSet<nint>(); var read = 0;
        if (count > 0) fixed (AddonConfigEntry* first = storage)
            foreach (var pointer in set->UsedAddonConfigEntries)
            {
                var entry = pointer.Value;
                var address = (nuint)entry; var start = (nuint)first;
                if (++read > count || address < start || address - start >= (nuint)(storage.Length * sizeof(AddonConfigEntry))
                    || (address - start) % (nuint)sizeof(AddonConfigEntry) != 0 || !visited.Add((nint)entry))
                    throw new InvalidOperationException("游戏窗口列表正在变化，请稍后重试。");
                if (entry->HasValue && names.TryGetValue(entry->AddonNameHash, out var name) && !hud.Contains(name))
                    result.Windows.Add(Read(*entry, name, -1));
            }
        if (read != count) throw new InvalidOperationException("游戏窗口列表读取不完整，请稍后重试。");
        var entries = set->HudLayoutConfigEntries;
        if (localBackup) result.LocalHudSnapshot = [];
        for (var layout = 0; layout < 4; layout++)
            for (var i = layout * 112; i < (layout + 1) * 112 && entries[i].HasValue; i++)
            {
                // Each HUD is a HasValue-terminated prefix, just like ApplyHudLayout's
                // native search. Stale cells beyond its terminator are not settings.
                if (names.TryGetValue(entries[i].AddonNameHash, out var name) && hud.Contains(name))
                    result.Windows.Add(Read(entries[i], name, layout));
                result.LocalHudSnapshot?.Add(Read(entries[i], "", layout));
            }
        result.Windows = result.Windows.OrderBy(w => w.Layout).ThenBy(w => w.Name, StringComparer.Ordinal).ToList();
    }
    internal static WorkshopGameTemplate Capture(ulong owner, WorkshopGameScope scope, bool localBackup = true)
    {
        Require(owner);
        var addon = AddonConfig.Instance(); var input = UIInputData.Instance();
        if (input == null || input->NumKeybinds is < 1 or > 4096) throw new InvalidOperationException("按键配置未就绪。");
        var result = new WorkshopGameTemplate { Source = owner.ToString("X16"), SourceName = DService.Instance().ObjectTable.LocalPlayer?.Name.ToString() ?? "",
            CapturedAt = DateTimeOffset.UtcNow.ToString("O"), StructsId = StructsId, KeySize = sizeof(Keybind), KeyCount = input->NumKeybinds,
            CurrentLayout = addon->ActiveDataSet->CurrentHudLayout, Scope = scope };
        if (scope.Layout)
        {
            var names = Names();
            CaptureWindows(result, addon->ActiveDataSet, names.Names, names.Hud, localBackup);
            if (!localBackup && !result.Windows.Any(w => w.Layout == result.CurrentLayout))
                throw new InvalidOperationException("没有读到当前已保存的 HUD 布局，请先在游戏界面设置中保存布局后重试。");
        }
        void Options(GameConfigSection section, string group, string[] names)
        {
            foreach (var name in names.Where(n => WorkshopGameRules.OptionAllowed(group, n)).Order(StringComparer.Ordinal))
            {
                if (section.TryGetProperties(name, out UIntConfigProperties? _) && section.TryGetUInt(name, out var u))
                    result.Options.Add(new(group, name, "UInt", u));
                else if (section.TryGetProperties(name, out FloatConfigProperties? _) && section.TryGetFloat(name, out var f))
                    result.Options.Add(new(group, name, "Float", Float: f));
                // String options can contain account, device or other character data; never copy them.
            }
        }
        var config = DService.Instance().GameConfig;
        if (scope.Character)
        { Options(config.UiConfig, "Ui", Enum.GetNames<UiConfigOption>()); Options(config.UiControl, "Control", Enum.GetNames<UiControlOption>()); }
        if (scope.System) Options(config.System, "System", Enum.GetNames<SystemConfigOption>());
        if (scope.Bindings)
            for (var id = 0; id < input->NumKeybinds; id++)
            {
                if (!WorkshopGameRules.KeyAllowed(id)) continue;
                var key = input->GetKeybind((InputId)id);
                if (key == null) throw new InvalidOperationException("其他按键配置读取不完整。");
                result.Keys.Add(new(id, Convert.ToBase64String(new ReadOnlySpan<byte>(key, sizeof(Keybind)))));
            }
        WorkshopGameRules.Validate(result); return result;
    }
    private static GameConfigSection Section(string name) => name switch
    {
        "Ui" => DService.Instance().GameConfig.UiConfig, "Control" => DService.Instance().GameConfig.UiControl,
        "System" => DService.Instance().GameConfig.System, _ => throw new InvalidDataException("游戏选项分区无效。"),
    };
    private static List<AddonConfigEntry>[] HudPlan(WorkshopGameTemplate template, AddonConfigDataSet* set)
    {
        var plan = new List<AddonConfigEntry>[4];
        for (var layout = 0; layout < 4; layout++)
        {
            if (template.LocalHudSnapshot is { } snapshot)
            {
                var exact = new List<AddonConfigEntry>();
                foreach (var value in snapshot.Where(w => w.Layout == layout))
                { var entry = new AddonConfigEntry(); Write(ref entry, value); exact.Add(entry); }
                plan[layout] = exact; continue;
            }
            var values = template.Windows.Where(w => w.Layout == layout).ToArray();
            var hashes = values.Select(w => w.Hash).ToHashSet();
            var entries = new List<AddonConfigEntry>();
            foreach (var value in values)
            { var entry = new AddonConfigEntry(); Write(ref entry, value); entries.Add(entry); }
            // Preserve native action bars and HUD entries not represented in this template.
            for (var i = layout * 112; i < (layout + 1) * 112 && set->HudLayoutConfigEntries[i].HasValue; i++)
            {
                var entry = set->HudLayoutConfigEntries[i];
                if (entry.HasValue && hashes.Add(entry.AddonNameHash)) entries.Add(entry);
            }
            if (entries.Count > 112) throw new InvalidOperationException("目标角色 HUD 条目不足以合并模板，原布局未改动。");
            plan[layout] = entries;
        }
        return plan;
    }
    internal static void Preflight(WorkshopGameTemplate template, ulong owner)
    {
        Require(owner); WorkshopGameRules.Validate(template);
        if (template.LocalHudSnapshot is not null && !template.Source.Equals(owner.ToString("X16"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("完整应用前 HUD 备份只允许恢复到原来的角色。");
        var input = UIInputData.Instance();
        if (template.StructsId != StructsId || template.KeySize != sizeof(Keybind) || template.KeyCount != input->NumKeybinds)
            throw new InvalidOperationException("模板与当前客户端结构不一致，请重新读取游戏设置模板。");
        if (template.Scope.Layout)
        {
            var names = Names();
            foreach (var w in template.Windows)
                if (!names.Names.TryGetValue(w.Hash, out var name) || name != w.Name || (w.Layout >= 0) != names.Hud.Contains(name))
                    throw new InvalidOperationException("当前客户端不支持模板界面：" + w.Name);
            _ = HudPlan(template, AddonConfig.Instance()->ActiveDataSet);
        }
        foreach (var option in template.Options)
        {
            var section = Section(option.Section);
            var known = option.Section switch { "Ui" => Enum.IsDefined(typeof(UiConfigOption), option.Name),
                "Control" => Enum.IsDefined(typeof(UiControlOption), option.Name), _ => Enum.IsDefined(typeof(SystemConfigOption), option.Name) };
            if (!known || (option.Kind == "UInt" ? !section.TryGetProperties(option.Name, out UIntConfigProperties? u)
                || u is null || !section.TryGetUInt(option.Name, out var currentUInt)
                || !WorkshopGameRules.UIntCompatible(option.UInt, currentUInt, u.Default, u.Minimum, u.Maximum)
                : !section.TryGetProperties(option.Name, out FloatConfigProperties? f)
                || f is null || !section.TryGetFloat(option.Name, out var currentFloat)
                || !WorkshopGameRules.FloatCompatible(option.Float, currentFloat, f.Default, f.Minimum, f.Maximum)))
                throw new InvalidOperationException("游戏选项类型或取值范围不兼容：" + option.Name);
        }
    }
    internal static void Apply(WorkshopGameTemplate template, WorkshopGameTemplate before, ulong owner)
    {
        Preflight(template, owner); Preflight(before, owner);
        try
        {
            ApplyCore(template, owner);
            if (WorkshopGameRules.Compare(Capture(owner, template.Scope), template).Count != 0)
                throw new IOException("游戏设置读回核对失败。");
        }
        catch (Exception error)
        {
            try { ApplyCore(before, owner, true); }
            catch (Exception rollback) { throw new IOException("游戏设置应用中断，自动恢复也未完成。请使用应用前备份核对并恢复：" + rollback.Message, error); }
            throw new IOException("游戏设置应用未完成，已尝试恢复应用前设置；请核对角色界面。原因：" + error.Message, error);
        }
    }
    private static void ApplyCore(WorkshopGameTemplate template, ulong owner, bool rollback = false)
    {
        Require(owner); var addon = AddonConfig.Instance(); var input = UIInputData.Instance();
        // A read-back failure must not prevent restoration of the already validated
        // application-before backup by trying to read the same broken state again.
        var current = rollback ? null : Capture(owner, template.Scope);
        var actualWindows = current?.Windows.ToDictionary(w => (w.Layout, w.Name));
        var layoutChanged = template.Scope.Layout && (rollback || current!.CurrentLayout != template.CurrentLayout
            || template.Windows.Any(w => !actualWindows!.TryGetValue((w.Layout, w.Name), out var old) || old != w)
            || template.LocalHudSnapshot is { } exact && !exact.SequenceEqual(current!.LocalHudSnapshot ?? []));
        var hud = layoutChanged ? HudPlan(template, addon->ActiveDataSet) : null;
        foreach (var option in template.Options)
        {
            var section = Section(option.Section);
            if (option.Kind == "UInt") { if (section.GetUInt(option.Name) != option.UInt) section.Set(option.Name, option.UInt); }
            else if (section.GetFloat(option.Name) != option.Float) section.Set(option.Name, option.Float);
        }
        if (template.Scope.Bindings)
        {
            var changed = false;
            foreach (var key in template.Keys)
            {
                var bytes = Convert.FromBase64String(key.Data);
                var existing = input->GetKeybind((InputId)key.Id);
                if (existing == null) throw new InvalidOperationException("按键配置已变化，停止本次应用。");
                if (new ReadOnlySpan<byte>(existing, sizeof(Keybind)).SequenceEqual(bytes)) continue;
                fixed (byte* ptr = bytes) input->SetKeybind((InputId)key.Id, (Keybind*)ptr);
                changed = true;
            }
            if (changed) { input->HasChanges = true; input->SaveFile(true); }
        }
        if (hud is not null)
        {
            foreach (var window in template.Windows.Where(w => w.Layout < 0))
            {
                // This native accessor maintains used/unused lists; never copy those lists or pointers.
                var entry = addon->GetConfigEntryByAddonName(window.Name);
                if (entry == null) throw new IOException("无法分配游戏窗口设置：" + window.Name);
                Write(ref *entry, window);
            }
            for (var layout = 0; layout < 4; layout++)
                for (var i = 0; i < 112; i++)
                    addon->ActiveDataSet->HudLayoutConfigEntries[layout * 112 + i] = i < hud[layout].Count ? hud[layout][i] : default;
            addon->ActiveDataSet->CurrentHudLayout = template.CurrentLayout;
            addon->ApplyHudLayout(); addon->HasChanges = true; addon->SaveFile(true);
        }
    }
    internal static bool SavePending(ulong owner, WorkshopGameScope scope)
    {
        Require(owner); var addon = AddonConfig.Instance(); var input = UIInputData.Instance();
        return scope.Layout && (addon->HasChanges || addon->IsSavePending)
            || scope.Bindings && (input->HasChanges || input->IsSavePending);
    }
}
