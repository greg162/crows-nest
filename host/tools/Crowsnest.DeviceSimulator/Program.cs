using Crowsnest.DeviceSimulator;

// Phase 1's demo (spec §13): the whole host against a fake sim and a simulated device.
//
//   dotnet run --project tools/Crowsnest.DeviceSimulator              interactive, fake sim
//   dotnet run --project tools/Crowsnest.DeviceSimulator -- --msfs    interactive, MSFS over SimConnect
//   dotnet run --project tools/Crowsnest.DeviceSimulator -- --script  a fixed tour, then exit
//   dotnet run --project tools/Crowsnest.DeviceSimulator -- --selftest   the link self-test

Console.OutputEncoding = System.Text.Encoding.UTF8;

switch (args.FirstOrDefault())
{
    case "--selftest":
        await SelfTest.RunAsync();
        break;

    case "--script":
        await Demo.RunScriptAsync();
        break;

    case "--msfs":
        await Demo.RunInteractiveAsync(msfs: true);
        break;

    case null:
        await Demo.RunInteractiveAsync(msfs: false);
        break;

    default:
        Console.Error.WriteLine($"Unknown option '{args[0]}'. Use --msfs, --script, --selftest, or nothing for interactive.");
        return 1;
}

return 0;
