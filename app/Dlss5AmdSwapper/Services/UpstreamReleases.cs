namespace Dlss5AmdSwapper.Services;

/// <summary>Digests of official DLSS-NR-on-AMD setup assets whose GitHub release metadata omits a digest.</summary>
public static class UpstreamReleases
{
    public static string? KnownSha256(string? tag, long size)
    {
        // Downloaded from the official v0.3.1 release and independently hashed.
        if (string.Equals(tag, "v0.3.1", StringComparison.OrdinalIgnoreCase) && size == 7_598_347)
            return "cf7ada1486b499700a84846b342ca2b1defdb4db622843f812151f255f2ad63c";
        if (string.Equals(tag, "v0.2.18", StringComparison.OrdinalIgnoreCase) && size == 7_570_162)
            return "dad67cc649ad91ba28e83c30049fc899900ae532daf818803bd1123e6e2315c3";
        if (string.Equals(tag, "v0.2.17", StringComparison.OrdinalIgnoreCase) && size == 7_538_418)
            return "4fcd167d07bc4964eaf9162aa8f4f11e852b91bf866b28cb48d45934022440bc";
        return null;
    }

    internal static string ResolveSha256(string? tag, long size, string? digest)
    {
        if (size <= 0) throw new InvalidOperationException("The official setup metadata contains an invalid size.");
        if (string.IsNullOrWhiteSpace(digest))
            return KnownSha256(tag, size)
                ?? throw new InvalidOperationException("GitHub did not publish a SHA-256 digest for the official setup.");
        // A malformed published digest must never silently fall back to an older value.
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("The official setup metadata contains an invalid SHA-256 digest.");
        return digest[7..].ToLowerInvariant();
    }
}
