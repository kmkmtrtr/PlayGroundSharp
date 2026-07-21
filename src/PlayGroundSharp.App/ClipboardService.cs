using System.Runtime.InteropServices;
using System.Windows;

namespace PlayGroundSharp.App;

/// <summary>Writes text while tolerating brief clipboard ownership by another desktop process.</summary>
internal static class ClipboardService
{
    private const int MaximumAttempts = 8;

    public static async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException) when (attempt < MaximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 50), cancellationToken);
            }
        }
    }
}
