using Crowsnest.DeviceSimulator;

// Phase 1's demo (spec §13): the whole host against a fake sim and a simulated panel.
//
//   dotnet run --project tools/Crowsnest.DeviceSimulator              interactive
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

    case null:
        await Demo.RunInteractiveAsync();
        break;

    default:
        Console.Error.WriteLine($"Unknown option '{args[0]}'. Use --script, --selftest, or nothing for interactive.");
        return 1;
}

return 0;
