using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeployAngel.Tool;

/// <summary>A bad command line: reported with exit 5 and a pointer to the help.</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}

/// <summary>
/// The options of one command, parsed as the Python command's argparse parses
/// them, so a mistake gets the same message from every deployangel command:
/// --name=value or --name value, the last one wins, no abbreviations, and
/// anything else is "unrecognized arguments".
/// </summary>
internal sealed class OptionParser
{
    private static readonly Regex NegativeNumber = new(@"\A-\d+\z|\A-\d*\.\d+\z", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, Option> _options = new(StringComparer.Ordinal);

    private enum Kind
    {
        String,
        Flag,
        Integer,
    }

    public OptionParser String(string flag, string? dest = null, string? defaultValue = null, params string[] choices)
    {
        _options[flag] = new Option(flag, dest ?? DestFor(flag), Kind.String, defaultValue, choices);
        return this;
    }

    public OptionParser Flag(string flag, string? dest = null)
    {
        _options[flag] = new Option(flag, dest ?? DestFor(flag), Kind.Flag, null, Array.Empty<string>());
        return this;
    }

    public OptionParser Integer(string flag, string? dest, int defaultValue)
    {
        _options[flag] = new Option(flag, dest ?? DestFor(flag), Kind.Integer,
            defaultValue.ToString(CultureInfo.InvariantCulture), Array.Empty<string>());
        return this;
    }

    public ParsedOptions Parse(IReadOnlyList<string> args)
    {
        var values = _options.Values.ToDictionary(option => option.Dest, option => option.Default, StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var extras = new List<string>();
        var positionalOnly = false;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (positionalOnly || !LooksLikeOption(arg))
            {
                extras.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                positionalOnly = true;
                extras.Add(arg);
                continue;
            }
            var (option, explicitValue) = Match(arg);
            if (option is null)
            {
                extras.Add(arg);
                continue;
            }
            if (option.Kind == Kind.Flag)
            {
                if (explicitValue is not null)
                {
                    throw new UsageException($"argument {option.Flag}: ignored explicit argument '{explicitValue}'");
                }
                flags.Add(option.Dest);
                continue;
            }
            var value = explicitValue;
            if (value is null)
            {
                if (index + 1 >= args.Count || args[index + 1] == "--" || LooksLikeOption(args[index + 1]))
                {
                    throw new UsageException($"argument {option.Flag}: expected one argument");
                }
                value = args[++index];
            }
            values[option.Dest] = Check(option, value);
        }
        if (extras.Count > 0)
        {
            throw new UsageException($"unrecognized arguments: {string.Join(" ", extras)}");
        }
        return new ParsedOptions(values, flags);
    }

    private static string Check(Option option, string value)
    {
        if (option.Kind == Kind.Integer &&
            !int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            throw new UsageException($"argument {option.Flag}: invalid int value: '{value}'");
        }
        if (option.Choices.Length > 0 && !option.Choices.Contains(value))
        {
            throw new UsageException($"argument {option.Flag}: invalid choice: '{value}' (choose from {string.Join(", ", option.Choices)})");
        }
        return value;
    }

    private (Option? Option, string? ExplicitValue) Match(string arg)
    {
        if (_options.TryGetValue(arg, out var exact))
        {
            return (exact, null);
        }
        var equals = arg.IndexOf('=', StringComparison.Ordinal);
        if (equals > 0 && _options.TryGetValue(arg[..equals], out var named))
        {
            return (named, arg[(equals + 1)..]);
        }
        return (null, null);
    }

    /// <summary>
    /// As argparse decides: "-" alone, negative numbers, and text with spaces
    /// are values, unless they name an option.
    /// </summary>
    private bool LooksLikeOption(string arg)
    {
        if (arg.Length == 0 || arg[0] != '-')
        {
            return false;
        }
        if (_options.ContainsKey(arg))
        {
            return true;
        }
        if (arg.Length == 1)
        {
            return false;
        }
        var equals = arg.IndexOf('=', StringComparison.Ordinal);
        if (equals > 0 && _options.ContainsKey(arg[..equals]))
        {
            return true;
        }
        return !NegativeNumber.IsMatch(arg) && !arg.Contains(' ', StringComparison.Ordinal);
    }

    private static string DestFor(string flag) => flag.TrimStart('-').Replace('-', '_');

    private sealed record Option(string Flag, string Dest, Kind Kind, string? Default, string[] Choices);
}

internal sealed class ParsedOptions
{
    private readonly Dictionary<string, string?> _values;
    private readonly HashSet<string> _flags;

    public ParsedOptions(Dictionary<string, string?> values, HashSet<string> flags)
    {
        _values = values;
        _flags = flags;
    }

    public string? this[string dest] => _values.TryGetValue(dest, out var value) ? value : null;

    public bool Flag(string dest) => _flags.Contains(dest);

    public int Integer(string dest) => int.Parse(this[dest]!.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}
