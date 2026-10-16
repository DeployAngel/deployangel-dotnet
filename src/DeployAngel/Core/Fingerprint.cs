using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeployAngel.Core;

/// <summary>A stack frame, read from the exception without touching source files.</summary>
internal sealed record Frame(string? File, string Type, string Method, Assembly? Assembly);

/// <summary>
/// Exception fingerprint algorithm v1. Stable across deployments: no line
/// numbers, no messages, no package versions, no absolute paths, and no
/// compiler-generated names (an async method's state machine or a lambda
/// counts as the method it's written in).
/// </summary>
internal static class Fingerprint
{
    public const int Version = 1;
    public const int MaxMessage = 200;
    public const int MaxBacktrace = 20;
    public const int MaxFrames = 200;

    private static readonly (Regex Pattern, string Placeholder)[] MessagePlaceholders =
    {
        (new Regex(@"\b[A-Za-z0-9_.+-]+@[A-Za-z0-9_-]+\.[A-Za-z0-9_.-]+\b", RegexOptions.CultureInvariant), "<email>"),
        (new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "<uuid>"),
        (new Regex(@"\b0x[0-9a-f]+\b|\b[0-9a-f]{16,}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "<hex>"),
        (new Regex(@"([""'`]).*?\1", RegexOptions.CultureInvariant), "<string>"),
        (new Regex(@"\b[0-9]+(\.[0-9]+)?\b", RegexOptions.CultureInvariant), "<n>"),
    };

    /// <summary>
    /// message false leaves the message out entirely. redactions are
    /// (value, placeholder) pairs replaced first (Redaction).
    /// </summary>
    public static ExceptionDetails ForException(Exception exception, SourceMap map, bool message = true,
        IEnumerable<(string Value, string Placeholder)>? redactions = null)
    {
        exception = Unwrap(exception);
        return Details(ExceptionClass(exception), Frames(exception), map,
            message ? SafeMessage(exception) : null, redactions);
    }

    public static ExceptionDetails Details(string className, IReadOnlyList<Frame> frames, SourceMap map,
        string? message = null, IEnumerable<(string Value, string Placeholder)>? redactions = null)
    {
        var (frame, app) = TopFrame(frames, map);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", $"v{Version}", className, frame)));
        return new ExceptionDetails(Convert.ToHexString(digest).ToLowerInvariant()[..32], className,
            message is null ? null : NormalizeMessage(message, redactions), frame, app);
    }

    /// <summary>
    /// The exception that says what went wrong: reflection and a task's
    /// single failure wrap it.
    /// </summary>
    public static Exception Unwrap(Exception exception)
    {
        for (var depth = 0; depth < 10; depth++)
        {
            var inner = exception switch
            {
                TargetInvocationException { InnerException: { } e } => e,
                AggregateException { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
                _ => null,
            };
            if (inner is null) break;
            exception = inner;
        }
        return exception;
    }

    public static string ExceptionClass(Exception exception) => TypeName(exception.GetType());

    /// <summary>Frames innermost first.</summary>
    public static IReadOnlyList<Frame> Frames(Exception exception)
    {
        var frames = new List<Frame>();
        StackFrame[] stack;
        try
        {
            stack = new StackTrace(exception, fNeedFileInfo: true).GetFrames();
        }
        catch (Exception)
        {
            return frames;
        }
        foreach (var stackFrame in stack)
        {
            if (frames.Count >= MaxFrames) break;
            var method = stackFrame.GetMethod();
            if (method is null) continue;
            var (type, name) = Readable(method);
            string? file = null;
            try
            {
                file = stackFrame.GetFileName();
            }
            catch (Exception)
            {
                // No debug symbols: the frame is named by its type.
            }
            frames.Add(new Frame(file, type, name, method.Module.Assembly));
        }
        return frames;
    }

    /// <summary>
    /// The first application frame as "relative/Path.cs#Method" (or
    /// "Namespace.Type#Method" when its file isn't known), else the first
    /// frame as "Namespace.Type#Method"; and whether it's the application's.
    /// </summary>
    public static (string Frame, bool App) TopFrame(IReadOnlyList<Frame> frames, SourceMap map)
    {
        foreach (var frame in frames)
            if (map.IsAppAssembly(frame.Assembly)) return (Name(frame, map), true);
        return frames.Count == 0 ? ("unknown", false) : (Name(frames[0], map), false);
    }

    public static IReadOnlyList<string> Backtrace(Exception exception, SourceMap map) =>
        AppBacktrace(Frames(Unwrap(exception)), map);

    /// <summary>Application frames only.</summary>
    public static IReadOnlyList<string> AppBacktrace(IReadOnlyList<Frame> frames, SourceMap map) =>
        frames.Where(f => map.IsAppAssembly(f.Assembly)).Select(f => Name(f, map)).Take(MaxBacktrace).ToList();

    public static string Name(Frame frame, SourceMap map)
    {
        var path = map.IsAppAssembly(frame.Assembly) ? map.RelativePath(frame.File) : null;
        return $"{path ?? frame.Type}#{frame.Method}";
    }

    /// <summary>
    /// Only quoted values, numbers, emails, UUIDs, and long hex are replaced;
    /// unquoted words, such as a name in a message the app builds, are kept.
    /// </summary>
    public static string NormalizeMessage(string message, IEnumerable<(string Value, string Placeholder)>? redactions = null)
    {
        var text = message.Split('\n', '\r')[0].Trim();
        foreach (var (value, placeholder) in redactions ?? Array.Empty<(string, string)>())
        {
            text = Regex.Replace(text, @"(?<![^\W_])" + Regex.Escape(value) + @"(?![^\W_])", placeholder,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        foreach (var (pattern, placeholder) in MessagePlaceholders) text = pattern.Replace(text, placeholder);
        return text.Length > MaxMessage ? text[..MaxMessage] : text;
    }

    /// <summary>
    /// A type's name as code writes it, without generic arguments or
    /// assembly versions: "Shop.Orders.OrderService", "Shop.Cache`1+Entry".
    /// </summary>
    public static string TypeName(Type type)
    {
        if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
        if (type.DeclaringType is { } outer && !type.IsGenericParameter) return TypeName(outer) + "+" + type.Name;
        return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
    }

    /// <summary>
    /// The type and method a frame's code is written in. The compiler moves
    /// an async method's body into a state machine ("&lt;Create&gt;d__5.MoveNext"),
    /// and lambdas and local functions into methods named after the method
    /// that holds them ("&lt;Create&gt;b__5_0"), often on a nested class.
    /// </summary>
    public static (string Type, string Method) Readable(MethodBase method)
    {
        var name = method.Name;
        var type = method.DeclaringType;
        if (type is not null && type.Name.StartsWith('<'))
        {
            var original = Enclosed(type.Name);
            if (original is not null && name == "MoveNext") name = original;
            while (type is not null && type.Name.StartsWith('<')) type = type.DeclaringType;
        }
        if (name.StartsWith('<')) name = Enclosed(name) ?? name;
        return (type is null ? "<global>" : TypeName(type), name);
    }

    /// <summary>
    /// "Create" in "&lt;Create&gt;d__5", and "Main" in "&lt;&lt;Main&gt;$&gt;b__0_0" (a lambda in
    /// top-level statements); null for "&lt;&gt;c__DisplayClass5_0".
    /// </summary>
    private static string? Enclosed(string generated)
    {
        var depth = 0;
        for (var i = 0; i < generated.Length; i++)
        {
            if (generated[i] == '<') depth++;
            else if (generated[i] == '>' && --depth == 0)
            {
                if (i <= 1) return null;
                var inner = generated[1..i];
                return inner.StartsWith('<') ? Enclosed(inner) ?? inner : inner;
            }
        }
        return null;
    }

    private static string SafeMessage(Exception exception)
    {
        try
        {
            return exception.Message ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
