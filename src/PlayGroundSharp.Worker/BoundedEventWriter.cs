using System.Diagnostics;
using System.Text;

namespace PlayGroundSharp.Worker;

internal sealed class BoundedEventWriter(Action<string>? sink) : TextWriter
{
    public const int MaximumCharacters = 10 * 1024 * 1024;
    private const int BatchCharacters = 64 * 1024;
    private static readonly TimeSpan MaximumBatchDelay = TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private readonly StringBuilder pending = new();
    private readonly Stopwatch lastEmission = Stopwatch.StartNew();
    private int written;
    private bool truncated;
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (gate) WriteCore(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (gate)
            foreach (var character in value)
                WriteCore(character);
    }

    public override void WriteLine()
    {
        lock (gate)
        {
            foreach (var character in NewLine) WriteCore(character);
            if (lastEmission.Elapsed >= MaximumBatchDelay) EmitCore();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (gate)
        {
            if (value is not null)
                foreach (var character in value)
                    WriteCore(character);
            foreach (var character in NewLine) WriteCore(character);
            if (lastEmission.Elapsed >= MaximumBatchDelay) EmitCore();
        }
    }

    public override void Flush()
    {
        lock (gate) EmitCore();
    }

    private void WriteCore(char value)
    {
        if (written >= MaximumCharacters)
        {
            if (!truncated)
            {
                EmitCore();
                sink?.Invoke("\n… output truncated at 10 MiB …\n");
                truncated = true;
            }
            return;
        }
        written++;
        pending.Append(value);
        if (pending.Length >= BatchCharacters) EmitCore();
    }

    private void EmitCore()
    {
        if (pending.Length == 0) return;
        sink?.Invoke(pending.ToString());
        pending.Clear();
        lastEmission.Restart();
    }
}
