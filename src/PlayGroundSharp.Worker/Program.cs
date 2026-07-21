using PlayGroundSharp.Worker;

if (args.Length != 2 || args[0] != "--pipe" || string.IsNullOrWhiteSpace(args[1]))
{
    Console.Error.WriteLine("Usage: PlayGroundSharp.Worker --pipe <name>");
    return 2;
}

return await WorkerEntryPoint.RunAsync(args[1]);
