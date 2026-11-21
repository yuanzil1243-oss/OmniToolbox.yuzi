using System;
using System.Reflection;

namespace OmniToolbox.NativeDragHotbarLocal;

// The public UiBuilder.Draw callback is suppressed before modules can inspect
// cutscene/UI state. Subscribe only our fallback to the same UI-frame source;
// never change the host UiBuilder's hide flags or other modules' callbacks.
internal sealed class NativeHotbarDrawFallback : IDisposable
{
    private object? source;
    private EventInfo? drawEvent;
    private Delegate? callback;
    private Action<Exception>? report;
    private bool enabled;
    private int generation;
    internal bool Available => enabled && source is not null;

    internal bool TryStart(object builder, Action draw, out string error, Action<Exception>? onError = null)
    {
        Dispose();
        report = onError;
        try
        {
            var type = builder.GetType();
            FieldInfo? field = null;
            while (type is not null && field is null)
            {
                field = type.GetField("interfaceManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }
            var manager = field?.GetValue(builder) ?? throw new NotSupportedException("找不到独立绘制入口");
            var evt = manager.GetType().GetEvent("Draw", BindingFlags.Instance | BindingFlags.Public);
            var invoke = evt?.EventHandlerType?.GetMethod("Invoke");
            if (invoke is null || invoke.ReturnType != typeof(void) || invoke.GetParameters().Length != 0
                || evt!.GetAddMethod() is null || evt.GetRemoveMethod() is null)
                throw new NotSupportedException("独立绘制入口与当前版本不兼容");
            source = manager; drawEvent = evt;
            var ticket = generation;
            Action guarded = () =>
            {
                if (!enabled || ticket != generation) return;
                try { draw(); }
                catch (Exception exception) { enabled = false; Report(exception); }
            };
            callback = Delegate.CreateDelegate(evt.EventHandlerType!, guarded.Target, guarded.Method);
            enabled = true;
            evt.AddEventHandler(manager, callback);
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            Dispose();
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    public void Dispose()
    {
        // A stale delegate is inert even if an incompatible host refuses removal.
        enabled = false;
        generation++;
        try { if (source is not null) drawEvent?.RemoveEventHandler(source, callback); }
        catch (Exception exception) { Report(exception); }
        finally { source = null; drawEvent = null; callback = null; report = null; }
    }

    private void Report(Exception exception)
    {
        try { report?.Invoke(exception.GetBaseException()); }
        catch { /* Never propagate a module error into the shared frame event. */ }
    }
}
