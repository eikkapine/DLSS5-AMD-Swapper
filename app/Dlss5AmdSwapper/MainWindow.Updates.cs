using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace Dlss5AmdSwapper;

public partial class MainWindow
{
    private bool _checkingUpdate;
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
