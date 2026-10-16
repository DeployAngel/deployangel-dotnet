using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using static DeployAngel.Tool.JsonValues;

namespace DeployAngel.Tool;

/// <summary>
/// Plain-text and Markdown rendering of the verdict document for people.
/// Everything shown comes from the document; nothing is inferred here.
/// Mirrors the Ruby gem's and the Python package's formatters, so all the
/// commands read the same.
/// </summary>
internal static class Formatter
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["http_5xx_rate"] = "HTTP 5xx rate",
        ["route_5xx_rate"] = "Route 5xx rate",
        ["p95_latency"] = "p95 latency",
        ["job_failure_rate"] = "Job failure rate",
        ["job_duration"] = "Job duration p95",
        ["queue_latency"] = "Queue latency p95",
        ["new_fingerprint"] = "New exception",
        ["fingerprint_amplification"] = "Exception amplification",
        ["missing_recurring_job"] = "Recurring job",
        ["first_use_failure"] = "First-use failure",
        ["external_check"] = "External check",
        ["checkpoint_rate"] = "Checkpoint",
    };

    private static readonly string[] Durations = { "p95_latency", "job_duration", "queue_latency" };
    private static readonly string[] Counts = { "new_fingerprint", "checkpoint_rate" };

    // Exercise plan statuses with something worth running now.
    public static readonly string[] Exercisable = { "exercisable", "waiting_for_activity" };

    private static readonly Dictionary<string, string> Reasons = new()
    {
        ["normally_active"] = "normally active",
        ["changed_in_release"] = "changed in this release",
        ["rarely_used"] = "rarely used",
        ["critical_flow"] = "critical flow",
    };

    private static readonly Dictionary<string, string> VerdictWords = new()
    {
        ["verified"] = "cleared",
        ["failed"] = "failed",
        ["inconclusive"] = "not cleared",
    };

    public static string Verification(JsonObject document)
    {
        var lines = new List<string?> { Statement(document), StatusLine(document) };
        lines.AddRange(Notes(document));
        var found = Problems(document);
        if (found.Count > 0)
        {
            lines.Add("Findings:");
            lines.AddRange(found.Select(finding => $"  {Text(Get(finding, "status")).PadRight(8)} {FindingLine(finding)}"));
        }
        var gathering = GatheringLine(document);
        if (gathering is not null)
        {
            lines.Add($"{gathering} (--format=json for details)");
        }
        foreach (var (title, items) in Sections(document))
        {
            lines.Add($"{title}:");
            lines.AddRange(items.Select(item => $"  {item}"));
        }
        if (Truthy(Get(document, "dashboard_url")))
        {
            lines.Add(Text(Get(document, "dashboard_url")));
        }
        return string.Join("\n", lines.Where(line => line is not null));
    }

    /// <summary>The same document as GitHub-flavored Markdown, for a CI job's summary.</summary>
    public static string Markdown(JsonObject document)
    {
        var lines = new List<string?>
        {
            $"### DeployAngel: {ReleaseName(document)} {Outcome(document)}", "", Statement(document) is { Length: > 0 } statement ? statement : "",
            "", StatusLine(document),
        };
        lines.AddRange(Notes(document).Select(note => $"\n{note}"));
        var found = Problems(document);
        if (found.Count > 0)
        {
            lines.AddRange(new[] { "", "| Status | Finding |", "| --- | --- |" });
            lines.AddRange(found.Select(finding => $"| {Text(Get(finding, "status"))} | {Cell(FindingLine(finding))} |"));
        }
        var gathering = GatheringLine(document);
        if (gathering is not null)
        {
            lines.AddRange(new[] { "", gathering });
        }
        foreach (var (title, items) in Sections(document))
        {
            lines.AddRange(new[] { "", $"**{title}**", "" });
            lines.AddRange(items.Select(item => $"- {item}"));
        }
        if (Truthy(Get(document, "dashboard_url")))
        {
            lines.AddRange(new[] { "", $"[Open in DeployAngel]({Text(Get(document, "dashboard_url"))})" });
        }
        return string.Join("\n", lines.Where(line => line is not null)) + "\n";
    }

    public static string? Statement(JsonObject document)
    {
        var statement = Or(Get(Get(document, "clearance"), "statement"), Get(document, "summary"));
        return statement is null ? null : Text(statement);
    }

    public static string StatusLine(JsonObject document)
    {
        var v = Get(document, "verification");
        var parts = new List<string> { $"State: {Text(Get(v, "state"))}" };
        if (Truthy(Get(v, "verdict")))
        {
            parts.Add($"Verdict: {Text(Get(v, "verdict"))}");
        }
        if (Truthy(Get(v, "confidence")))
        {
            parts.Add($"Confidence: {Text(Get(v, "confidence"))}");
        }
        if (Get(v, "coverage") is not null)
        {
            parts.Add($"Coverage: {Round(ToNumber(Get(v, "coverage")) * 100)}%");
        }
        return string.Join(" · ", parts);
    }

    public static List<string> Notes(JsonObject document)
    {
        var v = Get(document, "verification");
        // What a clearance doesn't cover, right under it (servers from
        // 2026-10-10 send it).
        var found = ItemsOf(Get(Get(Get(document, "clearance"), "scope"), "lines")).Select(Text).ToList();
        var check = Get(v, "initial_check");
        if (Truthy(check) && Get(v, "verdict") is null)
        {
            found.Add($"Initial check: {Text(Get(check, "result")).Replace('_', ' ')}. Not cleared yet.");
        }
        if (Truthy(Get(v, "expected_clearance_at")) && Get(v, "verdict") is null)
        {
            found.Add($"Expected clearance: {Text(Get(v, "expected_clearance_at"))}");
        }
        var source = Get(Get(document, "deployment"), "promoted_from");
        if (Truthy(source))
        {
            var verdict = StringOf(Get(source, "verdict"));
            var result = verdict is not null && VerdictWords.TryGetValue(verdict, out var word) ? word : "still verifying";
            var name = Truthy(Get(source, "version")) ? Text(Get(source, "version")) : Prefix(Get(source, "commit"), 7);
            found.Add($"Promoted from {Text(Get(source, "environment"))} {name} ({result})");
        }
        return found;
    }

    public static List<JsonNode?> Problems(JsonObject document) =>
        ItemsOf(Get(document, "findings"))
            .Where(finding => StringOf(Get(finding, "status")) is "failing" or "warning")
            .ToList();

    public static string? GatheringLine(JsonObject document)
    {
        var gathering = ItemsOf(Get(document, "findings")).Count(finding => StringOf(Get(finding, "status")) == "insufficient_data");
        if (gathering == 0)
        {
            return null;
        }
        return $"{gathering} signal{(gathering == 1 ? "" : "s")} still gathering data";
    }

    /// <summary>Titled lists that follow the findings, each item already a line.</summary>
    public static List<(string Title, List<string> Items)> Sections(JsonObject document)
    {
        var v = Get(document, "verification");
        var clearance = Get(document, "clearance");
        var changed = ItemsOf(Get(document, "rare_items_pending"))
            .Where(item => StringOf(Get(item, "changed_in_release")) == "changed");
        var exceptions = new List<string>();
        foreach (var e in ItemsOf(Get(document, "exceptions")))
        {
            var suffix = Get(e, "sources") is JsonObject { Count: > 0 } sources ? " " + string.Join(", ", sources.Select(pair => pair.Key)) : "";
            exceptions.Add($"{Text(Get(e, "exception_class"))} in {Text(Get(e, "top_frame"))} ({Text(Get(e, "count"))}x){suffix}");
        }
        var flows = new List<string>();
        foreach (var flow in ItemsOf(Get(document, "critical_flows")))
        {
            var note = Truthy(Get(flow, "changed_in_release")) ? " (changed in this release)" : "";
            flows.Add($"{Text(Get(flow, "name"))}: {Text(Get(flow, "status")).Replace('_', ' ')}{note}");
        }
        var found = new List<(string, List<string>)>
        {
            ("New exceptions", exceptions),
            ("Missing evidence", StringOf(Get(v, "verdict")) == "failed"
                ? new List<string>()
                : ItemsOf(Get(clearance, "missing_evidence")).Select(Text).ToList()),
            ("Not observable", ItemsOf(Get(clearance, "not_observable"))
                .Select(item => $"{Text(Get(item, "label"))} ({Text(Get(item, "reason"))})").ToList()),
            ("Still watching", ItemsOf(Get(clearance, "still_watching"))
                .Select(e => $"{Text(Get(e, "job_class"))} (expected by {Text(Get(e, "expected_by"))})").ToList()),
            ("Critical flows", flows),
            ("Changed but not yet exercised", changed.Select(item => Text(Get(item, "key"))).ToList()),
            ("To clear sooner, exercise (deployangel plan for details)", ExercisableItems(document).Take(5).ToList()),
            ("Late regressions", ItemsOf(Get(document, "late_regressions")).Select(late => Text(Get(late, "summary"))).ToList()),
        };
        return found.Where(section => section.Item2.Count > 0).ToList();
    }

    /// <summary>The release's exercise plan, for <c>deployangel plan</c>.</summary>
    public static string ExercisePlan(JsonObject document)
    {
        var plan = ObjectOf(Get(document, "exercise_plan"));
        var summary = Truthy(Get(plan, "summary")) ? Text(Get(plan, "summary")) : "No exercise plan in this response; update the server.";
        var lines = new List<string> { $"{ReleaseName(document)}: {summary}" };
        var shortfall = ShortfallLines(ObjectOf(Get(plan, "shortfall")));
        if (shortfall.Count > 0)
        {
            lines.Add("Short of:");
            lines.AddRange(shortfall.Select(line => $"  {line}"));
        }
        var items = ItemsOf(Get(plan, "items"));
        if (Exercisable.Contains(StringOf(Get(plan, "status"))))
        {
            ItemGroup(lines, "Needed to clear, exercise against production:", items.Where(Needed));
            ItemGroup(lines, "Also worth running, not needed to clear (changed or rarely used, watched on first use):",
                items.Where(item => !Needed(item)));
        }
        else
        {
            ItemGroup(lines, "Optional:", items);
        }
        if (items.Any(item => Truthy(Get(item, "mutating"))))
        {
            lines.Add("Use a test account, or ask first, for routes marked [changes data].");
        }
        if (Truthy(Get(plan, "report_with")))
        {
            lines.Add($"Then report it: {Text(Get(plan, "report_with"))}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Only what clearance waits on: the rest is in <c>deployangel plan</c>.</summary>
    public static List<string> ExercisableItems(JsonObject document)
    {
        var plan = Get(document, "exercise_plan");
        if (!Exercisable.Contains(StringOf(Get(plan, "status"))))
        {
            return new List<string>();
        }
        return ItemsOf(Get(plan, "items")).Where(Needed).Select(ItemLine).ToList();
    }

    /// <summary>
    /// Whether clearance waits on the item. Servers older than the needed
    /// flag listed normally active items first, so count those.
    /// </summary>
    public static bool Needed(JsonNode? item) =>
        Has(item, "needed") ? Truthy(Get(item, "needed")) : StringOf(Get(item, "reason")) == "normally_active";

    public static List<string> ShortfallLines(JsonObject shortfall)
    {
        var lines = new List<string>();
        var requests = Get(shortfall, "requests");
        if (Truthy(requests))
        {
            var rule = StringOf(Get(shortfall, "rule")) == "low_volume" ? " (low-traffic rule)" : "";
            lines.Add($"requests: {Text(Get(requests, "have"))} of {Text(Get(requests, "need"))}{rule}");
        }
        foreach (var (key, value) in shortfall)
        {
            if (key.StartsWith("routes_run_", StringComparison.Ordinal))
            {
                var times = new string(key.Where(char.IsDigit).ToArray());
                lines.Add($"routes run {times}+ times: {Text(Get(value, "have"))} of the {Text(Get(value, "need"))} needed " +
                          $"({Text(Get(value, "of"))} normally active)");
            }
        }
        var coverage = Get(shortfall, "coverage");
        if (Truthy(coverage))
        {
            lines.Add($"coverage: {Round(NumberOr0(Get(coverage, "have")) * 100)}% of {Round(NumberOr0(Get(coverage, "need")) * 100)}%");
        }
        var jobs = Get(shortfall, "jobs");
        if (Truthy(jobs))
        {
            if (Get(jobs, "classes_not_run") is not null)
            {
                lines.Add($"jobs not run yet: {string.Join(", ", ItemsOf(Get(jobs, "classes_not_run")).Select(Text))}");
            }
            else
            {
                var attempts = Get(jobs, "attempts");
                lines.Add($"job attempts: {Text(Get(attempts, "have"))} of {Text(Get(attempts, "need"))}");
            }
        }
        if (Truthy(Get(shortfall, "elevated")))
        {
            lines.Add($"elevated, review before exercising more: {string.Join(", ", ItemsOf(Get(shortfall, "elevated")).Select(Text))}");
        }
        if (Truthy(Get(shortfall, "critical_flows")))
        {
            lines.Add($"critical flows not run: {string.Join(", ", ItemsOf(Get(shortfall, "critical_flows")).Select(Text))}");
        }
        return lines;
    }

    public static string ItemLine(JsonNode? item)
    {
        var runsNeeded = Get(item, "runs_needed");
        var runs = (Truthy(runsNeeded) ? ToInteger(runsNeeded) : 0) > 1
            ? $"run {Text(Get(item, "runs"))} of {Text(runsNeeded)}"
            : "not run yet";
        if (StringOf(Get(item, "triggered_by")) == "app_behavior")
        {
            runs = "runs when the app starts it";
        }
        var reason = Get(item, "reason");
        var reasonText = StringOf(reason) is { } known && Reasons.TryGetValue(known, out var word) ? word : Text(reason);
        var parts = new List<string> { Text(Get(item, "key")), $"({reasonText}, {runs})" };
        if (Truthy(Get(item, "mutating")))
        {
            parts.Add("[changes data]");
        }
        if (Truthy(Get(item, "checked_by")))
        {
            parts.Add($"checked by {string.Join(", ", ItemsOf(Get(item, "checked_by")).Select(Text))}");
        }
        return string.Join(" ", parts);
    }

    public static string ReleaseName(JsonObject document)
    {
        var deployment = Get(document, "deployment");
        return Truthy(Get(deployment, "version")) ? Text(Get(deployment, "version")) : Prefix(Get(deployment, "commit"), 7);
    }

    public static string Outcome(JsonObject document)
    {
        var v = Get(document, "verification");
        var verdict = StringOf(Get(v, "verdict"));
        if (verdict is not null && VerdictWords.TryGetValue(verdict, out var word))
        {
            return word;
        }
        var result = Get(Get(v, "initial_check"), "result");
        if (StringOf(result) == "warnings")
        {
            return "has warnings, not cleared yet";
        }
        if (result is null)
        {
            return "is still being verified";
        }
        return "has no problems so far, not cleared yet";
    }

    public static string Exception(JsonObject details)
    {
        var lines = new List<string>
        {
            $"{Text(Get(details, "exception_class"))}: {Text(Get(details, "message"))}",
            $"Fingerprint {Text(Get(details, "fingerprint"))} · first seen {Text(Get(details, "first_seen_at"))} ({Text(Get(details, "first_seen_release"))})",
            $"{Text(Get(details, "occurrences_last_24h"))} occurrences in the last 24 hours",
        };
        lines.AddRange(ItemsOf(Get(details, "backtrace")).Select(frame => $"  {Text(frame)}"));
        return string.Join("\n", lines);
    }

    public static string FindingLine(JsonNode? finding)
    {
        var signal = StringOf(Get(finding, "signal"));
        var labelText = signal is not null && Labels.TryGetValue(signal, out var name) ? name : Text(Get(finding, "signal"));
        var label = $"{labelText} on {Text(Get(finding, "scope"))}";
        if (Counts.Contains(signal))
        {
            var observed = Get(finding, "observed_value");
            return $"{label}: {(Truthy(observed) ? ToInteger(observed) : 0)} occurrences";
        }
        if (signal == "missing_recurring_job")
        {
            // Its value is the job's interval in seconds, not a rate.
            var ran = StringOf(Get(finding, "status")) == "pass" ? "ran" : "didn't run";
            return $"{label}: {ran}, {Text(Get(finding, "threshold"))}";
        }
        if (Get(finding, "baseline_value") is null && Get(finding, "observed_value") is null)
        {
            return $"{label}: {Text(Get(finding, "threshold"))}";
        }
        var samples = Get(finding, "observed_n") is { } n ? $" ({Text(n)} samples)" : "";
        return $"{label}: {Value(signal, Get(finding, "baseline_value"))} -> {Value(signal, Get(finding, "observed_value"))}{samples}";
    }

    private static void ItemGroup(List<string> lines, string heading, IEnumerable<JsonNode?> items)
    {
        var group = items.ToList();
        if (group.Count > 0)
        {
            lines.Add(heading);
            lines.AddRange(group.Select(item => $"  {ItemLine(item)}"));
        }
    }

    private static string Value(string? signal, JsonNode? raw)
    {
        if (raw is null)
        {
            return "n/a";
        }
        if (Counts.Contains(signal))
        {
            return ToInteger(raw).ToString(CultureInfo.InvariantCulture);
        }
        if (Durations.Contains(signal))
        {
            return $"{Round(ToNumber(raw))} ms";
        }
        return $"{Round(ToNumber(raw) * 100, 2)}%";
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Prefix(JsonNode? value, int length)
    {
        var text = Truthy(value) ? Text(value) : "";
        return text[..Math.Min(text.Length, length)];
    }

    private static double NumberOr0(JsonNode? value) => Truthy(value) ? ToNumber(value) : 0;

    /// <summary>
    /// Rounds halves away from zero, as Ruby does, so all the commands print
    /// the same numbers. Whole numbers print without a decimal point; to
    /// digits, as Python prints a float.
    /// </summary>
    internal static string Round(double value, int digits = 0)
    {
        decimal exact;
        try
        {
            // From the shortest text that round-trips, as Python's
            // Decimal(str(value)) does, so 0.565 * 100 is 56.49999999999999.
            exact = decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is OverflowException or FormatException)
        {
            var rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
            return digits == 0 ? rounded.ToString("F0", CultureInfo.InvariantCulture) : FloatText(rounded);
        }
        var result = Math.Round(exact, digits, MidpointRounding.AwayFromZero);
        return digits == 0
            ? decimal.Truncate(result).ToString(CultureInfo.InvariantCulture)
            : FloatText((double)result);
    }
}
