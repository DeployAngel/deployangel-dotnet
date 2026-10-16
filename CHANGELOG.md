# Changelog

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
