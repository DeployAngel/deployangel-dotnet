using System;
using System.Collections.Generic;
using System.Threading;

namespace DeployAngel.Core;

/// <summary>
/// Values that name a customer of the app rather than its code, replaced in
/// exception messages before they're cleaned: the request's host. Kept per
/// async flow, where the request that raised ran.
/// </summary>
internal static class Redaction
{
    // Shorter values would replace ordinary words.
    public const int MinLength = 3;
    private static readonly AsyncLocal<string?> Host = new();

    public static void SetRequestHost(string? host) => Host.Value = host;

    /// <summary>(value, placeholder) pairs, longest first.</summary>
    public static IReadOnlyList<(string Value, string Placeholder)> Current()
    {
        var host = Host.Value ?? "";
        var colon = host.LastIndexOf(':');
        if (colon >= 0 && colon < host.Length - 1 && IsDigits(host.AsSpan(colon + 1))) host = host[..colon];
        return host.Length >= MinLength ? new[] { (host, "<host>") } : Array.Empty<(string, string)>();
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (c < '0' || c > '9') return false;
        return true;
    }
}
