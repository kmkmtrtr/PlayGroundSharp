using PlayGroundSharp.TestDependency;

namespace PlayGroundSharp.TestFixture;

/// <summary>Provides deterministic greeting values for integration tests.</summary>
public static class Greeter
{
    /// <summary>Gets the greeting supplied by the transitive fixture dependency.</summary>
    public static string Message => $"hello from {DependencyValue.Text}";

    /// <summary>Creates a greeting for the specified person.</summary>
    /// <param name="name">The person to greet.</param>
    /// <returns>A deterministic greeting.</returns>
    public static string Greet(string name) => $"Hello, {name}!";
}
