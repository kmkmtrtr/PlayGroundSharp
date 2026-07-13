using System.Net;
using System.Text;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.Worker.Tests;

public sealed class PackageSearchServiceTests
{
    [Fact]
    public async Task DiscoversSearchServiceAndReturnsPackageMetadata()
    {
        var handler = new StubHandler(
            """
            {"resources":[{"@id":"https://packages.test/query","@type":"SearchQueryService/3.5.0"}]}
            """,
            """
            {"totalHits":1,"data":[{"id":"Example.Package","version":"2.1.0","description":"Example package","authors":["Example Author"],"totalDownloads":1234,"verified":true}]}
            """);
        var service = new PackageSearchService(new HttpClient(handler), new Uri("https://packages.test/index.json"));

        var result = await service.SearchAsync("json serializer");

        Assert.Equal(1, result.TotalHits);
        var package = Assert.Single(result.Packages);
        Assert.Equal("Example.Package", package.PackageId);
        Assert.Equal("2.1.0", package.Version);
        Assert.Equal("Example Author", package.Authors);
        Assert.True(package.IsVerified);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("q=json%20serializer", handler.Requests[1].OriginalString, StringComparison.Ordinal);
        Assert.Contains("prerelease=false", handler.Requests[1].Query, StringComparison.Ordinal);
    }

    private sealed class StubHandler(string serviceIndex, string searchResult) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var json = request.RequestUri!.AbsolutePath.EndsWith("index.json", StringComparison.Ordinal)
                ? serviceIndex
                : searchResult;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
