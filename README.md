# ViVeTool Studio

ViVeTool Studio is a Windows 11 desktop application for viewing and changing Windows feature configurations
(the A/B "feature experiment" mechanism used by Windows 10 and newer). It is a fork of
[ViVe / ViVeTool](https://github.com/thebookisclosed/ViVe) by @thebookisclosed and keeps the original
`ViVeTool` command-line tool available. The CLI and the GUI share one feature-management engine.

> **Warning.** Feature IDs control hidden, experimental and unsupported Windows functionality. Changing them can
> break features or make Windows unstable, and enabling a feature does not guarantee a visible change.
> ViVeTool Studio records each feature's previous state for rollback, but that is **not** a system backup.

## Status

This is an MVP (Phase 1 and Phase 2 of the [roadmap](docs/ROADMAP.md)).

| Area | State |
|---|---|
| ViVeTool CLI (all 0.3.4 commands) | Preserved; now built on the shared engine, with exit codes ([details](docs/CLI.md)) |
| Shared engine (`ViVe.Core`) | Query, enable, disable, reset, variants, priorities, sequential queue, snapshots, post-change verification |
| GUI: Dashboard | Windows version/build/architecture, feature-management status, change count, recent changes, warnings |
| GUI: Feature Explorer | Manual ID input (batch), search by name or ID, query/enable/disable/reset, advanced priority/store/variant options |
| GUI: Change History | Timestamped audit log with previous state, JSON export, rollback when the previous state was captured reliably |
| GUI: Settings | System/light/dark theme, warnings, logging, default stores, local catalog file, settings export/import |
| Scan this build | Reads feature names and IDs from this PC's own binaries using Microsoft's public symbols (hash/GUID-checked, data only, cached), so names match the installed build. Dashboard lists feature IDs new since the last Windows update. |
| Feature names from mach2 | Optional import of riverar/mach2 feature dumps (builds 17643 to 27764, hash-pinned, data only) with per-build presence and staging; read-only cross-check against this PC's feature store. See [Feature catalog](docs/FEATURE-CATALOG.md#mach2-import). |
| Feature Collections, curated categories/descriptions, remote catalog updates | **Not implemented** (Phase 3). No feature metadata has been verified yet, so none is shipped. |

**Validation so far:** everything compiles (including WPF) and 132 unit tests pass on Linux; Windows-only tests,
the CLI smoke test and packaging run in GitHub Actions on `windows-latest`. **The GUI has not yet been run
interactively on a real Windows machine** — treat it as untested until it has.

## Download and run

Builds are produced by GitHub Actions (`Build` workflow artifacts, and GitHub Releases for `v*` tags). They are
**not code-signed**; verify them against `SHA256SUMS.txt`.

| Package | Contents |
|---|---|
| `ViVeToolStudio-<ver>-win-x64-self-contained.zip` / `win-arm64` | GUI, no runtime install needed |
| `ViVeToolStudio-<ver>-win-x64-framework-dependent.zip` / `win-arm64` | GUI, needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `ViVeTool-<ver>-net481-anycpu.zip` | The CLI (.NET Framework 4.8.1, built into Windows 11; works on x64 and ARM64) |

Requirements: Windows 10 build 18963 or newer (Windows 11 recommended).

### Administrator rights

ViVeTool Studio starts **without** elevation, in read-only mode: you can query features and browse history.
Enabling, disabling, resetting or rolling back requires administrator rights; the app offers to restart itself
elevated (UAC prompt) when you try. Per-operation elevation would need a separate elevated helper process, which
is planned but not in the MVP (see the roadmap). The CLI must be run from an elevated terminal for changes, as before.

## Using the GUI

1. **Feature Explorer** → type one or more IDs (`12345678, 0x10`) or search by name and add results.
2. **Query** shows the effective state and every override per priority, for the runtime store and the persisted
   boot store.
3. **Enable / Disable / Reset** show a preview (operation, current state, catalog status, warnings) and require
   confirmation. Non-default priorities, the experiment flag and multi-priority resets are flagged as higher risk.
4. Each result says whether the change was **verified** by re-reading the store, only **reported** by Windows, or
   **failed**. A zero return code alone is never treated as verified success.
5. **Collections** groups named features by words in their internal names (Taskbar, Start menu, File Explorer, ...)
   so you can tick the ones you want and send them to Feature Explorer. The grouping is inferred, not a description,
   and nothing changes until you confirm in Feature Explorer.
6. **Change History** lists every attempt. **Roll back** is offered only for the most recent successful change to a
   feature when its previous state was fully captured and Windows has not been updated since.

## Using the CLI

`ViVeTool.exe` behaves like ViVeTool 0.3.4 (`/query`, `/enable`, `/disable`, `/reset`, `/fullreset`, `/changestamp`,
`/querysubs`, `/addsub`, `/delsub`, `/notifyusage`, `/lkgstatus`, `/export`, `/import`, `/fixlkg`, `/fixpriority`,
`/appupdate`, `/dictupdate`). Run `ViVeTool /?` for help. Differences (exit codes, crash fixes) are listed in
[docs/CLI.md](docs/CLI.md).

## Building from source

Requires the .NET 10 SDK. The .NET Framework 4.8.1 reference assemblies are restored from NuGet automatically.

```powershell
dotnet build ViVe.sln -c Release
dotnet test ViVe.sln -c Release
./build/package.ps1            # portable x64/ARM64 zips + SHA256SUMS.txt in ./artifacts
```

On Linux/macOS the whole solution also compiles (`EnableWindowsTargeting` is set in `Directory.Build.props`) and
the platform-independent tests run, but nothing Windows-specific is exercised: compiling is not runtime validation.

Tests never modify real feature configuration: the engine is tested against an in-memory fake store, and the
Windows-only tests (`NativeReadOnlyTests`) and `build/cli-smoke-test.ps1` only perform queries.

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — projects, engine design, safety model, upstream analysis and Windows 11 notes
- [CLI compatibility](docs/CLI.md) — preserved commands, exit codes, behaviour changes
- [Feature catalog](docs/FEATURE-CATALOG.md) — schema and sourcing rules ([JSON schema](docs/feature-catalog.schema.json))
- [Roadmap](docs/ROADMAP.md)

## License

GPL-3.0 (see [LICENSE](LICENSE)). ViVe and ViVeTool are Copyright (C) 2019-2025 @thebookisclosed; upstream license
headers are retained in all original files. Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Binary packages include `LICENSE.txt` and `SOURCE.txt`, which
points to the exact corresponding source commit.

---

## Original ViVe README

# ViVe
ViVe is a C# library you can use to make your own programs that interact with the A/B feature experiment mechanism found in Windows 10 & newer.

The *FeatureManager* class should cover most feature management needs with the added benefit of some struct heavy lifting being done for you. Boot persistence and LKG management is offered exclusively by this class as it had to be reimplemented.

In case you'd like to talk to NTDLL exports directly, you can use *NativeMethods*.

# ViVeTool
ViVeTool is both an example of how to use ViVe, as well as a straightforward tool for power users which want to use the new APIs instantly.

[![Release downloads](https://img.shields.io/github/downloads/thebookisclosed/ViVe/total.svg)](https://GitHub.com/thebookisclosed/ViVe/releases/)

# Compatibility
In order to use ViVe, you must be running Windows 10 build 18963 or newer.

![ViVeTool Helpfile](https://i.imgur.com/PzCHEUQ.png)
