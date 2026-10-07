using NetworkConditioner;

if (args.Contains("--help"))
{
    Console.WriteLine(ConditionerOptions.UsageText);
    return 0;
}

ConditionerOptions options;
try
{
    options = ConditionerOptions.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(ConditionerOptions.UsageText);
    return 1;
}

Console.WriteLine($"Listening on port {options.ListenPort}, forwarding to {options.TargetHost}:{options.TargetPort}");
Console.WriteLine($"Upstream (client to server): {options.Upstream}");
Console.WriteLine($"Downstream (server to client): {options.Downstream}");
Console.WriteLine($"Seed: {options.Seed}");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var relay = new UdpRelay(options);
await relay.RunAsync(cancellation.Token);
return 0;
