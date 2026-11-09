using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools;
using OmniToolbox.Host;
using Lumina.Excel.Sheets;
using OmniToolbox.SuperHotbarInterop;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static unsafe class SuperNativeArchive
{
    public static string AddonName(int bar) => NativeHotbarIdentity.AddonName(bar);
    public static AtkUnitBase* Addon(int bar)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName(AddonName(bar));
        if (addon != null && bar != 0 && !NativeHotbarIdentity.Matches(bar, ((AddonActionBarBase*)addon)->RaptureHotbarId))
            throw new InvalidOperationException($"原生第 {bar + 1} 栏身份不匹配，已停止读写该栏。");
        return addon;
    }

    public static int ReadLayout(int bar)
    {
        if (bar is < 0 or >= 10) throw new InvalidOperationException("普通栏编号无效。");
        var addon = (AddonActionBarX*)Addon(bar);
        if (addon == null) throw new InvalidOperationException($"原生第 {bar + 1} 栏尚未加载，原布局已保留。");
        var layout = (int)addon->ActionBarLayout;
        if (!NativeHotbarLayout.TryNativeShape(layout, out _, out _))
            throw new InvalidOperationException($"原生第 {bar + 1} 栏的格数样式不可识别，原布局已保留。");
        return layout;
    }

    public static Dictionary<int, int> CaptureLayouts(ulong owner, int? onlyBar = null)
    {
        Require(owner);
        var layouts = new Dictionary<int, int>();
        if (onlyBar is { } selected) layouts.Add(selected, ReadLayout(selected));
        else
            for (var bar = 0; bar < 10; bar++)
                if (Addon(bar) != null) layouts.Add(bar, ReadLayout(bar));
        if (layouts.Count == 0) throw new InvalidOperationException("原生栏尚未加载，未修改模块布局。");
        return layouts;
    }

    public static Dictionary<int, DragHotbar> CaptureAppearance(ulong owner)
    {
        Require(owner);
        var result = new Dictionary<int, DragHotbar>();
        for (var bar = 0; bar < 10; bar++)
        {
            var addon = Addon(bar); if (addon == null) continue;
            if (!NativeHotbarIdentity.Matches(bar, ((AddonActionBarBase*)addon)->RaptureHotbarId))
                throw new InvalidOperationException("主栏正在使用特殊替换栏，请恢复普通状态后再读取外观。");
            var appearance = new DragHotbar { X = addon->X, Y = addon->Y,
                Scale = Math.Clamp(addon->Scale, 0.5f, 2.5f), Visible = addon->IsVisible, Opacity = 0 };
            NativeHotbarLayout.ApplyNativeShape(appearance, ReadLayout(bar));
            NativeBarGeometryReader.Read(bar).Store(appearance);
            result.Add(bar, appearance);
        }
        if (result.Count != 10) throw new InvalidOperationException($"目前仅加载 {result.Count}/10 条原生栏。请先退出接管并等待角色界面就绪，再校正全部外观；未修改模块档案。");
        return result;
    }
    public static RaptureHotbarModule* Require(ulong owner)
    {
        var module = RaptureHotbarModule.Instance();
        var input = UIInputData.Instance();
        if (module == null || input == null || !module->ModuleReady || module->CharacterContentId != owner
            || input->CharacterContentId != 0 && input->CharacterContentId != owner || module->ActiveHotbarClassJobId is 0 or >= 128)
            throw new InvalidOperationException("等待角色热键栏和按键配置加载完成；未读写游戏配置。");
        return module;
    }

    public static int CurrentGroup(ulong owner)
    {
        var module = Require(owner);
        var group = module->PvPHotbarsActive
            ? module->GetPvPSavedHotbarIndexForClassJobId(module->ActiveHotbarClassJobId, true)
            : module->ActiveHotbarClassJobId;
        if (group < 0 || group >= module->SavedHotbars.Length) throw new InvalidOperationException("当前职业档案索引不可用。");
        return group;
    }

    public static SuperHotbarDocument Capture(ulong owner)
    {
        var native = Require(owner);
        var input = UIInputData.Instance();
        var appearances = CaptureAppearance(owner);
        var result = new SuperHotbarDocument { Character = owner.ToString("X16"), CapturedAt = DateTimeOffset.UtcNow.ToString("O"),
            NativeGroupCount = native->SavedHotbars.Length, NativeKeybindSize = sizeof(Keybind),
            NativeStructsId = typeof(RaptureHotbarModule).Assembly.ManifestModule.ModuleVersionId.ToString("D"), AppearanceMappingVersion = 1,
            KeyboardEncodingVersion = 1, AppearanceDefaultsVersion = 1, NativeGeometryVersion = 1, EmptySlotDefaultsVersion = 1 };
        for (var g = 0; g < native->SavedHotbars.Length; g++)
        {
            var group = new SuperSavedGroup { Index = g };
            for (var b = 0; b < 18; b++)
            {
                var slots = new List<SuperSavedSlot>();
                for (var s = 0; s < 16; s++)
                {
                    var slot = native->SavedHotbars[g].Hotbars[b].Slots[s];
                    slots.Add(new() { Type = (byte)slot.CommandType, Id = slot.CommandType == 0 ? 0 : slot.CommandId });
                }
                group.Bars.Add(slots);
            }
            result.Groups.Add(group);
        }
        for (var b = 0; b < 18; b++) result.Shared[b] = native->IsHotbarShared((uint)b);
        foreach (var job in DalamudServices.DataManager.GetExcelSheet<ClassJob>())
        {
            var index = native->GetPvPSavedHotbarIndexForClassJobId(job.RowId, true);
            if (index >= 0 && index < result.NativeGroupCount) result.PvpGroups[(int)job.RowId] = index;
        }
        // Capture the current live slots too: saved data can lag a recent in-game edit.
        var current = CurrentGroup(owner);
        for (var b = 0; b < 18; b++)
        {
            var group = SuperHotbarRules.GroupFor(result, b, current);
            for (var s = 0; s < (b < 10 ? 12 : 16); s++)
            {
                var slot = native->GetSlotById((uint)b, (uint)s);
                if (slot == null) throw new InvalidOperationException("读取当前原生格子失败。");
                result.Groups[group].Bars[b][s] = new() { Type = (byte)slot->CommandType,
                    Id = slot->CommandType == 0 ? 0 : slot->CommandId };
            }
        }
        foreach (var id in Enumerable.Range(45, 146).Append(447))
        {
            if (id >= input->NumKeybinds) continue;
            var binding = input->GetKeybind((InputId)id);
            if (binding == null) continue;
            result.Bindings.Add(new() { InputId = id,
                Primary = ReadKey(binding->KeySettings[0]), Secondary = ReadKey(binding->KeySettings[1]),
                NativeData = Convert.ToBase64String(new ReadOnlySpan<byte>(binding, sizeof(Keybind))) });
        }
        var config = DService.Instance().GameConfig;
        foreach (var name in Enum.GetNames<UiConfigOption>().Where(n => n.StartsWith("Hotbar", StringComparison.Ordinal)))
            try { result.NativeOptions[name] = config.UiConfig.GetUInt(name); } catch (Exception) { /* Non-uint or unavailable options are not fabricated. */ }
        foreach (var name in Enum.GetNames<UiControlOption>().Where(n => n.StartsWith("Hotbar", StringComparison.Ordinal) || n == "ExHotbarSetting"))
            try { result.ControlOptions[name] = config.UiControl.GetUInt(name); } catch (Exception) { }
        result.Locked = result.NativeOptions.GetValueOrDefault("HotbarLock") != 0;
        for (var b = 0; b < 10; b++)
        {
            var addon = Addon(b);
            var appearance = appearances[b]; appearance.Name = $"原生热键栏 {b + 1}";
            if (b == 0) result.FirstPage = ((AddonActionBarBase*)addon)->RaptureHotbarId;
            appearance.HideEmpty = true;
            result.Appearance.Add(appearance);
        }
        result.Macros = CaptureMacros(owner);
        result.MacroBackupCharacter = owner.ToString("X16");
        result.MacroBackupName = DService.Instance().ObjectTable.LocalPlayer?.Name.ToString() ?? "";
        result.MacroBackupAt = result.CapturedAt;
        SuperHotbarRules.Validate(result, owner); return result;
    }

    public static List<SuperMacro> CaptureMacros(ulong owner)
    {
        Require(owner);
        var result = new List<SuperMacro>();
        var macros = RaptureMacroModule.Instance();
        if (macros == null) throw new InvalidOperationException("宏配置尚未就绪，未建立不完整备份。");
        for (uint set = 0; set < 2; set++)
            for (uint index = 0; index < 100; index++)
            {
                var macro = macros->GetMacro(set, index);
                if (macro == null) throw new InvalidOperationException("宏档案读取失败。");
                result.Add(new() { Set = (int)set, Index = (int)index, Icon = macro->IconId, IconRow = macro->MacroIconRowId,
                    Name = macro->Name.ToString(), Lines = macro->Lines.ToArray().Select(l => l.ToString()).ToList() });
            }
        return result;
    }

    private static SuperShortcut ReadKey(KeySetting key) =>
        SuperInputKeys.Read(SuperHotbarKeyMapRuntime.Current, (int)key.Key, (int)key.KeyModifier);

    public static void Restore(SuperHotbarDocument data, ulong owner, bool contents, bool bindings, bool options, bool macros, bool cross)
    {
        SuperHotbarRules.Validate(data, owner);
        var native = Require(owner); var input = UIInputData.Instance();
        if (native->SavedHotbars.Length != data.NativeGroupCount || sizeof(Keybind) != data.NativeKeybindSize
            || data.NativeStructsId != typeof(RaptureHotbarModule).Assembly.ManifestModule.ModuleVersionId.ToString("D"))
            throw new InvalidOperationException("客户端档案结构与备份不一致，拒绝写入。");
        if (contents && native->PvPHotbarsActive) throw new InvalidOperationException("请先离开 PvP 区域再恢复完整热键档案。");
        if (options)
        {
            if (data.AppearanceMappingVersion != 1) throw new InvalidOperationException("请先校正全部 10 栏外观，再恢复栏选项。");
            for (var b = 0; b < 10; b++)
                if (Addon(b) == null) throw new InvalidOperationException($"原生第 {b + 1} 栏尚未加载，未写回游戏。");
        }
        // Complete all structural checks before writing. Caller saves a full pre-restore snapshot first.
        if (bindings && data.Bindings.Any(b => b.InputId >= input->NumKeybinds))
            throw new InvalidOperationException("客户端按键数量与备份不一致，拒绝写入。");
        if (bindings)
            foreach (var binding in data.Bindings)
            {
                if (binding.PrimaryEdited) _ = SuperInputKeys.Native(SuperHotbarKeyMapRuntime.Current, binding.Primary.Key);
                if (binding.SecondaryEdited) _ = SuperInputKeys.Native(SuperHotbarKeyMapRuntime.Current, binding.Secondary.Key);
            }
        var macroModule = RaptureMacroModule.Instance();
        if (macros && (macroModule == null || data.Macros.Any(m => macroModule->GetMacro((uint)m.Set, (uint)m.Index) == null)))
            throw new InvalidOperationException("宏模块尚未就绪，拒绝写入。");
        if (contents)
        {
            // Config change listeners may save/reload live bars. Apply share mode before replacing the archive.
            for (var b = 0; b < (cross ? 18 : 10); b++)
            {
                var key = b < 10 ? $"HotbarCommon{b + 1:D2}" : $"HotbarCrossCommon{b - 9:D2}";
                DService.Instance().GameConfig.UiConfig.Set(key, data.Shared[b] ? 1u : 0u);
                var mask = 1 << (b % 8);
                native->HotbarShareStateBitmask[b / 8] = (byte)((native->HotbarShareStateBitmask[b / 8] & ~mask) | (data.Shared[b] ? mask : 0));
            }
            foreach (var group in data.Groups)
                for (var b = 0; b < (cross ? 18 : 10); b++)
                    for (var s = 0; s < 16; s++)
                    {
                        var source = SuperHotbarRules.EffectiveSlot(data, group.Index, b, s);
                        ref var target = ref native->SavedHotbars[group.Index].Hotbars[b].Slots[s];
                        target.CommandType = (RaptureHotbarModule.HotbarSlotType)source.Type; target.CommandId = source.Id;
                    }
            for (uint b = 0; b < (cross ? 18u : 10u); b++) native->LoadSavedHotbar(native->ActiveHotbarClassJobId, b);
            native->HasChanges = true; native->SaveFile(true);
        }
        if (bindings)
        {
            foreach (var saved in data.Bindings)
            {
                var bytes = Convert.FromBase64String(saved.NativeData);
                fixed (byte* address = bytes)
                {
                    var value = *(Keybind*)address;
                    ApplyEditedKeys(ref value, saved);
                    input->SetKeybind((InputId)saved.InputId, &value);
                }
            }
            input->HasChanges = true; input->SaveFile(true);
        }
        if (options)
        {
            var uiNames = Enum.GetNames<UiConfigOption>().ToHashSet(StringComparer.Ordinal);
            var controlNames = Enum.GetNames<UiControlOption>().ToHashSet(StringComparer.Ordinal);
            foreach (var (name, value) in data.NativeOptions)
                if (uiNames.Contains(name) && !name.StartsWith("HotbarCommon") && !name.StartsWith("HotbarCrossCommon")
                    && (cross || !name.Contains("Cross") && !name.Contains("XHB")))
                    DService.Instance().GameConfig.UiConfig.Set(name, value);
            foreach (var (name, value) in data.ControlOptions)
                if (controlNames.Contains(name) && (cross || !name.Contains("Cross") && !name.Contains("XHB")))
                    DService.Instance().GameConfig.UiControl.Set(name, value);
            DService.Instance().GameConfig.UiConfig.Set("HotbarLock", data.Locked ? 1u : 0u);
            for (var b = 0; b < 10; b++)
            {
                var addon = Addon(b); if (addon == null) continue;
                var appearance = data.Appearance[b];
                var position = data.NativeGeometryVersion == 1 ? NativeBarGeometry.FromSaved(appearance).Root
                    : new System.Numerics.Vector2(appearance.X, appearance.Y);
                addon->SetPosition((short)Math.Clamp(position.X, short.MinValue, short.MaxValue), (short)Math.Clamp(position.Y, short.MinValue, short.MaxValue));
                addon->SetScale(appearance.Scale, true);
                if (b == 0) ((AddonActionBarBase*)addon)->RaptureHotbarId = (byte)data.FirstPage;
                if (appearance.Visible) addon->Show(false, 0); else addon->Hide(false, false, 0);
                addon->SaveAddonConfig(AddonName(b), false, false);
            }
        }
        if (macros)
        {
            var module = macroModule;
            foreach (var saved in data.Macros)
            {
                var macro = module->GetMacro((uint)saved.Set, (uint)saved.Index);
                macro->Name.SetString(saved.Name); macro->IconId = saved.Icon; macro->MacroIconRowId = saved.IconRow;
                for (var line = 0; line < 15; line++) macro->Lines[line].SetString(saved.Lines[line]);
                module->SetSavePendingFlag(true, (uint)saved.Set);
            }
            module->HasChanges = true; module->SaveFile(true); native->ReloadAllMacroSlots();
        }
        // Read actual targets back. Disk persistence must additionally be confirmed after relogging in-game.
        if (contents)
        {
            foreach (var group in data.Groups)
                for (var b = 0; b < (cross ? 18 : 10); b++)
                    for (var s = 0; s < 16; s++)
                    {
                        var expected = SuperHotbarRules.EffectiveSlot(data, group.Index, b, s);
                        var actual = native->SavedHotbars[group.Index].Hotbars[b].Slots[s];
                        if ((byte)actual.CommandType != expected.Type || actual.CommandId != expected.Id)
                            throw new InvalidOperationException($"还原后核对失败：职业组 {group.Index}，栏 {b + 1}，格 {s + 1}。");
                    }
            for (uint b = 0; b < (cross ? 18u : 10u); b++)
                if (native->IsHotbarShared(b) != data.Shared[b]) throw new InvalidOperationException("公用／专属属性核对失败。");
            var current = CurrentGroup(owner);
            for (var b = 0; b < (cross ? 18 : 10); b++)
                for (var s = 0; s < (b < 10 ? 12 : 16); s++)
                {
                    var expected = SuperHotbarRules.EffectiveSlot(data, SuperHotbarRules.GroupFor(data, b, current), b, s);
                    var actual = native->GetSlotById((uint)b, (uint)s);
                    if (actual == null || (byte)actual->CommandType != expected.Type || actual->CommandId != expected.Id)
                        throw new InvalidOperationException($"当前原生栏读回核对失败：栏 {b + 1}，格 {s + 1}。");
                }
        }
        if (bindings)
            foreach (var saved in data.Bindings)
            {
                var actual = input->GetKeybind((InputId)saved.InputId);
                var bytes = Convert.FromBase64String(saved.NativeData);
                fixed (byte* address = bytes)
                {
                    var expected = *(Keybind*)address; ApplyEditedKeys(ref expected, saved);
                    if (actual == null || !new ReadOnlySpan<byte>(actual, sizeof(Keybind)).SequenceEqual(new ReadOnlySpan<byte>(&expected, sizeof(Keybind))))
                        throw new InvalidOperationException("快捷键还原后核对失败：" + saved.InputId);
                }
            }
        if (macros)
            foreach (var saved in data.Macros)
            {
                var actual = macroModule->GetMacro((uint)saved.Set, (uint)saved.Index);
                if (actual->IconId != saved.Icon || actual->MacroIconRowId != saved.IconRow || actual->Name.ToString() != saved.Name
                    || !actual->Lines.ToArray().Select(l => l.ToString()).SequenceEqual(saved.Lines))
                    throw new InvalidOperationException($"宏恢复后核对失败：{saved.Set}/{saved.Index}。");
            }
        if (options)
        {
            var uiNames = Enum.GetNames<UiConfigOption>().ToHashSet(StringComparer.Ordinal);
            var controlNames = Enum.GetNames<UiControlOption>().ToHashSet(StringComparer.Ordinal);
            foreach (var (name, value) in data.NativeOptions)
                if (uiNames.Contains(name) && !name.StartsWith("HotbarCommon") && !name.StartsWith("HotbarCrossCommon")
                    && (cross || !name.Contains("Cross") && !name.Contains("XHB")))
                {
                    var expected = name == "HotbarLock" ? data.Locked ? 1u : 0u : value;
                    if (DService.Instance().GameConfig.UiConfig.GetUInt(name) != expected)
                        throw new InvalidOperationException("原生栏选项恢复后核对失败：" + name);
                }
            foreach (var (name, value) in data.ControlOptions)
                if (controlNames.Contains(name) && (cross || !name.Contains("Cross") && !name.Contains("XHB")))
                    if (DService.Instance().GameConfig.UiControl.GetUInt(name) != value)
                        throw new InvalidOperationException("原生栏控制选项恢复后核对失败：" + name);
        }
    }

    internal static byte[] ExpectedBindingBytes(SuperBinding saved)
    {
        var bytes = Convert.FromBase64String(saved.NativeData);
        if (bytes.Length != sizeof(Keybind)) throw new InvalidOperationException("原生绑定字节数不匹配，未写回游戏。");
        fixed (byte* address = bytes)
        {
            var value = *(Keybind*)address; ApplyEditedKeys(ref value, saved);
            new ReadOnlySpan<byte>(&value, sizeof(Keybind)).CopyTo(bytes);
        }
        return bytes;
    }

    internal static bool RestoreSavePending(ulong owner, SuperRestoreScope scope)
    {
        var native = Require(owner); var input = UIInputData.Instance(); var macros = RaptureMacroModule.Instance();
        return scope.Contents && (native->HasChanges || native->IsSavePending)
            || scope.Bindings && (input->HasChanges || input->IsSavePending)
            || scope.Macros && macros != null && (macros->HasChanges || macros->IsSavePending);
    }

    private static void ApplyEditedKeys(ref Keybind value, SuperBinding saved)
    {
        if (saved.PrimaryEdited)
        { value.KeySettings[0].Key = (SeVirtualKey)SuperInputKeys.Native(SuperHotbarKeyMapRuntime.Current, saved.Primary.Key); value.KeySettings[0].KeyModifier = (KeyModifierFlag)saved.Primary.Modifiers; }
        if (saved.SecondaryEdited)
        { value.KeySettings[1].Key = (SeVirtualKey)SuperInputKeys.Native(SuperHotbarKeyMapRuntime.Current, saved.Secondary.Key); value.KeySettings[1].KeyModifier = (KeyModifierFlag)saved.Secondary.Modifiers; }
    }

    public static int OtherBindingConflict(SuperShortcut shortcut)
    {
        var input = UIInputData.Instance(); if (input == null || shortcut.Key == 0) return -1;
        if (!SuperInputKeys.TryNative(SuperHotbarKeyMapRuntime.Current, shortcut.Key, out var nativeKey)) return -1;
        for (var i = 0; i < input->NumKeybinds; i++)
        {
            if (SuperHotbarRules.HotbarInput(i)) continue;
            var binding = input->GetKeybind(i); if (binding == null) continue;
            foreach (var key in binding->KeySettings)
                if ((int)key.Key == nativeKey && (int)key.KeyModifier == shortcut.Modifiers) return i;
        }
        return -1;
    }
}
