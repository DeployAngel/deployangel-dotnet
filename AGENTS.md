# DeployAngel agent for .NET: working rules

The `DeployAngel` packages run inside customers' production .NET apps.
These rules come before any feature.

## Never harm the customer's app

- No DeployAngel network calls during a request or job. Payloads go out from a
  background thread, once a minute, with short timeouts.
- Fail open: every public method catches its own errors. Nothing may throw
  into, or block, the app.
- Keep buffers bounded, and drop telemetry rather than block or fail.
- Keep overhead very low: around 1% CPU or less, where practical
  (`dotnet run -c Release --project bench/Overhead`).
- If DeployAngel is down, the customer's app must not notice.
- No NuGet dependencies in `DeployAngel`: the .NET and ASP.NET Core shared
  frameworks only. `DeployAngel.Hangfire` depends on Hangfire.Core and nothing
  else. No reflection-based serialization: payloads are written with
  `Utf8JsonWriter`, so trimmed apps work.

## Send aggregates, never events

- One payload per process per minute, whatever the traffic.
- Only mergeable values: counts and histograms. Never percentiles or rates;
  the cloud computes those after merging processes.
- Bound every list in a payload (routes, exceptions, jobs, checkpoints).

## Send as little as possible

Never collect request bodies, parameters, cookies, authorization headers,
session contents, SQL parameters, email addresses, other personal data, or raw
logs. Record route patterns (`/orders/{id}`), never raw paths, and sanitize
exception messages. Source code never leaves the app: the agent reads paths
and hashes from the debug symbols, never file contents. If a change would add
a line to the README's "What it sends" section, that's a decision for the
maintainer, not an implementation detail.

## Keep the protocol framework-neutral

The Agent Protocol speaks in HTTP, routes, exceptions, jobs, queues, and
scheduled tasks, never in controllers, endpoints, or Hangfire filters. The
integrations (`src/DeployAngel/AspNetCore/`, `src/DeployAngel.Hangfire/`)
translate their framework into those concepts through
`DeployAngel.Integration.Instrumentation`, and the Ruby and Python agents
speak the same protocol. Protocol changes must work for all three, and the
DeployAngel cloud has to accept them first.

## The deployangel command

`src/DeployAngel.Tool` is the command and MCP server for developers, CI, and
coding agents. It talks only to DeployAngel's API, never references the agent
or ASP.NET Core, and never starts the agent. It must match the Ruby gem's and
Python package's `deployangel` command: the same commands, options, text
output, exit codes, and MCP tools. `tests/DeployAngel.Tool.Tests` is ported
case for case from the Python package's CLI tests; change them together.

## Compatibility and tests

- Supports .NET 8 or later and Hangfire 1.8 or later. The libraries target
  `net8.0`, so don't use newer APIs without a fallback.
- `dotnet test` must pass, with no warnings (they're errors). Test the
  failure paths too: network errors, timeouts, full buffers, missing debug
  symbols.
- Update the README and CHANGELOG when what the agent sends, or how it's
  configured, changes.

## Releasing

Run `bin/release X.Y.Z` from an up-to-date `main` with the changes under the
CHANGELOG's "## Unreleased" heading. It checks X.Y.Z is newer than NuGet's
latest, sets the version in `Directory.Build.props`, dates the CHANGELOG
section, runs the tests, commits "Release X.Y.Z", and tags `vX.Y.Z`; with
`--push` it also pushes. `.github/workflows/release.yml` packs the three
packages, waits for a maintainer to approve the `nuget` environment,
publishes them to nuget.org through trusted publishing (no API key), and
then creates the GitHub release.
