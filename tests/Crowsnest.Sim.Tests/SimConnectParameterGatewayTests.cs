using System.Collections.Concurrent;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Domain;
using Crowsnest.Core.Panels;
using Crowsnest.SimConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Crowsnest.Sim.Tests;

/// <summary>The gateway against a scripted SimConnect session and a fake clock.</summary>
public sealed class SimConnectParameterGatewayTests : IAsyncDisposable
{
    private static readonly ParameterId ComStandby = new("com1.standby");
    private static readonly ParameterId ComActive = new("com1.active");
    private static readonly ParameterId Spacing = new("com1.spacing");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly ConcurrentQueue<FakeSimConnectClient> _clients = new();
    private readonly Queue<FakeSimConnectClient> _next = new();
    private readonly ConcurrentQueue<ParameterSnapshot> _snapshots = new();
    private readonly ConcurrentQueue<SimConnectionState> _states = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SimConnectParameterGateway _gateway;
    private Task? _run;
    private Task? _reader;

    public SimConnectParameterGatewayTests()
    {
        _gateway = new SimConnectParameterGateway(
            DefaultParameters.Load().Registry,
            () =>
            {
                FakeSimConnectClient client = _next.Count > 0 ? _next.Dequeue() : new FakeSimConnectClient();
                _clients.Enqueue(client);
                return client;
            },
            NullLogger<SimConnectParameterGateway>.Instance,
            _time);
    }

    private FakeSimConnectClient Client => _clients.Last();

    private async Task StartAsync()
    {
        _gateway.ConnectionState.Subscribe(new Recorder(_states));
        await _gateway.SubscribeAsync(DefaultParameters.Load().Registry.Subscriptions, CancellationToken.None);
        _run = _gateway.RunAsync(_stop.Token);
        _reader = Task.Run(async () =>
        {
            await foreach (ParameterSnapshot snapshot in _gateway.Snapshots.WithCancellation(_stop.Token))
            {
                _snapshots.Enqueue(snapshot);
            }
        });

        await Eventually(() => _clients.LastOrDefault()?.Watches.Count >= 6, "the session to watch its values");
    }

    /// <summary>Connected, then the cockpit camera and five quiet seconds.</summary>
    private async Task StartReadyAsync()
    {
        await StartAsync();
        Client.Push("COM ACTIVE FREQUENCY:1", 127_850_000);
        Client.Push("COM STANDBY FREQUENCY:1", 124_850_000);
        Client.Push("CAMERA STATE", 2);
        await Advance(TimeSpan.FromSeconds(5.2));
        await Eventually(() => _states.LastOrDefault() == SimConnectionState.Connected, "readiness");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        if (_reader is not null)
        {
            await Task.WhenAny(_reader, Task.Delay(1000));
        }

        await _gateway.DisposeAsync();
        _stop.Dispose();
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    private async Task Advance(TimeSpan by)
    {
        for (TimeSpan moved = TimeSpan.Zero; moved < by; moved += TimeSpan.FromMilliseconds(100))
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task ItWatchesTheCameraAndEverySubscriptionEveryFrame()
    {
        await StartAsync();

        Assert.Equal(
            ["CAMERA STATE", "COM STANDBY FREQUENCY:1", "COM ACTIVE FREQUENCY:1", "COM STANDBY FREQUENCY:2", "COM ACTIVE FREQUENCY:2",
             "NAV STANDBY FREQUENCY:1", "NAV ACTIVE FREQUENCY:1", "COM SPACING MODE:1", "COM SPACING MODE:2"],
            Client.Watches.Select(w => w.SimVar));
        Assert.All(Client.Watches, w => Assert.Equal(SimConnectPeriod.VisualFrame, w.Period));
        Assert.Equal("Hz", Client.Watches.Single(w => w.SimVar == "COM ACTIVE FREQUENCY:1").Unit);
        Assert.Equal(SimConnectionState.Connecting, _states.Last());
    }

    [Fact]
    public async Task NothingIsPassedOnUntilTheFlightIsReady()
    {
        await StartAsync();

        Client.Push("COM ACTIVE FREQUENCY:1", 118_505_000); // the sim's value at the Start Flight screen
        Client.Push("CAMERA STATE", 16);
        await Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(_snapshots);
        Assert.Equal(SimConnectionState.Connecting, _states.Last());
    }

    [Fact]
    public async Task WhenReadyTheLatestValuesArePassedOnAndZeroHzIsNoValue()
    {
        await StartAsync();
        Client.Push("COM ACTIVE FREQUENCY:1", 118_505_000);
        Client.Push("COM ACTIVE FREQUENCY:1", 127_850_000); // the aircraft wins the fight
        Client.Push("COM STANDBY FREQUENCY:1", 0);
        Client.Push("COM SPACING MODE:1", 0);
        Client.Push("CAMERA STATE", 2);

        await Advance(TimeSpan.FromSeconds(4.8));
        Assert.Empty(_snapshots);

        await Advance(TimeSpan.FromSeconds(0.4));
        await Eventually(() => _snapshots.Count >= 3, "the held values");

        Assert.Equal(SimConnectionState.Connected, _states.Last());
        Assert.Contains(new ParameterSnapshot(ComActive, 127_850), _snapshots);
        Assert.Contains(new ParameterSnapshot(ComStandby, 0, Available: false), _snapshots);
        Assert.Contains(new ParameterSnapshot(Spacing, 0), _snapshots);
        Assert.DoesNotContain(_snapshots, s => s.CanonicalValue == 118_505);
    }

    [Fact]
    public async Task ALateChangeFromTheAircraftPushesReadinessBack()
    {
        await StartAsync();
        Client.Push("CAMERA STATE", 2);
        await Advance(TimeSpan.FromSeconds(1.4));
        Client.Push("COM ACTIVE FREQUENCY:1", 118_705_000); // the TriStar, 1.4 s after camera 2
        await Task.Delay(50);

        await Advance(TimeSpan.FromSeconds(4.5));
        Assert.NotEqual(SimConnectionState.Connected, _states.Last());

        await Advance(TimeSpan.FromSeconds(0.7));
        await Eventually(() => _states.Last() == SimConnectionState.Connected, "readiness after the late write");
    }

    [Fact]
    public async Task AfterReadinessChangesArePassedOnAsTheyHappen()
    {
        await StartReadyAsync();

        Client.Push("COM STANDBY FREQUENCY:1", 121_500_000);

        await Eventually(() => _snapshots.Contains(new ParameterSnapshot(ComStandby, 121_500)), "the change");
    }

    [Fact]
    public async Task SubscribingAgainResendsTheLatestValues()
    {
        await StartReadyAsync();
        await Eventually(() => _snapshots.Contains(new ParameterSnapshot(ComStandby, 124_850)), "the first values");
        _snapshots.Clear();

        // A panel plugged back in: a new coordinator subscribes to what is already watched.
        await _gateway.SubscribeAsync(DefaultParameters.Load().Registry.Subscriptions, CancellationToken.None);
        await Advance(TimeSpan.FromMilliseconds(200));

        await Eventually(() => _snapshots.Contains(new ParameterSnapshot(ComStandby, 124_850)), "the values again");
        Assert.Contains(new ParameterSnapshot(ComActive, 127_850), _snapshots);
        Assert.Equal(1 + DefaultParameters.Load().Registry.Subscriptions.Count, Client.Watches.Count); // the camera, then no duplicates
    }

    [Fact]
    public async Task AnUnchangedValueIsNotPassedOnTwice()
    {
        await StartReadyAsync();
        int before = _snapshots.Count;

        Client.Push("COM STANDBY FREQUENCY:1", 124_850_000);
        Client.Push("COM STANDBY FREQUENCY:1", 121_500_000);

        await Eventually(() => _snapshots.Contains(new ParameterSnapshot(ComStandby, 121_500)), "the real change");
        Assert.Equal(before + 1, _snapshots.Count);
    }

    [Fact]
    public async Task WritesBeforeReadinessAreDropped()
    {
        await StartAsync();

        await _gateway.WriteAsync(ComStandby, 122_800, CancellationToken.None);
        await _gateway.InvokeAsync("COM_STBY_RADIO_SWAP", 0, CancellationToken.None);

        Assert.Empty(Client.Transmits);
    }

    [Fact]
    public async Task WritesAfterReadinessGoOutAsTheirKeyEvent()
    {
        await StartReadyAsync();

        await _gateway.WriteAsync(ComStandby, 122_800, CancellationToken.None);
        await _gateway.WriteAsync(ComActive, 118_005, CancellationToken.None);
        await _gateway.InvokeAsync("COM_STBY_RADIO_SWAP", 0, CancellationToken.None);

        Assert.Equal(
            [("COM_STBY_RADIO_SET_HZ", 122_800_000u), ("COM_RADIO_SET_HZ", 118_005_000u), ("COM_STBY_RADIO_SWAP", 0u)],
            Client.Transmits);
    }

    [Fact]
    public async Task GoingBackToTheMenuHoldsValuesAndDropsWritesAgain()
    {
        await StartReadyAsync();

        Client.Push("CAMERA STATE", 32);
        await Eventually(() => _states.Last() == SimConnectionState.Connecting, "readiness to be lost");
        await _gateway.WriteAsync(ComStandby, 122_800, CancellationToken.None);

        Assert.Empty(Client.Transmits);
    }

    [Fact]
    public async Task NoSimMeansRetryingWithBackoff()
    {
        _next.Enqueue(new FakeSimConnectClient { Unavailable = true });
        _next.Enqueue(new FakeSimConnectClient { Unavailable = true });
        _gateway.ConnectionState.Subscribe(new Recorder(_states));
        _run = _gateway.RunAsync(_stop.Token);

        await Eventually(() => _clients.Count == 1, "the first attempt");
        await Advance(TimeSpan.FromSeconds(1.3)); // 1 s ± 20%
        await Eventually(() => _clients.Count == 2, "the second attempt");
        await Advance(TimeSpan.FromSeconds(2.5)); // 2 s ± 20%
        await Eventually(() => _clients.Count == 3 && _clients.Last().Opened, "the third attempt to connect");

        Assert.All(_clients.Take(2), c => Assert.True(c.Disposed));
        Assert.DoesNotContain(SimConnectionState.Connected, _states);
    }

    [Fact]
    public async Task WhenTheSimQuitsItReconnects()
    {
        await StartReadyAsync();
        FakeSimConnectClient first = Client;

        first.Push(new SimConnectMessage.Quit());
        await Eventually(() => _states.Last() == SimConnectionState.Disconnected || _clients.Count == 2, "the session to end");
        await Eventually(() => _clients.Count == 2, "a new session");

        Assert.True(first.Disposed);
        await _gateway.WriteAsync(ComStandby, 122_800, CancellationToken.None);
        Assert.Empty(Client.Transmits); // a new session starts unready
    }

    [Fact]
    public async Task AWriteModeOtherThanKeyEventsIsNotSupportedYet()
    {
        // Every shipped parameter writes by key event, so check through a registry of our own.
        ReadBinding read = new(ReadSource.SimVar, "AUTOPILOT ALTITUDE LOCK VAR", "feet", 1);
        Core.Domain.ParameterDefinition altitude = new(
            new ParameterId("ap.altitude"), "ALT", "ap", CanonicalUnit.Feet,
            new Core.Domain.Grids.LinearGrid(0, 50_000, 100), [new CursorLevel("fine", 100, CursorWrap.Clamp, ..)],
            read, new WriteBinding(WriteMode.SimVarWrite, "AUTOPILOT ALTITUDE LOCK VAR", PayloadEncoding.Raw),
            new Core.Domain.Formatting.GroupedFormatter());
        SimConnectParameterGateway gateway = new(
            new Core.Application.ParameterRegistry([altitude]), () => new FakeSimConnectClient(), NullLogger<SimConnectParameterGateway>.Instance, _time);

        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.WriteAsync(altitude.Id, 12_000, CancellationToken.None));
    }

    [Fact]
    public async Task CancellingStopsTheRunCleanly()
    {
        await StartReadyAsync();

        await _stop.CancelAsync();
        await _run!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_run.IsCompletedSuccessfully);
        Assert.Equal(SimConnectionState.Disconnected, _states.Last());
    }

    private sealed class Recorder(ConcurrentQueue<SimConnectionState> states) : IObserver<SimConnectionState>
    {
        public void OnNext(SimConnectionState value) => states.Enqueue(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
