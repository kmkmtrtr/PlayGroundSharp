using System.Diagnostics;

namespace PlayGroundSharp.Worker;

/// <summary>Runs the Worker without allowing a host-level failure to trigger desktop crash UI.</summary>
public static class WorkerEntryPoint
{
    public static async Task<int> RunAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        var configuration = new WorkerConfiguration(
            pipeName,
            $"net{Environment.Version.Major}.0",
            null);
        return await RunAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(
        WorkerConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await new WorkerHost(configuration).RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            Trace.TraceError($"Worker host failed: {error}");
            Console.Error.WriteLine($"Worker host failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }
}
