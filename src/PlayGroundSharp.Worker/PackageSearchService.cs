using System.Net.Http.Headers;
using System.Text.Json;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Searches a NuGet V3 source after discovering its SearchQueryService endpoint.</summary>
public sealed class PackageSearchService
{
    private static readonly Uri NuGetServiceIndex = new("https://api.nuget.org/v3/index.json");
    private readonly HttpClient httpClient;
    private readonly Uri serviceIndex;
    private Uri? searchEndpoint;

    public PackageSearchService(HttpClient? httpClient = null, Uri? serviceIndex = null)
    {
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        this.serviceIndex = serviceIndex ?? NuGetServiceIndex;
        if (!this.httpClient.DefaultRequestHeaders.UserAgent.Any())
            this.httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PlayGroundSharp", "1.0"));
    }

    public async Task<PackageSearchResultsEvent> SearchAsync(
        string query,
        bool includePrerelease = false,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length > 200) throw new ArgumentException("Package search query is too long.", nameof(query));
        take = Math.Clamp(take, 1, 50);
        var endpoint = searchEndpoint ??= await DiscoverSearchEndpointAsync(cancellationToken).ConfigureAwait(false);
        var separator = string.IsNullOrEmpty(endpoint.Query) ? '?' : '&';
        var requestUri = new Uri(endpoint + $"{separator}q={Uri.EscapeDataString(normalizedQuery)}&skip=0&take={take}&prerelease={includePrerelease.ToString().ToLowerInvariant()}&semVerLevel=2.0.0");

        using var response = await httpClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var totalHits = root.TryGetProperty("totalHits", out var totalElement) && totalElement.TryGetInt64(out var total)
            ? total
            : 0;
        var packages = new List<NuGetPackageInfo>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var packageId = GetString(item, "id");
                var version = GetString(item, "version");
                if (packageId.Length == 0 || version.Length == 0) continue;
                packages.Add(new(
                    packageId,
                    version,
                    GetString(item, "description"),
                    GetAuthors(item),
                    item.TryGetProperty("totalDownloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : 0,
                    item.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True,
                    GetVersions(item, version)));
            }
        }

        return new(normalizedQuery, totalHits, packages);
    }

    private async Task<Uri> DiscoverSearchEndpointAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(serviceIndex, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = new List<(Uri Uri, int Rank)>();
        if (document.RootElement.TryGetProperty("resources", out var resources))
        {
            foreach (var resource in resources.EnumerateArray())
            {
                var id = GetString(resource, "@id");
                if (!Uri.TryCreate(id, UriKind.Absolute, out var uri) ||
                    !resource.TryGetProperty("@type", out var typeElement)) continue;
                foreach (var type in EnumerateTypes(typeElement))
                {
                    if (!type.StartsWith("SearchQueryService", StringComparison.Ordinal)) continue;
                    var rank = type.Contains("/3.5.0", StringComparison.Ordinal) ? 3 :
                        type.Contains("/3.0.0", StringComparison.Ordinal) ? 2 : 1;
                    candidates.Add((uri, rank));
                }
            }
        }

        return candidates.OrderByDescending(static candidate => candidate.Rank).Select(static candidate => candidate.Uri).FirstOrDefault()
            ?? throw new InvalidDataException("NuGet service index does not expose SearchQueryService.");
    }

    private static IEnumerable<string> EnumerateTypes(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString() ?? string.Empty],
        JsonValueKind.Array => element.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty),
        _ => []
    };

    private static string GetAuthors(JsonElement item)
    {
        if (!item.TryGetProperty("authors", out var authors)) return string.Empty;
        return authors.ValueKind switch
        {
            JsonValueKind.Array => string.Join(", ", authors.EnumerateArray()
                .Where(static author => author.ValueKind == JsonValueKind.String)
                .Select(static author => author.GetString())
                .Where(static author => !string.IsNullOrWhiteSpace(author))),
            JsonValueKind.String => authors.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static IReadOnlyList<string> GetVersions(JsonElement item, string currentVersion)
    {
        var versions = item.TryGetProperty("versions", out var versionItems) && versionItems.ValueKind == JsonValueKind.Array
            ? versionItems.EnumerateArray()
                .Select(static versionItem => GetString(versionItem, "version"))
                .Where(static version => version.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (!versions.Contains(currentVersion, StringComparer.OrdinalIgnoreCase)) versions.Add(currentVersion);
        return versions;
    }

    private static string GetString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
