using PlayGroundSharp.Core;

namespace PlayGroundSharp.Core.Tests;

public sealed class WorkspaceFileTests
{
    [Fact]
    public async Task RoundTripsWorkspaceDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.pgsworkspace");
        var expected = new WorkspaceDocument(
            WorkspaceDocument.CurrentVersion,
            DateTime.UtcNow,
            ["var answer = 42", "answer"],
            [.. SessionContext.DefaultImports, "Humanizer"],
            [@"C:\Libraries\Example.dll"],
            [new("Humanizer.Core", "3.0.10")],
            "answer + 1");
        try
        {
            await WorkspaceFile.SaveAsync(path, expected);

            var actual = await WorkspaceFile.LoadAsync(path);

            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.SavedAtUtc, actual.SavedAtUtc);
            Assert.Equal(expected.Submissions, actual.Submissions);
            Assert.Equal(expected.Imports, actual.Imports);
            Assert.Equal(expected.References, actual.References);
            Assert.Equal(expected.Packages, actual.Packages);
            Assert.Equal(expected.InputText, actual.InputText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsUnsupportedVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.pgsworkspace");
        await File.WriteAllTextAsync(path, "{\"version\":99,\"savedAtUtc\":\"2026-01-01T00:00:00Z\",\"submissions\":[],\"imports\":[],\"references\":[],\"packages\":[],\"inputText\":\"\"}");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceFile.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
