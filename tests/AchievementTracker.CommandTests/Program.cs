using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using OmniToolbox.AchievementTrackerOnline;

var checks = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, message); return; }
    throw new Exception("Expected rejection: " + message);
}

var commands = new TestCommandManager();
var forwarded = new List<(string Command, string Arguments)>();
var toggles = 0;
var original = new CommandInfo((command, arguments) => forwarded.Add((command, arguments)))
{
    HelpMessage = "existing Omni help",
    ShowInHelp = false,
    AllowedInMacros = true,
    DisplayOrder = 7,
};
commands.AddHandler("/omni", original);
var route = AchievementTrackerCommandRoute.Attach(commands, () => toggles++);
var wrapped = commands.Commands["/omni"];
Check(wrapped.HelpMessage == original.HelpMessage && wrapped.ShowInHelp == original.ShowInHelp
    && wrapped.AllowedInMacros == original.AllowedInMacros && wrapped.DisplayOrder == original.DisplayOrder,
    "original help and macro settings survive registration");
commands.ProcessCommand("/omni 成就追踪悬浮窗");
commands.ProcessCommand("/omni   成就追踪悬浮窗  ");
Check(toggles == 2 && forwarded.Count == 0, "Chinese alias toggles without falling through to Omni help");
commands.ProcessCommand("/omni AchievementTracker overlay");
commands.ProcessCommand("/omni achievementtracker OVERLAY");
commands.ProcessCommand("/omni AchievementTracker 成就追踪悬浮窗");
Check(toggles == 2 && forwarded.Count == 3
    && forwarded[0] == ("/omni", "AchievementTracker overlay")
    && forwarded[1] == ("/omni", "achievementtracker OVERLAY")
    && forwarded[2] == ("/omni", "AchievementTracker 成就追踪悬浮窗"),
    "removed module-prefixed routes are forwarded unchanged without toggling");
foreach (var arguments in new[] { "", "help", "CustomHotbar toggle 1", "成就追踪悬浮窗 extra", "AchievementTracker", "AchievementTracker overlay extra", "AchievementTracker disable", "成就追踪" })
{
    var count = forwarded.Count;
    commands.DispatchCommand("/omni", arguments, wrapped);
    Check(forwarded.Count == count + 1 && forwarded[^1] == ("/omni", arguments) && toggles == 2,
        $"unrelated input is forwarded unchanged: [{arguments}]");
}
route.Dispose();
route.Dispose();
Check(ReferenceEquals(commands.Commands["/omni"], original), "disabling twice restores the original handler exactly once");
wrapped.Handler("/omni", "成就追踪悬浮窗");
Check(toggles == 2 && forwarded[^1].Arguments == "成就追踪悬浮窗", "a retained disabled wrapper only forwards");

using (var enabledAgain = AchievementTrackerCommandRoute.Attach(commands, () => toggles++))
{
    commands.ProcessCommand("/omni 成就追踪悬浮窗");
    Check(toggles == 3, "reenabling toggles once without stacking old callbacks");
}
Check(ReferenceEquals(commands.Commands["/omni"], original), "reenabled registration is restored on disposal");

var chained = AchievementTrackerCommandRoute.Attach(commands, () => toggles++);
var inner = commands.Commands["/omni"];
var external = new CommandInfo((command, arguments) => inner.Handler(command, arguments));
commands.RemoveHandler("/omni");
commands.AddHandler("/omni", external);
chained.Dispose();
commands.ProcessCommand("/omni 成就追踪悬浮窗");
Check(ReferenceEquals(commands.Commands["/omni"], external) && toggles == 3 && forwarded[^1].Arguments == "成就追踪悬浮窗",
    "disabling preserves later registrations and deactivates retained callbacks");
commands.RemoveHandler("/omni");
commands.AddHandler("/omni", original);

commands.FailNextAdd = true;
Reject(() => AchievementTrackerCommandRoute.Attach(commands, () => toggles++), "registration failure is reported");
Check(ReferenceEquals(commands.Commands["/omni"], original), "failed registration rolls back to the original command");
commands.FailNextRemove = true;
Reject(() => AchievementTrackerCommandRoute.Attach(commands, () => toggles++), "removal failure is reported");
Check(ReferenceEquals(commands.Commands["/omni"], original), "failed removal leaves the original command available");

var recovery = AchievementTrackerCommandRoute.Attach(commands, () => toggles++);
commands.FailNextAdd = true;
Reject(recovery.Dispose, "restoration failure is reported");
commands.ProcessCommand("/omni 成就追踪悬浮窗");
Check(commands.Commands.ContainsKey("/omni") && toggles == 3 && forwarded[^1].Arguments == "成就追踪悬浮窗",
    "a failed restore retains an inactive forwarding command");
recovery.Dispose();
Check(ReferenceEquals(commands.Commands["/omni"], original), "restoration can be retried after transient failure");
commands.RemoveHandler("/omni");
Reject(() => AchievementTrackerCommandRoute.Attach(commands, () => toggles++), "missing host command is reported instead of silently enabling");
Console.WriteLine($"All {checks} command checks passed.");

sealed class TestCommandManager : ICommandManager
{
    private readonly Dictionary<string, IReadOnlyCommandInfo> handlers = new(StringComparer.Ordinal);
    public ReadOnlyDictionary<string, IReadOnlyCommandInfo> Commands => new(handlers);
    public bool FailNextAdd { get; set; }
    public bool FailNextRemove { get; set; }

    public bool AddHandler(string command, CommandInfo info)
    {
        if (FailNextAdd) { FailNextAdd = false; return false; }
        return handlers.TryAdd(command, info);
    }
    public bool RemoveHandler(string command)
    {
        if (FailNextRemove) { FailNextRemove = false; return false; }
        return handlers.Remove(command);
    }
    public bool ProcessCommand(string content)
    {
        var parts = content.Split(' ', 2);
        if (!handlers.TryGetValue(parts[0], out var info)) return false;
        DispatchCommand(parts[0], parts.Length == 2 ? parts[1] : "", info);
        return true;
    }
    public void DispatchCommand(string command, string argument, IReadOnlyCommandInfo info)
        => info.Handler(command, argument);
}
