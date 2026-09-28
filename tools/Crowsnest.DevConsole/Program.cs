using Crowsnest.Host;
using Microsoft.Extensions.Hosting;

// The whole bridge, headless: MSFS over SimConnect on one side, the panel on USB on the other.
// Either can come and go; the host reconnects to both. Ctrl+C to stop.
//
//   dotnet run --project tools/Crowsnest.DevConsole
//   dotnet run --project tools/Crowsnest.DevConsole -- --Logging:LogLevel:Default=Debug

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddCrowsnest();

await builder.Build().RunAsync();
