using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;

namespace Crowsnest.Core.Tests.Fakes;

/// <summary>An observable with a current value, replayed to each new subscriber.</summary>
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

/// <summary>A sim that records what it is asked to do and reports what the test pushes.</summary>
internal sealed class FakeSimGateway : ISimParameterGateway
{
    private readonly Channel<ParameterSnapshot> _snapshots = Channel.CreateUnbounded<ParameterSnapshot>();

    public StateSubject<SimConnectionState> State { get; } = new(SimConnectionState.Disconnected);

    public IObservable<SimConnectionState> ConnectionState => State;

    public IAsyncEnumerable<ParameterSnapshot> Snapshots => Read();

    public IReadOnlyList<SimSubscription>? Subscribed { get; private set; }

    public ConcurrentQueue<SimCommand> Commands { get; } = new();

    /// <summary>Set to make every write throw it.</summary>
    public Exception? FailWritesWith { get; set; }

    public void Push(ParameterId id, int value) => _snapshots.Writer.TryWrite(new ParameterSnapshot(id, value));

    public Task SubscribeAsync(IReadOnlyList<SimSubscription> subscriptions, CancellationToken ct)
    {
        Subscribed = subscriptions;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ParameterId id, int canonicalValue, CancellationToken ct)
    {
        if (FailWritesWith is { } failure)
        {
            return Task.FromException(failure);
        }

        Commands.Enqueue(new SimCommand.Write(id, canonicalValue));
        return Task.CompletedTask;
    }

    public Task InvokeAsync(string eventName, uint payload, CancellationToken ct)
    {
        Commands.Enqueue(new SimCommand.Invoke(eventName));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async IAsyncEnumerable<ParameterSnapshot> Read([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (ParameterSnapshot snapshot in _snapshots.Reader.ReadAllAsync(ct))
        {
            yield return snapshot;
        }
    }
}

/// <summary>A panel that records the frames it is sent and delivers the inputs the test sends.</summary>
internal sealed class FakePanelDevice : IPanelDevice
{
    private readonly Channel<DeviceInputEvent> _inputs = Channel.CreateUnbounded<DeviceInputEvent>();
    private long _sequence;

    public StateSubject<DeviceConnectionState> State { get; } = new(DeviceConnectionState.Connected);

    public IObservable<DeviceConnectionState> ConnectionState => State;

    public DeviceCapabilities? Capabilities => null;

    public IAsyncEnumerable<DeviceInputEvent> Inputs => Read();

    public ConcurrentQueue<DisplayFrame> Frames { get; } = new();

    public DisplayFrame? Latest => Frames.LastOrDefault();

    public void Turn(int detents, DateTimeOffset at) =>
        _inputs.Writer.TryWrite(new DeviceInputEvent.EncoderTurned(++_sequence, at, detents));

    public void Tap(DateTimeOffset at) =>
        _inputs.Writer.TryWrite(new DeviceInputEvent.ScreenTapped(++_sequence, at, 240, 240));

    /// <summary>The input stream fails, as a dropped serial link would make it.</summary>
    public void Fail(Exception error) => _inputs.Writer.TryComplete(error);

    public Task RenderAsync(DisplayFrame frame, CancellationToken ct)
    {
        Frames.Enqueue(frame);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async IAsyncEnumerable<DeviceInputEvent> Read([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (DeviceInputEvent input in _inputs.Reader.ReadAllAsync(ct))
        {
            yield return input;
        }
    }
}
