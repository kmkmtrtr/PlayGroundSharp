namespace PlayGroundSharp.TestFixture;

/// <summary>Provides an extension method used to verify instance and type completion behavior.</summary>
public static class NumberExtensions
{
    /// <summary>Converts the supplied number of billions to its numeric value.</summary>
    /// <param name="value">The number of billions.</param>
    /// <returns>The numeric value.</returns>
    public static long Billions(this int value) => value * 1_000_000_000L;
}
