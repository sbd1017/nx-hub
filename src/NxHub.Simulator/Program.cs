using System.Globalization;
using System.Net;
using NxHub.Simulator;

var port = 6000;
var flavour = SimulatorFlavour.SysBotBase;
var address = IPAddress.Loopback;
var delayMs = 0;
var interactive = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--flavour" or "--flavor" when i + 1 < args.Length:
            flavour = args[++i].ToLowerInvariant() switch
            {
                "cpp" or "sys-botbase-cpp" => SimulatorFlavour.SysBotBaseCpp,
                _ => SimulatorFlavour.SysBotBase,
            };
            break;
        case "--interactive-screen":
            interactive = true;
            break;
        case "--listen-any":
            address = IPAddress.Any;
            break;
        case "--delay-ms" when i + 1 < args.Length:
            delayMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "-h" or "--help":
            Console.WriteLine("""
                nxhub-simulator: a sys-botbase protocol simulator (NOT a Switch).

                  --port <n>          TCP port (default 6000)
                  --flavour <name>    sysbotbase (default) or cpp
                  --delay-ms <n>      artificial response delay
                  --interactive-screen  pretend HOME menu that reacts to the D-pad, A and B
                  --listen-any        listen on all interfaces instead of loopback
                """);
            return;
    }
}

await using var simulator = new SysBotSimulator(new SimulatorOptions
{
    Port = port,
    Flavour = flavour,
    Address = address,
    ResponseDelay = TimeSpan.FromMilliseconds(delayMs),
    InteractiveScreen = interactive,
});
simulator.Start();
Console.WriteLine($"nxhub-simulator ({flavour}, version {simulator.Version}) listening on {address}:{simulator.Port}. Ctrl+C to stop.");

using var exit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exit.Cancel();
};

var lastCount = 0;
while (!exit.IsCancellationRequested)
{
    try
    {
        await Task.Delay(500, exit.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    var received = simulator.ReceivedCommands;
    for (; lastCount < received.Count; lastCount++)
    {
        Console.WriteLine($"  ← {received[lastCount]}");
    }
}
