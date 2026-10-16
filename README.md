# DeployAngel for .NET

The DeployAngel agent for ASP.NET Core, worker services, and Hangfire jobs.
It watches what your app does in production and reports one small
aggregated payload per process per minute. DeployAngel uses that to verify
every deployment, and to tell you (or your coding agent) when a release is
**cleared** and you can stop watching it.

## Install

Requires .NET 8 or later. The package has no dependencies beyond the .NET
and ASP.NET Core shared frameworks.

```bash
dotnet add package DeployAngel
```

```csharp
// Program.cs
builder.Services.AddDeployAngel();
```

Set this in production, for every process (web servers and job workers alike):

```bash
DEPLOYANGEL_TOKEN=da_live_...           # an ingestion token with the telemetry scope
```

On Heroku, the DeployAngel add-on sets it for you.

That's all for a web app. The agent puts its middleware first in the
pipeline, records every request and unhandled exception (including ones your
exception handler renders as an error page), starts with the host, and sends
the minute in progress when the host stops. Reporting is on when the host's
environment is `Production`.

In a worker service (`Host.CreateApplicationBuilder`), the same
`AddDeployAngel()` starts and stops the agent with the host. The package
uses the ASP.NET Core shared framework, so a worker that adds it runs on the
ASP.NET Core runtime: in Docker, the `mcr.microsoft.com/dotnet/aspnet` images
rather than `runtime`.

Keep the debug symbols (`.pdb` files) that `dotnet publish` writes next to
your assemblies. They're the default, and the agent reads them to tell which
source file each exception frame, route, and job is in, without your source
code ever being deployed or sent. Without them, exceptions are named by type
and method instead of file.

### Hangfire

Supports Hangfire 1.8 or later.

```bash
dotnet add package DeployAngel.Hangfire
```

```csharp
builder.Services.AddHangfire(config => config
    .UseDeployAngel()
    .UsePostgreSqlStorage(...));
```

Every job attempt is recorded, with its duration and whether it threw; a job
whose retries run out counts as discarded. Jobs are named by type and method,
as Hangfire names them (`Shop.Jobs.InvoiceJob.Send`). Recurring jobs are read
from Hangfire's storage with their cron schedules and time zones, so
DeployAngel expects each one and notices when one stops running after a
deploy.

### Without the generic host

A console app or script without `Host` starts and stops the agent itself:

```csharp
DeployAngelAgent.Start();
try
{
    await RunImportAsync();
}
finally
{
    DeployAngelAgent.Shutdown();      // sends what was recorded
}
```

### Which release is running

The agent must know which release it's running. It finds it in this order:

1. `DEPLOYANGEL_REVISION` (the commit SHA) and `DEPLOYANGEL_RELEASE_VERSION`, if you set them
2. Heroku dyno metadata. Enable it with `heroku labs:enable runtime-dyno-metadata`
   and `heroku labs:enable runtime-dyno-build-metadata`; both take effect on the
   next deploy
3. Kamal: `KAMAL_VERSION`
4. Render: `RENDER_GIT_COMMIT`
5. Fly.io: the deploy's image tag, from `FLY_IMAGE_REF`. Fly.io sets no commit, so
   pass one in for commit-level change tracking (`ENV DEPLOYANGEL_REVISION=$GIT_SHA`
   in the Dockerfile, with `--build-arg GIT_SHA=$(git rev-parse HEAD)`)
6. Railway: `RAILWAY_GIT_COMMIT_SHA`, or `RAILWAY_DEPLOYMENT_ID`
7. Coolify: `SOURCE_COMMIT`. Dokku: `GIT_REV`
8. the commit the app was built from: when it's built in a git checkout, the
   .NET SDK adds the commit to the app assembly's informational version
   (`1.0.0+3f2a9c1...`), and the agent reads it from there. A Docker build
   whose context leaves out `.git` doesn't have it
9. a `REVISION` file in the app's root
10. a git checkout: the commit `HEAD` names, read from `.git` in the app's root
    or up to 3 directories above it, without running `git`. This covers servers
    deployed by `git pull`
11. ECS, including Fargate: the container's image, from the metadata endpoint ECS
    provides (one local request at boot)
12. a code fingerprint: `code:` and the first 12 characters of the hash of the
    file digests the agent already sends (below), with no commit. The same
    code gives the same fingerprint, so a redeploy of unchanged code isn't a
    new release. It's computed in the background when the reporter starts, not
    at boot, and needs file digests on. Without a commit DeployAngel still
    shows which files changed in a release, but not its commits and pull
    requests, so the agent logs a warning: set `DEPLOYANGEL_REVISION` when
    you can

For other Docker deploys, bake the commit into the image:
`deployangel install docker` adds `ARG GIT_SHA` and
`ENV DEPLOYANGEL_REVISION=$GIT_SHA` to the end of your Dockerfile, where it
doesn't invalidate cached layers, and you build with
`--build-arg GIT_SHA=$(git rev-parse HEAD)`. On DigitalOcean App Platform, set
`DEPLOYANGEL_REVISION: ${_self.COMMIT_HASH}` in the app spec.

## What it sends

- HTTP request counts, 4xx/5xx counts by status code, unhandled exceptions,
  and a latency histogram, both per app and per route.
- Routes are recorded as the pattern the endpoint matched
  (`GET /orders/{id}`, `GET /Home/About/{id?}`), never the raw path, with
  route constraints and defaults left out. At most 100 routes are sent per
  payload; the rest are folded into `__other__`.
- Background jobs: attempts, failed attempts, discarded jobs, duration, and
  queue latency, per job. For queue latency, a Hangfire job created straight
  into a queue carries one extra job parameter, `DeployAngel.EnqueuedAt`, the
  time it was enqueued.
- Jobs still running at the end of each minute, and how long the oldest of
  each has been running, so a job that hangs is noticed.
- Release identity, runtime versions, and a per-process instance ID.
- Exceptions: a stable fingerprint, the exception class, the first line of the
  message, and application frames only (file paths and method names). In
  the message, numbers, IDs, emails, UUIDs, long hex strings, quoted values,
  and the request's host are replaced with placeholders. Other words are kept:
  `Payment failed for Jane Doe` is sent as it is. If your app's messages might
  hold personal or health data, [turn messages off](#exception-messages).
- Once per process: the route table (with each endpoint's controller and
  action and its source files), job names and files, recurring schedules
  declared in Hangfire or in configuration, critical flows, and file digests
  (repository-relative paths and the SHA-256 hashes the compiler recorded in
  the debug symbols, never file contents) so DeployAngel can tell which
  routes changed in a release. Disable digests with
  `DEPLOYANGEL_FILE_DIGESTS=false`.

It does not send request bodies, parameters, headers, cookies, SQL, logs, or
user data.

## Data and pricing

- Everything goes to DeployAngel's hosted service, which runs on DigitalOcean
  in the United States. There's no self-hosted version, and no choice of
  region.
- Telemetry is kept for 21 days, and release history for 7, 30, or 90 days
  depending on the plan. The [privacy policy](https://deployangel.com/privacy)
  has the details and the services DeployAngel uses.
- DeployAngel is free during the beta, with notice before paid plans start.
  See [pricing](https://deployangel.com/pricing).

## Safety

- Nothing runs on the network during a request or job. Recording only updates
  in-memory counters.
- Payloads are sent from a background thread, once a minute, with short
  timeouts.
- When DeployAngel is unreachable, the buffer is bounded (10 payloads, kept as
  gzipped JSON) and the oldest are dropped. Your app is never blocked or failed.
- The minute in progress is sent when the host stops, after everything else
  has stopped, within 2 seconds.
- Payloads are written without reflection, so the agent works in trimmed apps.

`dotnet run -c Release --project bench/Overhead` measures this. On an Apple
M-series laptop with .NET 10, the middleware adds about 1.2 µs to a request,
recording a job takes about 0.4 µs, and with every route, job, checkpoint,
and exception list at its cap the agent holds about 0.1 MB, including 10
unsent minutes.

## Configuration

Environment variables are enough for most apps. Settings can also go in the
`DeployAngel` configuration section (`appsettings.json`, or variables like
`DeployAngel__Environments__0`), and code passed to `AddDeployAngel` comes last:

```csharp
builder.Services.AddDeployAngel(options =>
{
    options.Environments = new[] { "production", "staging" };   // default: production only
});
```

```json
{
  "DeployAngel": {
    "Environments": ["production", "staging"]
  }
}
```

The agent reports in the environments listed. The environment is
`DEPLOYANGEL_ENVIRONMENT` if set, otherwise the host's environment name in
lowercase (`ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT`, `Production` when
neither is set). `DEPLOYANGEL_ENABLED=true|false` forces reporting on or off
anywhere. `DEPLOYANGEL_URL` overrides the API endpoint (default
`https://api.deployangel.com`).

File paths are relative to the repository's root. The package's build step
records it in your app's assembly: the nearest directory above the project
with `.git`, else the solution's directory, else the nearest with a
`.gitignore`, else the project's own. If yours is somewhere else, set the
`DeployAngelSourceRoot` MSBuild property, or `DEPLOYANGEL_SOURCE_ROOT` to the
path the build ran in. Deterministic CI builds (`ContinuousIntegrationBuild`)
map the repository to `/_/`, which the agent understands without either.
`DEPLOYANGEL_ROOT` is where the app runs, searched for a `REVISION` file and a
git checkout (the host's content root by default).

Critical flows (for example sign-up or password reset) are always listed in
clearance reports:

```csharp
options.CriticalFlows["password_reset"] = new[] { "POST /password-reset", "job:Shop.Jobs.ResetEmail.Send" };
```

Health checks aren't recorded: load balancers and uptime monitors call them
all the time and they always answer fast, so they would make your app look
busier and healthier than its real pages. The agent recognizes
`MapHealthChecks` endpoints wherever they're mounted, and any route at a
conventional path (`/up`, `/health`, `/healthz`, `/healthcheck`,
`/health_check`, `/livez`, `/readyz`, `/statusz`, `/ping`, `/ht`, `/alive`).
If yours is somewhere else, list it the way the dashboard shows it. HEAD
requests to it are left out too:

```csharp
options.IgnoredRoutes.Add("GET /status");
```

### Exception messages

To send exceptions without any message, only their class, fingerprint, and
application frames:

```csharp
options.ExceptionMessages = false;     // or DEPLOYANGEL_EXCEPTION_MESSAGES=false
```

Grouping, new-exception detection, and verdicts work the same, since the
fingerprint never uses the message. You lose the message text in the
dashboard, notifications, and AI investigation.

### Handled exceptions

Exceptions your code catches aren't seen. To report one for context (handled
exceptions never fail a release):

```csharp
try
{
    await SyncInventoryAsync();
}
catch (UpstreamException error)
{
    DeployAngelAgent.Notify(error);
}
```

### Recurring jobs

DeployAngel expects declared recurring jobs on schedule. It reads Hangfire's
recurring jobs, and also learns recurring jobs from their history.

#### Cron, Heroku Scheduler, and BackgroundService loops

Work that something outside Hangfire starts on a schedule, such as cron,
Heroku Scheduler, a Kubernetes CronJob, or a `BackgroundService` with a
timer, is recorded when you wrap it. Exceptions are recorded and rethrown:

```csharp
await DeployAngelAgent.RunTaskAsync("nightly import", async () =>
{
    await RunImportAsync();
});
```

Declare when it should run, by the same name, with a cron line or words like
`"every day at 4am"`, in the server's time zone unless you add one
(`"0 3 * * * America/New_York"`):

```csharp
options.RecurringJobs["nightly import"] = "30 2 * * *";
```

## Checkpoints

Errors and latency don't catch work that silently stops happening. Count the
business events that matter with one line:

```csharp
DeployAngelAgent.Checkpoint("order.created");
DeployAngelAgent.Checkpoint("webhook.stripe.processed", events.Count);
```

DeployAngel learns each checkpoint's normal rate relative to your traffic and
fails a release after which it drops sharply or stops, even when every request
and job still succeeds. Only drops are flagged, and a checkpoint without enough
traffic never blocks a release from being cleared. Checkpoints can also be part
of a critical flow (`checkpoint:order.created`).

The agent also records whether each checkpoint happened while handling an HTTP
request or while running a job (a Hangfire job or a `RunTask`, including one
run inline inside a request), so DeployAngel compares it against the right
traffic: requests for checkpoints recorded in requests, job runs for ones
recorded in jobs.

It's safe to call anywhere: it never throws, never touches the network, and is
ignored outside reporting environments. Names use letters, numbers, and
`. _ : -` (up to 100 characters); keep them to a fixed set rather than
including IDs, since only 100 distinct names are counted per minute.

## Other job systems

`DeployAngel.Integration.Instrumentation` is what the Hangfire integration
records through, for anyone adding another job system (Quartz.NET, a message
consumer): `RecordJob`, `JobStarted` and `JobFinished` for jobs still
running, `RecordDiscard`, `RecordException` with a `job_class:<name>` source,
`BeginJob` so checkpoints know they ran in a job, and `AddMetadataSource` for
the job classes and schedules it knows.

## Registering deploys

DeployAngel notices a new release when the agent first reports it, and
verifies it from there, with nothing to set up. On Heroku, the add-on also
registers every release for you.

To register deploys from CI, which also catches a release that never boots,
use an API token created for **CI deploys** in the dashboard. On GitHub
Actions, add [DeployAngel/verify-release](https://github.com/DeployAngel/verify-release)
after your deploy step: it registers the deploy, waits for the verdict, and
fails the step if the release fails.

```yaml
- uses: DeployAngel/verify-release@v1
  with:
    api-token: ${{ secrets.DEPLOYANGEL_API_TOKEN }}
```

Anywhere else, post the commit:

```bash
curl -fsS https://api.deployangel.com/api/v1/deployments \
  -H "Authorization: Bearer $DEPLOYANGEL_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"commit\": \"$GIT_SHA\"}"
```

## Command line and coding agents

The `deployangel` command and an MCP server for coding agents come as a .NET
tool:

```bash
dotnet tool install --global DeployAngel.Tool
```

or, with nothing installed, `dotnet tool exec DeployAngel.Tool -- <command>`
(`dnx DeployAngel.Tool <command>` on .NET 10). They talk only to
DeployAngel's API: they never start the agent or load your app. Give them a
token in `DEPLOYANGEL_API_TOKEN`: a "CLI & coding agents" token reads
verdicts, and a "CI deploys" token can also register deploys and report
checks.

To let a coding agent set DeployAngel up, use "Set up with your coding agent"
on the app's page in DeployAngel: it gives a one-time setup code, not tokens.
`deployangel setup --code=CODE` redeems it and saves the app's token and a "CI
deploys" token to `.deployangel/credentials.json`, readable only by you and
kept out of git, without printing either. The command and MCP server then use
the saved token whenever `DEPLOYANGEL_API_TOKEN` isn't set. `deployangel token
app` (or `api`) prints a saved token for piping into a host's or CI's
secrets, so it never appears on screen or in an agent's conversation:

```bash
fly secrets set DEPLOYANGEL_TOKEN=$(deployangel token app)
deployangel token api | gh secret set DEPLOYANGEL_API_TOKEN
```

```bash
deployangel verify --wait                   # current git HEAD, until a verdict
deployangel verify --wait --until=initial   # return at the 15-minute initial check
deployangel status                          # latest deployment
deployangel plan                            # what to exercise so a release clears sooner
deployangel exercise --url=https://example.com  # send the plan's read-only requests to production
deployangel release --commit=$SHA           # register a deploy (manual or CI)
deployangel check --name="smoke: signup" --status=pass --covers=registration
deployangel install kamal                   # a Kamal post-deploy hook that registers each deploy
deployangel install docker                  # bake the commit into the image as DEPLOYANGEL_REVISION
deployangel install agents                  # set up Claude Code, Cursor, and Codex in this project
deployangel doctor                          # check the setup
```

`deployangel doctor` checks this checkout (a `DeployAngel` package reference
in the project files or lock files, and an `AddDeployAngel` call in the code)
and, with a token, what DeployAngel has seen of the app: which processes
report and the release each runs, the routes, job classes, and scheduled
jobs the agent reported, and setup problems with what to do about each. Only
errors (a rejected token, an unreachable server, no agent in the project)
exit 5; warnings exit 0, so it's safe in CI.

Output is text on a terminal and JSON when piped (`--format=text|json`). Exit
codes: 0 cleared, 1 failed, 2 not cleared, 3 still verifying or timed out, 4
deployment not found, 5 usage, auth, or network error, 6 no problems so far
at the initial check (not cleared), 7 warnings at the initial check. In
GitHub Actions, `verify` also writes the verdict to the job's summary.

For coding agents, run `deployangel install agents` in the project. It adds
the MCP server to `.mcp.json` (Claude Code), `.cursor/mcp.json` (Cursor), and
`.codex/config.toml` (Codex, which reads it only in projects you trust), and
instructions to wait for a verdict after deploying to `AGENTS.md`, with a
`CLAUDE.md` that imports it. When the project lists the tool as a local tool
(`.config/dotnet-tools.json`), they run it as `dotnet deployangel`. Or add
the server yourself:

```bash
claude mcp add deployangel -- deployangel mcp
```

The tools are `get_verification`, `wait_for_verification`,
`get_exercise_plan`, `list_deployments`, `get_exception`,
`list_late_regressions`, and `register_deployment` when the token allows it.
None of them can change production.

The command and its output match the Ruby gem's and the Python package's
`deployangel` command, so the docs and agent instructions for any of them
apply to all.

## Development

```bash
dotnet test
```

The agent speaks DeployAngel Agent Protocol v1: one gzipped JSON payload per
process per minute to `POST /api/v1/telemetry`, and the application's metadata
once per process to `POST /api/v1/application_metadata`.
`src/DeployAngel/Core/Protocol.cs` and `src/DeployAngel/Core/Metadata.cs`
build them.
