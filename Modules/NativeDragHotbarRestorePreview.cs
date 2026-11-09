using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed record SuperRestoreScope(bool Contents, bool Bindings, bool Options, bool Macros, bool Cross);
internal sealed record SuperRestorePreviewRow(string Category, string Location, string Before, string After,
    string BeforeToken, string AfterToken, SuperSavedSlot? BeforeSlot = null, SuperSavedSlot? AfterSlot = null)
{
    internal bool Changed => BeforeToken != AfterToken;
}

internal sealed class SuperRestorePreview
{
    internal SuperRestoreScope Scope { get; }
    internal List<SuperRestorePreviewRow> Rows { get; } = [];
    internal int ComparedFields { get; private set; }
    internal int Changes => Rows.Count(r => r.Changed);
    internal int UnsupportedCommands { get; private set; }
    private SuperRestorePreview(SuperRestoreScope scope) => Scope = scope;
    private void Add(string category, string location, string before, string after,
        string? beforeToken = null, string? afterToken = null, SuperSavedSlot? oldSlot = null, SuperSavedSlot? newSlot = null)
    {
        ComparedFields++;
        beforeToken ??= before; afterToken ??= after;
        if (beforeToken != afterToken)
            Rows.Add(new(category, location, before, after, beforeToken, afterToken, oldSlot?.Copy(), newSlot?.Copy()));
    }
    internal bool SameAs(SuperRestorePreview other) => Scope == other.Scope && Changes == other.Changes
        && UnsupportedCommands == other.UnsupportedCommands
        && Rows.Select(r => (r.Category, r.Location, r.BeforeToken, r.AfterToken))
            .SequenceEqual(other.Rows.Select(r => (r.Category, r.Location, r.BeforeToken, r.AfterToken)));

    internal static bool OptionIncluded(string name, bool cross, bool control) =>
        (control || !name.StartsWith("HotbarCommon", StringComparison.Ordinal)
            && !name.StartsWith("HotbarCrossCommon", StringComparison.Ordinal))
        && (cross || !name.Contains("Cross", StringComparison.Ordinal) && !name.Contains("XHB", StringComparison.Ordinal));

    internal static SuperRestorePreview Build(SuperHotbarDocument current, SuperHotbarDocument target,
        SuperRestoreScope scope, Func<SuperBinding, byte[]> expectedBinding)
    {
        var result = new SuperRestorePreview(scope);
        var bars = scope.Cross ? 18 : 10;
        if (scope.Contents)
        {
            for (var b = 0; b < bars; b++) result.Add("公用属性", $"第 {b + 1} 栏",
                current.Shared[b] ? "公用栏" : "职业专属栏", target.Shared[b] ? "公用栏" : "职业专属栏");
            var groups = current.Groups.ToDictionary(g => g.Index);
            foreach (var group in target.Groups.OrderBy(g => g.Index))
            {
                if (!groups.TryGetValue(group.Index, out var actual)) throw new InvalidOperationException("当前职业档案组不完整，无法预览还原。");
                for (var b = 0; b < bars; b++) for (var s = 0; s < 16; s++)
                {
                    var before = actual.Bars[b][s];
                    var after = SuperHotbarRules.EffectiveSlot(target, group.Index, b, s);
                    var location = $"职业组 {group.Index} · 第 {b + 1} 栏 / 格 {s + 1}";
                    result.Add("内容", location, SlotText(before), SlotText(after), oldSlot: before, newSlot: after);
                }
            }
            result.UnsupportedCommands = target.EditedBars.Where(k =>
                int.TryParse(k.Key.Split(':').Last(), out var b) && b < bars)
                .Sum(k => k.Value.Count(e => !e.HasNative && e.PreservedNativeType == 0 && e.Command.Length > 0));
        }
        if (scope.Bindings)
        {
            var bindings = current.Bindings.ToDictionary(b => b.InputId);
            foreach (var binding in target.Bindings.OrderBy(b => b.InputId))
            {
                if (!bindings.TryGetValue(binding.InputId, out var actual)) throw new InvalidOperationException("当前原生绑定不完整，无法预览还原。");
                var beforeBytes = Convert.FromBase64String(actual.NativeData);
                var afterBytes = expectedBinding(binding);
                var sameKeys = KeyText(actual) == KeyText(binding);
                result.Add("快捷键", BindingLocation(binding.InputId), KeyText(actual), KeyText(binding)
                    + (sameKeys && !beforeBytes.SequenceEqual(afterBytes) ? "（完整原生绑定数据不同）" : ""),
                    Convert.ToHexString(beforeBytes), Convert.ToHexString(afterBytes));
            }
        }
        if (scope.Options)
        {
            foreach (var (name, value) in target.NativeOptions.OrderBy(p => p.Key))
                if (current.NativeOptions.ContainsKey(name) && OptionIncluded(name, scope.Cross, false))
                    result.Add("栏选项", name, current.NativeOptions[name].ToString(),
                        (name == "HotbarLock" ? target.Locked ? 1u : 0u : value).ToString());
            foreach (var (name, value) in target.ControlOptions.OrderBy(p => p.Key))
                if (current.ControlOptions.ContainsKey(name) && OptionIncluded(name, scope.Cross, true))
                    result.Add("控制选项", name, current.ControlOptions[name].ToString(), value.ToString());
            for (var b = 0; b < 10; b++) result.Add("HUD 显示", $"第 {b + 1} 栏", Appearance(current.Appearance[b]), Appearance(target.Appearance[b]));
            result.Add("HUD 主栏页", "第 1 栏", (current.FirstPage + 1).ToString(), (target.FirstPage + 1).ToString());
        }
        if (scope.Macros)
        {
            var macros = current.Macros.ToDictionary(m => (m.Set, m.Index));
            foreach (var macro in target.Macros.OrderBy(m => m.Set).ThenBy(m => m.Index))
            {
                if (!macros.TryGetValue((macro.Set, macro.Index), out var actual)) throw new InvalidOperationException("当前宏档案不完整，无法预览还原。");
                result.Add("宏", $"{(macro.Set == 0 ? "个人" : "公用")}宏 {macro.Index + 1}", MacroText(actual), MacroText(macro));
            }
        }
        return result;
    }
    private static string Appearance(DragHotbar bar) => string.Create(CultureInfo.InvariantCulture,
        $"{(bar.Visible ? "显示" : "隐藏")} · X {bar.X:0.##}, Y {bar.Y:0.##} · 缩放 {bar.Scale:0.###}");
    private static string MacroText(SuperMacro macro) => $"{macro.Name} · 图标 {macro.Icon}/{macro.IconRow}\n" + string.Join("\n", macro.Lines);
    private static string KeyText(SuperBinding key) => $"主：{key.Primary}；副：{key.Secondary}";
    internal static string BindingLocation(int id) => NativeHotbarIdentity.TryInput(id, out var bar, out var slot)
        ? $"第 {bar + 1} 栏 / 格 {slot + 1}" : $"原生输入 {id}";
    private static string SlotText(SuperSavedSlot slot) => slot.Type == 0 ? "空格" : $"类型 {slot.Type} / ID {slot.Id}";
}
