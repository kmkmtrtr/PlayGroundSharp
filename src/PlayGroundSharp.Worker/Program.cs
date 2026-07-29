using PlayGroundSharp.Worker;

if (!WorkerCommandLine.TryParse(args, out var configuration))
{
    Console.Error.WriteLine(
        "Usage: PlayGroundSharp.Worker --pipe <name> [--target-framework <tfm>] [--framework-reference-directory <path>]");
    return 2;
}

return await WorkerEntryPoint.RunAsync(configuration!);
