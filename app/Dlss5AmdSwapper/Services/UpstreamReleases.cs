namespace Dlss5AmdSwapper.Services;

/// <summary>Digests of official DLSS-NR-on-AMD setup assets whose GitHub release metadata omits a digest.</summary>
public static class UpstreamReleases
{
    public static string? KnownSha256(string? tag, long size)
    {
        if (string.Equals(tag, "v0.2.18", StringComparison.OrdinalIgnoreCase) && size == 7_570_162)
            return "dad67cc649ad91ba28e83c30049fc899900ae532daf818803bd1123e6e2315c3";
        if (string.Equals(tag, "v0.2.17", StringComparison.OrdinalIgnoreCase) && size == 7_538_418)
            return "4fcd167d07bc4964eaf9162aa8f4f11e852b91bf866b28cb48d45934022440bc";
        return null;
    }
}
