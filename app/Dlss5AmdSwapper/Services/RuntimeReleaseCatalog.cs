using System.Net.Http;
using System.Text.Json;

namespace Dlss5AmdSwapper.Services;

public static class RuntimeReleaseCatalog
{
    public const string AmdRepository = "danielblnc/DLSS-NR-on-AMD";
    public const string OptiScalerRepository = "optiscaler/OptiScaler";
    public const string AmdNrRepository = "3zwr1/AMD-NR---OptiScaler";

    public static async Task<string> CheckAsync(HttpClient client, string repository)
    {
        ValidateRepository(repository);
        try
        {
            var json = await client.GetStringAsync($"https://api.github.com/repos/{repository}/releases/latest");
            var release = Parse(repository, json);
            return $"{repository}: {release.Tag}\n{release.Url}";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            return $"{repository}: update check unavailable ({error.Message})";
        }
    }

    public static RuntimeRelease Parse(string repository, string json)
    {
        ValidateRepository(repository);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("html_url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var preview) || preview.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("No stable published release was returned.");
        var tag = tagElement.GetString();
        var url = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(tag) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith($"/{repository}/releases/tag/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Length <= $"/{repository}/releases/tag/".Length)
            throw new InvalidDataException("Unexpected upstream release address.");
        return new RuntimeRelease(tag, uri.AbsoluteUri);
    }

    private static void ValidateRepository(string repository)
    {
        if (repository != AmdRepository && repository != OptiScalerRepository && repository != AmdNrRepository)
            throw new ArgumentException("Unsupported runtime repository.", nameof(repository));
    }
}

public sealed record RuntimeRelease(string Tag, string Url);
