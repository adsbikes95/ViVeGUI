# ViVeTool Studio architecture

## 1. Upstream analysis (thebookisclosed/ViVe v0.3.4)

The fork starts from upstream `master` at `3f8c6a3` ("Respect new 24H2 immutable property, update dictionary"),
which is the v0.3.4 release. The full upstream history is kept; an `upstream` remote can be added for syncing
(push to it is never configured).

### Projects (as found)

| Project | Type | Target | Notes |
|---|---|---|---|
| `ViVe` (`Albacore.ViVe.dll`) | Library | .NET Framework 4.8.1, old-style csproj | P/Invoke wrappers + `FeatureManager` |
| `ViVeTool` | Console app | .NET Framework 4.8.1, AnyCPU + ARM64 configs | Uses Newtonsoft.Json (packages.config) for update checks |
| `Extra/FeatureDictionary.pfs` | Data | — | ~17,000 `Name,Id` lines; used by `/name:` and to label query output |

### Public interface of the ViVe library

- `Albacore.ViVe.FeatureManager` (static): `QueryAllFeatureConfigurations`, `QueryFeatureConfiguration`,
  `QueryFeatureConfigurationChangeStamp`, `SetFeatureConfigurations` (runtime via ntdll, boot via registry),
  change-notification registration, usage subscriptions (query/add/remove, runtime + registry),
  `NotifyFeatureUsage`, boot status (`Get/SetBootFeatureConfigurationState`, `InitializeBootStatusDataFile`),
  `FixLKGStore`, and `ImmutablePriorities` (ImageDefault, EKB, ImageDefaultEditionOverride, Security, ImageOverride).
- `Albacore.ViVe.NativeMethods.Ntdll`: raw `Rtl*FeatureConfiguration*`, `Rtl*FeatureUsage*`, `RtlGet/SetSystemBootStatus`,
  `RtlCreateBootStatusDataFile` imports.
- `NativeStructs`: `RTL_FEATURE_CONFIGURATION` (bit-packed `CompactState`: priority, state, experiment, subscriptions,
  variant, payload kind), `RTL_FEATURE_CONFIGURATION_UPDATE`, usage subscription/report structs.
- `NativeEnums`: store type (Boot/Runtime), enabled state, priorities, payload kind, operation flags, LKG states.
- `ObfuscationHelpers`: feature-ID obfuscation used for registry key names.

### Windows integration

- **Runtime store**: `ntdll!RtlSetFeatureConfigurations` / `RtlQuery*`. Changes apply to the running session.
- **Boot store**: ViVe re-implements persistence by writing
  `HKLM\SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\<priority>\<obfuscated id>` (or the
  `Policies\Microsoft\FeatureManagement\Overrides` value for `UserPolicy`), then marks the Last Known Good boot
  status `BootPending`.
- Writing requires administrator rights. Querying does not.

### Compatibility findings on current Windows 11 builds

| Finding | Handling |
|---|---|
| 24H2 introduced the immutable `ImageOverride` priority | Already handled upstream (`ImmutablePriorities`); Studio never writes immutable priorities. |
| A Boot-store *query* reflects what was loaded at the last boot, not pending registry writes | Studio reads the persisted registry overrides directly for snapshots, verification and display. |
| `RtlQueryFeatureConfiguration` returns an error status when a feature simply has no configuration | Engine distinguishes "no configuration" from query failure using `QueryAll`. CLI reports exit code 4 (see CLI.md). |
| `/reset`, `/fullreset`, `/fixpriority` crashed with `NullReferenceException` when a store query failed | Fixed: an error is printed and a non-zero exit code returned. |
| `FeatureManager.FixLKGStore` opens `CurrentControlSet\...` under HKLM without the `SYSTEM\` prefix, so `/fixlkg` always reports that no fix was needed | **Not changed** (upstream library code, registry repair); documented for an upstream report. |
| `UpdateCheck` uses `WebClient`/`SHA1CryptoServiceProvider` and points at the upstream repository | Kept for CLI compatibility (`/appupdate`, `/dictupdate` check upstream). Obsolete APIs still work on net481/net10. |
| `Environment.OSVersion` reports the real build only with a Windows 10 `supportedOS` manifest entry | Both executables carry that manifest. |
| ARM64 | The IL is AnyCPU; Studio is published for `win-x64` and `win-arm64`. Not yet tested on ARM64 hardware. |

### Licensing

Upstream is GPL-3.0 with per-file headers. All headers are retained. New files carry `SPDX-License-Identifier:
GPL-3.0-or-later` (the upstream headers say "version 3 ... or any later version"). Packages include the license,
third-party notices and a pointer to the exact source commit.

## 2. Solution layout

The proposed `src/` layout was adapted: upstream `ViVe/` and `ViVeTool/` stay where they are so that future upstream
changes merge cleanly; new projects live under `src/` and `tests/`.

```text
ViVe/                      upstream library (SDK-style csproj, net481 + net10.0; sources unchanged)
ViVeTool/                  upstream CLI (SDK-style, net481 + net10.0; now uses ViVe.Core)
src/ViVe.Core/             shared engine (net481 + net10.0)
src/ViVe.FeatureCatalog/   feature metadata: schema, loaders, search (net10.0)
src/ViVe.Studio.Core/      UI-independent app logic: view models, history, rollback, settings (net10.0)
src/ViVe.Studio/           WPF application (net10.0-windows)
tests/ViVe.Core.Tests/     engine, builders, CLI argument regression, Windows-only read-only native tests
tests/ViVe.Studio.Tests/   catalog, history, rollback, settings, view models
catalog/feature-catalog.json  curated catalog (empty until entries are verified)
build/                     packaging and CLI smoke-test scripts
.github/workflows/build.yml   Windows + Linux CI, packaging, releases
```

`ViVe.Shared` from the original proposal was not needed: shared types live in `ViVe.Core`.

## 3. Engine (`ViVe.Core`)

```
 ViVeTool (CLI) ──► FeatureUpdateBuilder / FeatureStoreWriter ──┐
                                                                ├─► IFeatureConfigurationApi ──► Albacore.ViVe.FeatureManager ──► ntdll / registry
 ViVeTool Studio ─► FeatureChangeService ─► IFeatureManager ────┘        (NativeFeatureConfigurationApi)
                                             (FeatureEngine)
```

- **`IFeatureConfigurationApi`** — thin, mockable wrapper over the ViVe library; returns raw NTSTATUS. The production
  implementation is `NativeFeatureConfigurationApi`; tests use an in-memory fake.
- **`FeatureUpdateBuilder`** — update construction extracted verbatim from ViVeTool (`/enable`, `/disable`, `/reset`,
  `/import`, `/fixpriority`), so both front ends build identical updates.
- **`FeatureStoreWriter`** — ViVeTool's `FinalizeSet`/`UpdateLKGStatus` logic: runtime first, then boot, stop at the
  first failure, then set LKG `BootPending`; returns a structured `StoreWriteResult` instead of printing.
- **`IFeatureManager` / `FeatureEngine`** — the GUI-facing engine:
  - `Plan(request)` validates (ID ≠ 0, writable priority, variant ≤ 63, UserPolicy restrictions, snapshot
    completeness for restores, platform support) and classifies risk without touching anything.
  - `ExecuteAsync(request)` captures a `FeatureSnapshot` (runtime entries + persisted boot entries at writable
    priorities), builds per-store updates, writes, then **re-reads both stores and compares**. Results are
    `Succeeded` (verified), `SucceededUnverified`, `NoChange`, `Rejected` or `Failed`, with NTSTATUS, failed store,
    warnings (including partial runtime-only changes and LKG problems) and before/after state.
  - `ExecuteBatchAsync` is a sequential queue; cancellation stops before the next item, never mid-item.
  - Native calls are serialized with a semaphore and run off the UI thread.
- **`FeatureIdParser`** — strict GUI input validation (decimal or `0x` hex, 1…2³²−1, max 64 IDs). The CLI keeps its
  original parser for compatibility.

## 4. Application (`ViVe.Studio.Core` + `ViVe.Studio`)

- MVVM with CommunityToolkit.Mvvm; DI with Microsoft.Extensions.DependencyInjection (composition root in `App.xaml.cs`).
- View models are WPF-free and unit tested; WPF-specific services (dialogs, elevation, theme, system info) are behind
  interfaces in `Services/Abstractions.cs`.
- `FeatureChangeService` is the only path that modifies state: it runs the engine and appends a
  `ChangeHistoryEntry` for every attempt (including rejected ones).
- `JsonChangeHistoryStore` (`%LOCALAPPDATA%\ViVeToolStudio\history.json`): atomic writes; a corrupt file is moved
  aside, never overwritten; export produces a versioned JSON document.
- `RollbackPolicy`: rollback only for a change that altered state, whose snapshot is complete for the stores it
  touched, that has not been rolled back, that is the latest change to that feature, and that was made on the current
  Windows build. Rollback re-creates the captured entries (or resets priorities that had none).
- UI: WPF's built-in Fluent theme (`Application.ThemeMode`; system/light/dark, Mica on Windows 11). No third-party UI
  library is used, to keep dependencies minimal; WPF UI can be adopted later if a `NavigationView` etc. is needed.

## 5. Safety model

1. Previous state is queried and stored before every modification.
2. Every modification shows a preview (operation, current state, catalog status, warnings) and needs confirmation.
3. Compatibility is shown as unknown unless a verified, sourced catalog entry lists the build (none do yet).
4. Higher-risk changes (non-User priority, experiment flag, multi-priority reset) get a warning-styled confirmation.
5. Every attempt is recorded in the history; export to JSON.
6. Rollback only when the previous state can be restored reliably (rules above).
7. Snapshots are described as feature-state snapshots, never as backups.
8. Nothing is modified automatically; every change starts from an explicit user action.
9. No Windows security protections or policies are touched; immutable/OS-owned priorities are rejected.
10. No shell commands are executed; feature IDs are strictly validated; catalogs are data-only JSON.

Elevation: the app runs `asInvoker` and relaunches itself with `runas` only when the user asks to make a change.
