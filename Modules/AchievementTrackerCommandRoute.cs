using System;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace OmniToolbox.AchievementTrackerOnline;

internal sealed class AchievementTrackerCommandRoute : IDisposable
{
    internal const string Command = "/omni";
    internal const string Alias = "成就追踪悬浮窗";

    private readonly ICommandManager commands;
    private readonly CommandInfo original;
    private readonly CommandInfo routed;
    private bool active = true;
    private bool disposed;

    private AchievementTrackerCommandRoute(ICommandManager commands, CommandInfo original, Action toggleOverlay)
    {
        this.commands = commands;
        this.original = original;
        routed = new CommandInfo((command, arguments) =>
        {
            if (active && IsOverlayCommand(arguments))
            {
                toggleOverlay();
                return;
            }

            original.Handler(command, arguments);
        })
        {
            HelpMessage = original.HelpMessage,
            ShowInHelp = original.ShowInHelp,
            DisplayOrder = original.DisplayOrder,
            AllowedInMacros = original.AllowedInMacros,
        };
    }

    internal static AchievementTrackerCommandRoute Attach(ICommandManager commands, Action toggleOverlay)
    {
        if (!commands.Commands.TryGetValue(Command, out var current) || current is not CommandInfo original)
            throw new InvalidOperationException("成就追踪：找不到可接入的 /omni 命令处理器");

        var route = new AchievementTrackerCommandRoute(commands, original, toggleOverlay);
        if (!commands.RemoveHandler(Command))
            throw new InvalidOperationException("成就追踪：无法接入 /omni 命令处理器");

        try
        {
            if (!commands.AddHandler(Command, route.routed))
                throw new InvalidOperationException("成就追踪：注册 /omni 快捷命令处理器失败");
            return route;
        }
        catch
        {
            route.active = false;
            if (!commands.Commands.ContainsKey(Command) && !commands.AddHandler(Command, original))
                throw new InvalidOperationException("成就追踪：注册失败后无法恢复原 /omni 命令处理器");
            throw;
        }
    }

    private static bool IsOverlayCommand(string arguments)
        => string.Equals(arguments.Trim(), Alias, StringComparison.Ordinal);

    public void Dispose()
    {
        active = false;
        if (disposed) return;

        if (commands.Commands.TryGetValue(Command, out var current) && ReferenceEquals(current, routed))
        {
            if (!commands.RemoveHandler(Command))
                throw new InvalidOperationException("成就追踪：无法移除 /omni 快捷命令处理器");
            if (!commands.AddHandler(Command, original))
            {
                // Keep an inactive forwarding handler if restoring the original is rejected.
                if (!commands.Commands.ContainsKey(Command))
                    commands.AddHandler(Command, routed);
                throw new InvalidOperationException("成就追踪：无法恢复原 /omni 命令处理器");
            }
        }

        disposed = true;
    }
}
