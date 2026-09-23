using System.Net;
using System.Text.Json;
using Dlss5AmdSwapper.Services;

internal static class RuntimeReleaseCatalogTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Runtime release checks validate official stable release metadata", () =>
        {
            foreach (var repository in new[] { RuntimeReleaseCatalog.AmdRepository, RuntimeReleaseCatalog.OptiScalerRepository, RuntimeReleaseCatalog.AmdNrRepository })
            {
                var url = $"https://github.com/{repository}/releases/tag/v0.3.1";
                var valid = JsonSerializer.Serialize(new { tag_name = "v0.3.1", html_url = url, draft = false, prerelease = false });
                if (RuntimeReleaseCatalog.Parse(repository, valid).Tag != "v0.3.1") throw new Exception("Release tag lost.");
                foreach (var invalid in new[] { "[]", "{}", valid.Replace("\"draft\":false", "\"draft\":true"),
                    valid.Replace("\"prerelease\":false", "\"prerelease\":true"),
                    valid.Replace("https://github.com/", "https://github.com.evil.example/"),
                    valid.Replace("https://github.com/", "https://user@github.com/"),
                    valid.Replace($"/{repository}/", "/unrelated/project/") })
                {
                    try { RuntimeReleaseCatalog.Parse(repository, invalid); }
                    catch (InvalidDataException) { continue; }
                    throw new Exception("Untrusted or unpublished release accepted.");
                }
            }
            return Task.CompletedTask;
        });
        await run("Unavailable runtime update checks do not claim installed versions are current", async () =>
        {
            using var client = new HttpClient(new UnavailableHandler());
            var report = await RuntimeReleaseCatalog.CheckAsync(client, RuntimeReleaseCatalog.OptiScalerRepository);
            if (!report.Contains("unavailable") || report.Contains("up to date")) throw new Exception("Network failure misreported.");
        });
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
