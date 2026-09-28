using Crowsnest.Sim;
using Microsoft.Extensions.Hosting;

namespace Crowsnest.Host;

/// <summary>Owns the gateway's lifetime (spec §8): connects when the sim appears and reconnects after it quits.</summary>
public sealed class SimConnectHostedService(SimConnectParameterGateway gateway) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => gateway.RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await gateway.DisposeAsync().ConfigureAwait(false);
    }
}
