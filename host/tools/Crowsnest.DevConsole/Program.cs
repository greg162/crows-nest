using Crowsnest.Host;
using Microsoft.Extensions.Hosting;

// The whole bridge, headless: MSFS over SimConnect on one side, the devices on USB on the other.
// Either can come and go; the host reconnects to both. Ctrl+C to stop.
//
//   dotnet run --project tools/Crowsnest.DevConsole
//   dotnet run --project tools/Crowsnest.DevConsole -- --Logging:LogLevel:Default=Debug

using SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire();
if (guard is null)
{
    Console.Error.WriteLine("Crowsnest is already running (the tray app, or another DevConsole). Exit it first.");
    return 1;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddCrowsnest();

await builder.Build().RunAsync();
return 0;
