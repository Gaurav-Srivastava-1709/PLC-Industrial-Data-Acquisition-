using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace WTT.Tests;

/// <summary>
/// Runs a test body on a dedicated STA thread and briefly pumps that thread's message
/// loop afterwards.
/// <para>
/// Two reasons this exists. First, xUnit runs test bodies on an MTA thread, but the code
/// under test is WPF: <c>LiveDataViewModel</c> owns a <see cref="DispatcherTimer"/> and
/// raises property-change notifications, so it must be exercised on the same apartment
/// state the application uses. Second, the view model kicks off its first load with a
/// fire-and-forget call, so the continuation only runs once the dispatcher is pumped -
/// without this the test would assert before the load it is meant to check ever happened.
/// </para>
/// <para>
/// A fresh thread per call keeps each test isolated, and the pump is bounded so a
/// started <see cref="DispatcherTimer"/> cannot make a test hang.
/// </para>
/// </summary>
internal static class Sta
{
    public static void Run(Action body, int settleMs = 350)
    {
        ArgumentNullException.ThrowIfNull(body);

        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
                Pump(settleMs);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "wtt-ui-test-sta",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }

    /// <summary>Drains the dispatcher for a fixed time so queued continuations run.</summary>
    public static void Pump(int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
    }
}
