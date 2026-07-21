using System.Diagnostics;
using System.Text;

namespace PlayGroundSharp.Worker;

internal sealed class BoundedEventWriter(Action<string>? sink) : TextWriter
{
    public const int MaximumCharacters = 10 * 1024 * 1024;
    // Keep large writes from forcing a synchronous WPF layout pass every 64 KiB.
    // Line-oriented output still flushes within MaximumBatchDelay, so interactive
    // progress remains visible while bulk output crosses the pipe in fewer events.
    private const int BatchCharacters = 512 * 1024;
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
        lock (gate) WriteCore(value.AsSpan());
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (gate) WriteCore(buffer);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (gate) WriteCore(buffer.AsSpan(index, count));
    }

    public override void WriteLine()
    {
        lock (gate)
        {
            WriteCore(NewLine.AsSpan());
            if (lastEmission.Elapsed >= MaximumBatchDelay) EmitCore();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (gate)
        {
            if (value is not null) WriteCore(value.AsSpan());
            WriteCore(NewLine.AsSpan());
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

    private void WriteCore(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty && written < MaximumCharacters)
        {
            var count = Math.Min(value.Length, Math.Min(
                MaximumCharacters - written,
                BatchCharacters - pending.Length));
            pending.Append(value[..count]);
            written += count;
            value = value[count..];
            if (pending.Length >= BatchCharacters) EmitCore();
        }

        if (value.IsEmpty || truncated) return;
        EmitCore();
        sink?.Invoke("\n… output truncated at 10 MiB …\n");
        truncated = true;
    }

    private void EmitCore()
    {
        if (pending.Length == 0) return;
        sink?.Invoke(pending.ToString());
        pending.Clear();
        lastEmission.Restart();
    }
}
