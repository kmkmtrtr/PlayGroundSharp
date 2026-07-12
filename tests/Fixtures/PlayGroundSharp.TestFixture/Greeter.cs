using PlayGroundSharp.TestDependency;

namespace PlayGroundSharp.TestFixture;

public static class Greeter
{
    public static string Message => $"hello from {DependencyValue.Text}";
}
