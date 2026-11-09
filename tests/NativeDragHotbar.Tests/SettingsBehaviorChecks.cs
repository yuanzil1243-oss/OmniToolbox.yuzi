using System.Collections;
using System.Reflection;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class SettingsBehaviorChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(HotbarCommandFeedback.Format(HotbarCommandFeedback.Visibility(false, 1, "套装设计", true))
            == "扩展栏 #2 套装设计 已显示。", "extension command feedback has no module prefix and preserves scope, number, name and state");
        check(HotbarCommandFeedback.Format(HotbarCommandFeedback.Visibility(true, 0, "第 1 栏", false))
            == "接管栏 #1 第 1 栏 已隐藏。", "takeover command uses the identical prefix-free feedback format");
        foreach (var takeover in new[] { false, true })
            foreach (var visible in new[] { false, true })
                check(HotbarCommandFeedback.Format(HotbarCommandFeedback.Visibility(takeover, -1, "ignored", visible))
                    == "全部" + (takeover ? "接管栏" : "扩展栏") + (visible ? " 已显示。" : " 已隐藏。"),
                    "whole-scope feedback distinguishes both bar kinds and states");
        check(HotbarCommandFeedback.Format("未找到栏位。") == "未找到栏位。"
            && HotbarCommandFeedback.Format("用法") == "用法。", "errors and usage omit module prefix and retain terminal punctuation");
    }

    public static void Compiled(string root, Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar", true)!;
        var module = Activator.CreateInstance(type)!;
        object? Get(string name) => type.GetField(name, flags)!.GetValue(module);
        object? Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(module, args);
        check(!(bool)Get("superSettingsExpanded")! && !(bool)Get("sharedSettingsExpanded")!,
            "fresh module keeps layout, bindings and extension cards collapsed");
        type.GetField("superSettingsExpanded", flags)!.SetValue(module, true);
        type.GetField("sharedSettingsExpanded", flags)!.SetValue(module, true);
        Call("CollapseSettingsBars");
        check(!(bool)Get("superSettingsExpanded")! && !(bool)Get("sharedSettingsExpanded")!,
            "starting again collapses previously expanded native and extension cards");
        var config = Get("config")!;
        var messages = config.GetType().GetField("ShowCommandMessages")!;
        check((bool)messages.GetValue(config)! && config.GetType().GetField("ShowHelp") is null,
            "new and legacy host configurations default to feedback enabled and have no help toggle");
        messages.SetValue(config, false);
        Call("PrintCommandMessage", "must stay silent without game services");
        check(true, "disabled command feedback avoids chat service completely");
        check((bool)type.GetMethod("ResetSettings")!.Invoke(module, null)! && (bool)messages.GetValue(config)!
            && !(bool)type.GetMethod("ResetSettings")!.Invoke(module, null)!, "reset restores feedback default only when needed");
        var profile = Get("profile")!;
        var extra = ((IList)profile.GetType().GetProperty("Bars")!.GetValue(profile)!)[0]!;
        var archiveField = type.GetField("super", flags)!;
        var archive = Activator.CreateInstance(archiveField.FieldType)!;
        archiveField.SetValue(module, archive);
        var native = Activator.CreateInstance(extra.GetType())!;
        ((IList)archive.GetType().GetProperty("Appearance")!.GetValue(archive)!).Add(native);
        archive.GetType().GetProperty("Locked")!.SetValue(archive, true);
        check(!(bool)Call("BarEditingLocked", extra)! && (bool)Call("BarEditingLocked", native)!,
            "actual compiled lock policy leaves extensions editable while takeover is locked");
        Call("OpenEditor", extra, 0);
        var original = ((IList)extra.GetType().GetProperty("Slots")!.GetValue(extra)!)[0]!;
        check((bool)Get("editorOpen")! && ReferenceEquals(Get("editBar"), extra) && (int)Get("editIndex")! == 0
            && !ReferenceEquals(Get("draft"), original), "actual extension editor opens with a separate draft under takeover lock");
        Call("CloseEditor"); Call("OpenEditor", native, 0);
        check(!(bool)Get("editorOpen")!, "locked takeover cannot open an edit session");
        Call("OpenEditor", extra, -1); Call("OpenEditor", extra, 120);
        check(!(bool)Get("editorOpen")!, "invalid extension slot cannot open an edit session");
        archive.GetType().GetProperty("Locked")!.SetValue(archive, false);
        check(!(bool)Call("BarEditingLocked", native)!, "unlocking takeover re-enables its own edit policy");
        var source = File.ReadAllText(Path.Combine(root, "Modules/NativeDragHotbar.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "Modules/NativeDragHotbarSettings.cs"));
        check(!source.Contains("ReadNativeHotbarLock") && !source.Contains("nativeHotbarLocked")
            && source.Contains("ImGui.IsItemClicked(ImGuiMouseButton.Right)"),
            "extension editing has no game-lock dependency and settings slots support right clicks");
        check(source.Split("DalamudServices.ChatGUI.Print(").Length == 2
            && source.Contains("if (config.ShowCommandMessages) DalamudServices.ChatGUI.Print"),
            "every module command routes chat text through the single feedback switch");
        check(!settings.Contains("config.ShowHelp") && settings.Contains("ImGui.Selectable(command)")
            && settings.Contains("ImGui.TextWrapped(L(\"Help\")); ImGui.TextWrapped(L(\"Help2\"));"),
            "instructions render unconditionally and command labels are clickable copy targets");

        var libraryField = type.GetField("workshopLibrary", flags)!;
        var selected = Guid.NewGuid().ToString("N");
        var library = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { SelectedId = selected,
            Profiles = new[] { new { Id = selected, Name = "确认测试" } } }), libraryField.FieldType)!;
        libraryField.SetValue(module, library); type.GetField("owner", flags)!.SetValue(module, 1UL);
        var unchanged = JsonSerializer.Serialize(archive, archiveField.FieldType);
        Call("BeginSuperAppearanceRepair");
        check((bool)Get("superAppearanceConfirm")! && JsonSerializer.Serialize(archive, archiveField.FieldType) == unchanged,
            "opening reread-all confirmation does not access native game state or change saved layout");
        type.GetField("superSettingsTab", flags)!.SetValue(module, 1); Call("EnterSuperSettingsTab", 0);
        check(!(bool)Get("superAppearanceConfirm")! && JsonSerializer.Serialize(archive, archiveField.FieldType) == unchanged,
            "leaving layout tab cancels reread-all without layout changes");
        Call("BeginSuperAppearanceRepair"); type.GetField("owner", flags)!.SetValue(module, 2UL);
        Call("ConfirmSuperAppearanceRepair");
        check(!(bool)Get("superAppearanceConfirm")! && JsonSerializer.Serialize(archive, archiveField.FieldType) == unchanged,
            "confirmation from another character cannot invoke native reread or overwrite geometry");
        Call("BeginSuperAppearanceRepair"); library.GetType().GetProperty("SelectedId")!.SetValue(library, Guid.NewGuid().ToString("N"));
        Call("ConfirmSuperAppearanceRepair");
        check(!(bool)Get("superAppearanceConfirm")! && JsonSerializer.Serialize(archive, archiveField.FieldType) == unchanged,
            "confirmation from another selected profile cannot invoke native reread or overwrite geometry");
        library.GetType().GetProperty("SelectedId")!.SetValue(library, selected);
        Call("BeginSuperAppearanceRepair"); Call("ClearWorkshopOperations"); Call("ConfirmSuperAppearanceRepair");
        check(!(bool)Get("superAppearanceConfirm")! && JsonSerializer.Serialize(archive, archiveField.FieldType) == unchanged,
            "cancelled confirmation cannot later execute a stale native reread");
    }
}
