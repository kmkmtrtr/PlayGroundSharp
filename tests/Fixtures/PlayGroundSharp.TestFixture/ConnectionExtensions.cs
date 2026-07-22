using System.Data;

namespace PlayGroundSharp.TestFixture;

/// <summary>Models query extensions shipped separately from their connection implementation.</summary>
public static class ConnectionExtensions
{
    /// <summary>Returns rows for the supplied command.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="connection">The connection to query.</param>
    /// <param name="command">The command text.</param>
    public static IEnumerable<T> Query<T>(this IDbConnection connection, string command) => [];
}
