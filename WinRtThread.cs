using System.Collections.Concurrent;
using Windows.Foundation;

namespace SpotifyTaskbarWidget;

/// <summary>
/// The single long-lived thread every WinRT (SMTC) call goes through.
///
/// Why this exists: the widget used to die roughly once a day with an
/// AccessViolationException inside WinRT.IObjectReference.Finalize() — a crash
/// that leaves NO trace in errors.log, because an exception on the finalizer
/// thread cannot be caught and kills the process outright. The session objects
/// were created wherever the call happened to land: Task.Run threads, WinRT
/// event callbacks, async continuations. Every one of those is a transient
/// pool thread, so the COM apartment an object belonged to could be gone long
/// before the finalizer released the pointer — and releasing a pointer whose
/// apartment has been torn down faults.
///
/// The thread is deliberately MTA, the same apartment as the finalizer thread,
/// so that releasing those objects needs no cross-apartment marshalling at all.
/// WPF's own UI thread cannot do the job: it has to be STA, and SMTC calls are
/// allowed to block (they do hang on dead sessions), which would freeze the
/// window.
/// </summary>
internal static class WinRtThread
{
    private static readonly BlockingCollection<Action> Jobs = new();

    static WinRtThread()
    {
        var thread = new Thread(() =>
        {
            foreach (var job in Jobs.GetConsumingEnumerable())
            {
                // A job reports failure through its own task; nothing may be
                // allowed to take this thread down
                try { job(); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "WinRT",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Runs the work on the WinRT thread and awaits its result.
    /// Jobs are queued, so calls are serialized - which is also what keeps two
    /// bursts of session events from swapping subscriptions at once.</summary>
    public static Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Jobs.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public static Task Run(Action work) => Run<object?>(() => { work(); return null; });

    /// <summary>Fire and forget, for the session-event callbacks: they arrive
    /// on RPC threads and must not touch WinRT themselves.</summary>
    public static void Post(Action work) => Jobs.Add(work);

    /// <summary>Waits out a WinRT async operation ON this thread, so the result
    /// object is created in this apartment too. The timeout is what keeps a
    /// hung session from blocking the queue for good; the operation itself is
    /// simply abandoned, and its result is collected as usual.</summary>
    public static T? Await<T>(IAsyncOperation<T> operation, int timeoutMs = 3000) where T : class
    {
        var task = operation.AsTask();
        return task.Wait(timeoutMs) ? task.Result : null;
    }

    /// <summary>Same for operations with no result worth reading.</summary>
    public static void AwaitAny(Task task, int timeoutMs = 3000) => task.Wait(timeoutMs);
}
