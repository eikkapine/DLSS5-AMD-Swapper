using System.Text.Json;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper.Services;

public static class ManagedManifest
{
    public static string? FindManifestPath(GameEntry game)
    {
        if (File.Exists(game.ManifestPath)) return game.ManifestPath;
        if (File.Exists(game.LegacyManifestPath)) return game.LegacyManifestPath;
        return null;
    }

    public static InstallRoute ReadRoute(GameEntry game)
    {
        var path = FindManifestPath(game);
        return path is null ? InstallRoute.None : ReadRoute(path);
    }

    public static InstallRoute ReadRoute(string manifestPath)
    {
        try
        {
            using var stream = File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return InstallRoute.None;
            var route = document.RootElement.TryGetProperty("route", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return InstallRoutes.Parse(route);
        }
        catch (JsonException) { return InstallRoute.None; }
        catch (IOException) { return InstallRoute.None; }
        catch (UnauthorizedAccessException) { return InstallRoute.None; }
    }
}
