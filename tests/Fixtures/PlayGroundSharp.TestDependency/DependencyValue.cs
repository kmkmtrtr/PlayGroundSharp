namespace PlayGroundSharp.TestDependency;

public static class DependencyValue
{
    public static string Text => "fixture";
}

/// <summary>Provides an instance member so regular completion is non-empty.</summary>
public sealed class FixtureConnection : System.ComponentModel.Component, System.Data.IDbConnection
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public string ConnectionString { get; set; } = string.Empty;
    public int ConnectionTimeout => 0;
    public string Database => "fixture";
    public System.Data.ConnectionState State => System.Data.ConnectionState.Closed;
    public System.Data.IDbTransaction BeginTransaction() => throw new NotSupportedException();
    public System.Data.IDbTransaction BeginTransaction(System.Data.IsolationLevel il) => throw new NotSupportedException();
    public void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public void Close() { }
    public System.Data.IDbCommand CreateCommand() => throw new NotSupportedException();
    public void Open() { }
}
