// ReSharper disable ConstantConditionalAccessQualifier
// ReSharper disable ConditionIsAlwaysTrueOrFalse
// ReSharper disable GrammarMistakeInComment

using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace ReactiveWinUI;

/// <summary>
/// Represents an object that schedules units of work on a <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/>.
/// </summary>
public class DispatcherQueueScheduler : LocalScheduler, ISchedulerPeriodic
{
    /// <summary>
    /// Gets the scheduler that schedules work on the <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/> for the current thread.
    /// </summary>
    public static DispatcherQueueScheduler Current
    {
        get
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            return dispatcher == null
                ? throw new InvalidOperationException("There is no current dispatcher thread")
                : new DispatcherQueueScheduler(dispatcher);
        }
    }

    /// <summary>
    /// Constructs a <see cref="DispatcherQueueScheduler"/> that schedules units of work on the given <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/>.
    /// </summary>
    /// <param name="dispatcherQueue"><see cref="Microsoft.UI.Dispatching.DispatcherQueue"/> to schedule work on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcherQueue"/> is <see langword="null"/>.</exception>
    public DispatcherQueueScheduler(DispatcherQueue dispatcherQueue)
    {
        DispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        Priority = DispatcherQueuePriority.Normal;
    }

    /// <summary>
    /// Constructs a DispatcherScheduler that schedules units of work on the given <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/> at the given priority.
    /// </summary>
    /// <param name="dispatcherQueue"><see cref="Microsoft.UI.Dispatching.DispatcherQueue"/> to schedule work on.</param>
    /// <param name="priority">Priority at which units of work are scheduled.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcherQueue"/> is <c><see langword="null"/></c>.</exception>
    public DispatcherQueueScheduler(DispatcherQueue dispatcherQueue, DispatcherQueuePriority priority)
    {
        DispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        Priority = priority;
    }

    /// <summary>
    /// Gets the <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/> associated with the <see cref="DispatcherQueueScheduler"/>.
    /// </summary>
    public DispatcherQueue DispatcherQueue { get; }

    /// <summary>
    /// Gets the priority at which work items will be dispatched.
    /// </summary>
    public DispatcherQueuePriority Priority { get; }

    /// <summary>
    /// Schedules an action to be executed on the dispatcher.
    /// </summary>
    /// <typeparam name="TState">The type of the state passed to the scheduled action.</typeparam>
    /// <param name="state">State passed to the action to be executed.</param>
    /// <param name="action">Action to be executed.</param>
    /// <returns>The disposable object used to cancel the scheduled action (best effort).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <c><see langword="null"/></c>.</exception>
    public override IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var d = new SingleAssignmentDisposable();
        
        if(DispatcherQueue.HasThreadAccess)
        {
            d.Disposable = action(this, state);
            return d;
        }

        DispatcherQueue.TryEnqueue(Priority,
            () =>
            {
                if (!d.IsDisposed)
                {
                    d.Disposable = action(this, state);
                }
            });

        return d;
    }

    /// <summary>
    /// Schedules an action to be executed after <paramref name="dueTime"/> on the dispatcherQueue, using a <see cref="DispatcherQueueTimer"/> object.
    /// </summary>
    /// <typeparam name="TState">The type of the state passed to the scheduled action.</typeparam>
    /// <param name="state">State passed to the action to be executed.</param>
    /// <param name="action">Action to be executed.</param>
    /// <param name="dueTime">Relative time after which to execute the action.</param>
    /// <returns>The disposable object used to cancel the scheduled action (best effort).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <c><see langword="null"/></c>.</exception>
    public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dt = Scheduler.Normalize(dueTime);

        return dt.Ticks == 0 ? Schedule(state, action) : ScheduleSlow(state, dt, action);
    }

#pragma warning disable CA1859
    private IDisposable ScheduleSlow<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
#pragma warning restore CA1859
    {
        var d = new MultipleAssignmentDisposable();

        var timer = DispatcherQueue.CreateTimer();

        timer.Tick += (s, e) =>
        {
            var t = Interlocked.Exchange(ref timer, value: null);

            try
            {
                d.Disposable = action(this, state);
            }
            finally
            {
                if (t != null)
                {
                    t.Stop();
                    action = static (s, t) => Disposable.Empty;
                }
            }
        };

        timer.Interval = dueTime;
        timer.Start();

        d.Disposable = Disposable.Create(() =>
        {
            var t = Interlocked.Exchange(ref timer, value: null);
            if (t != null)
            {
                t.Stop();
                action = static (s, t) => Disposable.Empty;
            }
        });

        return d;
    }

    /// <summary>
    /// Schedules a periodic piece of work on the dispatcherQueue, using a <see cref="DispatcherQueueTimer"/> object.
    /// </summary>
    /// <typeparam name="TState">The type of the state passed to the scheduled action.</typeparam>
    /// <param name="state">Initial state passed to the action upon the first iteration.</param>
    /// <param name="period">Period for running the work periodically.</param>
    /// <param name="action">Action to be executed, potentially updating the state.</param>
    /// <returns>The disposable object used to cancel the scheduled recurring action (best effort).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> is less than <see cref="TimeSpan.Zero"/>.</exception>
    public IDisposable SchedulePeriodic<TState>(TState state, TimeSpan period, Func<TState, TState> action)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, TimeSpan.Zero);

        ArgumentNullException.ThrowIfNull(action);

        var timer = DispatcherQueue.CreateTimer();

        var state1 = state;

        timer.Tick += (s, e) =>
        {
            state1 = action(state1);
        };

        timer.Interval = period;
        timer.Start();

        return Disposable.Create(() =>
        {
            var t = Interlocked.Exchange(ref timer, value: null);

            t?.Stop();
            action = static x => x;
        });
    }
}
