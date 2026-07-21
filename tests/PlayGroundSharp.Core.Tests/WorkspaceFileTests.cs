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
            "answer + 1 // 日本語 <checked>");
        try
        {
            await WorkspaceFile.SaveAsync(path, expected);

            var actual = await WorkspaceFile.LoadAsync(path);
            var serialized = await File.ReadAllTextAsync(path);

            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.SavedAtUtc, actual.SavedAtUtc);
            Assert.Equal(expected.Submissions, actual.Submissions);
            Assert.Equal(expected.Imports, actual.Imports);
            Assert.Equal(expected.References, actual.References);
            Assert.Equal(expected.Packages, actual.Packages);
            Assert.Equal(expected.InputText, actual.InputText);
            Assert.Contains("answer + 1 // 日本語 <checked>", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u002B", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\u65E5", serialized, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task RejectsMissingCollectionsAsInvalidWorkspaceData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.pgsworkspace");
        await File.WriteAllTextAsync(path,
            "{\"version\":1,\"savedAtUtc\":\"2026-01-01T00:00:00Z\",\"submissions\":null,\"imports\":[],\"references\":[],\"packages\":[],\"inputText\":\"\"}");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceFile.LoadAsync(path));
            Assert.Contains("missing fields", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DoesNotCreateAnUnreadableOversizedWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.pgsworkspace");
        var document = new WorkspaceDocument(
            WorkspaceDocument.CurrentVersion,
            DateTime.UtcNow,
            [new string('x', checked((int)WorkspaceFile.MaximumFileBytes))],
            [],
            [],
            [],
            string.Empty);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceFile.SaveAsync(path, document));
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*.tmp"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsWorkspaceThatWouldRestoreTooManyPackages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.pgsworkspace");
        var document = new WorkspaceDocument(
            WorkspaceDocument.CurrentVersion,
            DateTime.UtcNow,
            [],
            [],
            [],
            Enumerable.Range(0, 101).Select(index => new WorkspacePackage($"Package.{index}", "1.0.0")).ToArray(),
            string.Empty);

        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceFile.SaveAsync(path, document));
        Assert.False(File.Exists(path));
    }
}
