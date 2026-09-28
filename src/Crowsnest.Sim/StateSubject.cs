namespace Crowsnest.Sim;

/// <summary>An observable with a current value, replayed to each new subscriber. Core has no Rx (spec §4).</summary>
internal sealed class StateSubject<T>(T initial) : IObservable<T>
{
    private readonly Lock _gate = new();
    private readonly List<IObserver<T>> _observers = [];
    private T _value = initial;

    public void Publish(T value)
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

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        T value;
        lock (_gate)
        {
            _observers.Add(observer);
            value = _value;
        }

        observer.OnNext(value);
        return new Unsubscriber(() =>
        {
            lock (_gate)
            {
                _observers.Remove(observer);
            }
        });
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
