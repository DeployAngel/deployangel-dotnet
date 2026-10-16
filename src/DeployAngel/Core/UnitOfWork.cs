using System.Threading;

namespace DeployAngel.Core;

/// <summary>
/// The unit of work the current code runs in: an HTTP request, a job, or
/// neither. Integrations mark where they handle a request or run a job, so
/// each checkpoint can say where it was recorded and the cloud compares it
/// against the right traffic.
///
/// An AsyncLocal follows threads and async flows separately. Nested units (a
/// job run inline inside a request) stack, and the innermost wins; ending one
/// restores the one around it.
/// </summary>
internal static class UnitOfWork
{
    public const string Http = "http";
    public const string Job = "job";

    private static readonly AsyncLocal<string?> Current_ = new();

    public static string? Current => Current_.Value;

    /// <summary>Returns the unit to restore; pass it to <see cref="End"/>.</summary>
    public static string? Begin(string kind)
    {
        var previous = Current_.Value;
        Current_.Value = kind;
        return previous;
    }

    public static void End(string? previous) => Current_.Value = previous;
}
