using Crowsnest.Core.Application;
using Crowsnest.Core.Application.Panels;
using Crowsnest.Core.Application.Ports;
using Crowsnest.Core.Panels;
using Crowsnest.Sim;
using Crowsnest.SimConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crowsnest.Host;

/// <summary>Composes the whole bridge (spec §8): the shipped panels, the SimConnect gateway, and the devices on USB.</summary>
public static class HostBuilderExtensions
{
    public static IHostApplicationBuilder AddCrowsnest(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        IServiceCollection services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => PanelCatalog.Load());
        services.AddSingleton<IInputActionMap>(DefaultInputActionMap.Instance);

        services.AddSingleton(sp => new SimConnectParameterGateway(
            sp.GetRequiredService<PanelSetup>().Registry,
            () => new ManagedSimConnectClient(),
            sp.GetRequiredService<ILogger<SimConnectParameterGateway>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISimParameterGateway>(sp => sp.GetRequiredService<SimConnectParameterGateway>());

        services.AddHostedService<SimConnectHostedService>();
        services.AddHostedService<BridgeHostedService>();
        return builder;
    }
}
