using System.Text;

namespace PlayGroundSharp.Worker;

internal sealed class BoundedEventWriter(Action<string>? sink) : TextWriter
{
    public const int MaximumCharacters = 10 * 1024 * 1024;
    private readonly StringBuilder pending = new();
    private int written;
    private bool truncated;
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (written >= MaximumCharacters)
        {
            if (!truncated)
            {
                sink?.Invoke("\n… output truncated at 10 MiB …\n");
                truncated = true;
            }
            return;
        }
        written++;
        pending.Append(value);
        if (value == '\n' || pending.Length >= 4096)
        {
            Emit();
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (var character in value) Write(character);
    }

    public override void Flush() => Emit();
    private void Emit()
    {
        if (pending.Length == 0) return;
        sink?.Invoke(pending.ToString());
        pending.Clear();
    }
}
