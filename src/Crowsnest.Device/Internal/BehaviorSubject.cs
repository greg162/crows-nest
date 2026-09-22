namespace Crowsnest.Device.Internal;

/// <summary>
/// The smallest thing that satisfies <see cref="IObservable{T}"/> for a current-value
/// stream. Crowsnest.Core is BCL-only, so the port cannot hand out a Rx type, and one
/// observable per connection does not justify taking System.Reactive as a dependency.
/// </summary>
internal sealed class BehaviorSubject<T>(T initial) : IObservable<T>
{
    private readonly Lock _gate = new();
    private readonly List<IObserver<T>> _observers = [];
    private T _value = initial;

    public T Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    public void OnNext(T value)
    {
        IObserver<T>[] observers;
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(_value, value))
            {
                return;
            }

            _value = value;
            observers = [.. _observers];
        }

        foreach (IObserver<T> observer in observers)
        {
            observer.OnNext(value);
        }
    }

    public void OnCompleted()
    {
        IObserver<T>[] observers;
        lock (_gate)
        {
            observers = [.. _observers];
            _observers.Clear();
        }

        foreach (IObserver<T> observer in observers)
        {
            observer.OnCompleted();
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        T current;
        lock (_gate)
        {
            _observers.Add(observer);
            current = _value;
        }

        observer.OnNext(current);
        return new Subscription(this, observer);
    }

    private sealed class Subscription(BehaviorSubject<T> subject, IObserver<T> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (subject._gate)
            {
                subject._observers.Remove(observer);
            }
        }
    }
}
