using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Dlss5AmdSwapper.Services;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private bool _checkingUpdate;
    private bool _checkingRuntimeUpdates;

    private async void CheckRuntimeUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingRuntimeUpdates) return;
        _checkingRuntimeUpdates = true;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-AMD-Swapper");
            var results = await Task.WhenAll(
                RuntimeReleaseCatalog.CheckAsync(client, RuntimeReleaseCatalog.AmdRepository),
                RuntimeReleaseCatalog.CheckAsync(client, RuntimeReleaseCatalog.OptiScalerRepository));
            if (_closing) return;
            var packageVersion = _optiPackage?.ForkVersion ?? "not verified in this session";
            ShowDiagnosticsOverlay("Runtime updates", "Latest stable releases from the official GitHub projects.",
                string.Join("\n\n", results) +
                $"\n\nSelected AMD pre-SR package: {packageVersion}" +
                "\n\nDirect Game: run Set up / Update to download and verify the official AMD runtime. Game-specific compatibility pins remain in effect." +
                "\n\nLossless Scaling: update the user-supplied compatibility version.dll through bridge setup." +
                "\n\nStock OptiScaler releases cannot replace the custom amd-presr neural package. Use a complete compatible fork package and verify it before updating." +
                "\n\nThis check does not change installed runtimes or prove game compatibility.",
                "Open AMD releases", () => Process.Start(new ProcessStartInfo(DirectGameInstallerService.UpstreamReleasePage) { UseShellExecute = true }));
        }
        catch (Exception error) { if (!_closing) ShowToast("Runtime update check failed: " + error.Message); }
        finally { _checkingRuntimeUpdates = false; }
    }

    private async void CheckAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSS5-AMD-Swapper/0.2");
            var json = await client.GetStringAsync("https://api.github.com/repos/eikkapine/DLSS5-AMD-Swapper/releases?per_page=5");
            using var document = JsonDocument.Parse(json);
            var releases = document.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean()).ToArray();
            if (releases.Length == 0) { ShowToast("No published app release found."); return; }
            var newest = releases[0];
            var tag = newest.GetProperty("tag_name").GetString() ?? "Unknown";
            var url = newest.GetProperty("html_url").GetString();
            if (url is null || !url.StartsWith("https://github.com/eikkapine/DLSS5-AMD-Swapper/releases/tag/", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected app release address.");
            ShowDiagnosticsOverlay("App releases", "Updates are downloaded from this project's GitHub releases. Your game installations are kept separately.",
                $"Installed manager: {typeof(MainWindow).Assembly.GetName().Version}\nNewest published release: {tag}\n\n{url}\n\nDownload and extract the new portable ZIP, then close the running manager before replacing its files.",
                "Open release", () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        }
        catch (Exception ex) { ShowToast("App update check failed: " + ex.Message); }
        finally { _checkingUpdate = false; }
    }
}
