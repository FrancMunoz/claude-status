namespace ClaudeStatus.Usage;

/// <summary>
/// A minimal multicast <see cref="IObservable{T}"/> for snapshots.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a dependency on System.Reactive: this is the
/// only place the app needs an observable, and the whole surface used is
/// "subscribe, publish, unsubscribe". Adding a reactive framework for that would
/// not survive the justification in <c>docs/manual.md</c> §8.
/// </remarks>
internal sealed class SnapshotSubject : IObservable<UsageSnapshot>
{
    private readonly Lock _gate = new();
    private readonly List<IObserver<UsageSnapshot>> _observers = [];
    private bool _completed;

    /// <inheritdoc />
    public IDisposable Subscribe(IObserver<UsageSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            if (_completed)
            {
                observer.OnCompleted();
                return NullSubscription.Instance;
            }

            _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    /// <summary>Pushes a snapshot to every current subscriber.</summary>
    /// <remarks>
    /// Observers are copied before the callbacks run, so a subscriber that
    /// unsubscribes from inside its own handler cannot mutate the list we are
    /// iterating. A throwing observer is isolated: it does not stop the others
    /// and does not bubble into the polling loop.
    /// </remarks>
    public void Publish(UsageSnapshot snapshot)
    {
        IObserver<UsageSnapshot>[] targets;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            targets = [.. _observers];
        }

        foreach (IObserver<UsageSnapshot> observer in targets)
        {
            try
            {
                observer.OnNext(snapshot);
            }
            catch (Exception)
            {
                // A misbehaving subscriber must not take down polling.
            }
        }
    }

    /// <summary>Signals completion and drops every subscriber.</summary>
    public void Complete()
    {
        IObserver<UsageSnapshot>[] targets;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            targets = [.. _observers];
            _observers.Clear();
        }

        foreach (IObserver<UsageSnapshot> observer in targets)
        {
            try
            {
                observer.OnCompleted();
            }
            catch (Exception)
            {
                // As above.
            }
        }
    }

    private void Unsubscribe(IObserver<UsageSnapshot> observer)
    {
        lock (_gate)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class Subscription(SnapshotSubject subject, IObserver<UsageSnapshot> observer) : IDisposable
    {
        private IObserver<UsageSnapshot>? _observer = observer;

        public void Dispose()
        {
            IObserver<UsageSnapshot>? target = Interlocked.Exchange(ref _observer, null);
            if (target is not null)
            {
                subject.Unsubscribe(target);
            }
        }
    }

    private sealed class NullSubscription : IDisposable
    {
        public static readonly NullSubscription Instance = new();

        public void Dispose()
        {
        }
    }
}
