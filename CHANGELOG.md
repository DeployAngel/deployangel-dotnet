# Changelog

## 0.1.9 (2026-11-05)

- The agent keeps an hour of unsent minutes (60 payloads, up from 10) when
  DeployAngel can't be reached, so an outage of up to an hour loses no
  telemetry. A minute that is lost leaves a gap DeployAngel has to learn
  the app's normal traffic around. Kept gzipped, a full hour holds 0.47 MB
  with every list at its cap (`bench/Overhead`, which now fills the
  configured buffer). `MaxQueuedPayloads` still sets it. Matches the Ruby
  gem and the Python package.

## 0.1.8 (2026-11-04)

- `deployangel doctor` warns when the project's AGENTS.md or CLAUDE.md has
  an older copy of the block `deployangel install agents` writes, and
  `deployangel status` says so on stderr at the start of a session, where
  a coding agent reads it. Running `deployangel install agents` updates
  it. Hand-written instructions without the block's markers aren't
  checked. Matches the Ruby gem and the Python package.

## 0.1.7 (2026-11-04)

- `deployangel verify` shows a volume drop (servers from 2026-11-04) as
  what ran against what was expected (`0 requests, about 27 expected`),
  and lists findings a person accepted as an intended change, with who
  and why. A failing finding that could be intended gets the link where a
  person can accept it; the CLI and MCP server can't, so an agent never
  accepts its own failure. Matches the Ruby gem and the Python package.
- The MCP server's instructions, and the block `deployangel install
  agents` writes, tell agents to hand that link to the user with their
  evidence and never report the release as a success.

## 0.1.6 (2026-11-03)

- An exception raised entirely in libraries, with no frame from the app's
  code, now sends its first library frames as its stack trace
  (`MailKit.Net.Smtp.SmtpClient#Send`), named like the top frame. Before,
  it sent none, so an error such as a background job's SMTP timeout showed
  no trace. Matches the Ruby gem and the Python package.

## 0.1.5 (2026-11-01)

- `deployangel exercise --param id=42` fills path parameters (`{id}`,
  `{id:int}`, `:id`), so routes with IDs in their paths can be exercised.
  Skipped routes name the parameters they need.
- `deployangel exercise --header="Name: value"`, or
  `DEPLOYANGEL_EXERCISE_HEADERS` (one per line), sends headers with every
  request, such as a Bearer token or a test account's session cookie. The
  output names them but never shows their values, and they go only to the
  `--url` host. Matches the Ruby gem and the Python package.

## 0.1.4 (2026-11-01)

- `deployangel exercise` prints each route's status codes (`404 ×1`)
  instead of their class (`1 4xx`). When every page answers 404, 405, or
  410, as when `--url` is the app's marketing site rather than its own
  host, it says so, records nothing on the release, and exits 5. Matches
  the Ruby gem and the Python package.

## 0.1.3 (2026-11-01)

- `deployangel verify` prints a failed release's next steps: the last
  cleared release a rollback would go to, with the migrations it would
  cross, and where to look first. They come from the verdict document's new
  `next_steps` field, so servers before 2026-11-01 show none. The coding
  agent instructions mention them, and still say to ask before rolling back.

## 0.1.2 (2026-10-21)

- `deployangel mcp` starts without an API token. Each tool then answers with
  how to set `DEPLOYANGEL_API_TOKEN` (or run `deployangel setup`), instead of
  the server failing to start, which many coding agents only report as
  "failed to start".

## 0.1.1 (2026-10-21)

- The instructions `deployangel install agents` writes to `AGENTS.md` now tell
  coding agents to check recent releases and late regressions at the start
  of a session, and to read `fix_check` in the verdict after deploying a fix
  for a failed release. Run it again to update them.

## 0.1.0 (2026-10-16)

- First release: the DeployAngel agent for ASP.NET Core and worker services
  (`DeployAngel`), Hangfire jobs and recurring jobs (`DeployAngel.Hangfire`),
  and the `deployangel` command and MCP server as a .NET tool
  (`DeployAngel.Tool`), speaking Agent Protocol v1 like the Ruby and Python
  agents.
- Exception frames, the files behind each route and job, and file digests
  come from the app's portable debug symbols, made relative to the
  repository root the package's build step records in the app's assembly.
- The release can also come from the commit the .NET SDK stamps in the app
  assembly's informational version (release source `assembly_version`).
