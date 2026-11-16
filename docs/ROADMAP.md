# Roadmap

Status legend: ✅ done in this branch · 🟡 partial · ⬜ not started

## Phase 1 — Repository and engine ✅

- ✅ Fork with full upstream history; development branch
- ✅ SDK-style projects (net481 + net10.0) without rewriting upstream sources
- ✅ `ViVe.Core` shared engine (`IFeatureConfigurationApi`, `IFeatureManager`), structured results, verification
- ✅ CLI on the shared engine, exit codes, crash fixes, argument regression tests
- ✅ Unit tests with an in-memory store; Windows-only read-only native tests

## Phase 2 — Functional GUI MVP 🟡 (implemented, awaiting Windows runtime validation)

- ✅ Dashboard, manual feature-ID input, query/enable/disable/reset through the real engine
- ✅ Clear success / unverified / failure feedback; previews and confirmations
- ✅ Administrator handling (read-only unelevated, relaunch elevated on demand)
- ✅ Local change history with snapshots, JSON export, reliable-only rollback
- ✅ System/light/dark themes (WPF Fluent)
- ⬜ Manual test pass on Windows 11 x64 and ARM64 (the GUI has only been compiled, not run)

## Phase 3 — Advanced feature browser

- 🟡 Searchable catalog (name/ID search over the upstream dictionary and the optional mach2 import, with aliases; no categories/descriptions)
- ✅ mach2 import: verified names/IDs and build presence for builds 17643 to 27764 (mach2 stopped at 2024-12-14), plus a cross-check against this PC
- ⬜ Names for builds newer than 27764: needs a new symbol-based source (mach2's successor, or a local PDB scan)
- ⬜ Curated, source-attributed catalog entries (categories, descriptions, verified builds) — needs research with citations
- ⬜ Feature Collections (File Explorer, Start, Taskbar, Context menus, Settings, Window management, Other) — only from verified entries
- 🟡 Batch queue (sequential multi-ID operations exist; no editable queue UI yet)
- ⬜ Build-specific compatibility warnings driven by verified catalog data (mechanism exists, data does not)
- ⬜ Feature details page (all priorities, subscriptions, history for the feature)
- 🟡 Change-history browsing (list + details; no filtering/search yet)
- ⬜ Remote catalog updates (HTTPS, hash-pinned or signed JSON, validated, never executed)
- ⬜ GUI for import/export of feature configurations (`/export`, `/import` equivalents), subscriptions, LKG tools

## Phase 4 — Production readiness

- ✅ CI on Windows (build, tests, CLI smoke test) and Linux (compile + unit tests)
- ✅ Portable x64/ARM64 packages, SHA-256 checksums, tag-driven GitHub releases (unsigned, marked pre-release)
- ⬜ Per-operation elevation via a minimal elevated helper (so the GUI never runs fully elevated)
- ⬜ Installer (MSIX or WiX) and code signing (requires credentials)
- 🟡 Accessibility and keyboard navigation (access keys, automation names, live status region; needs a screen-reader pass)
- 🟡 Diagnostics (optional file log; add crash reports and a "copy diagnostics" action)
- ⬜ UI automation tests on a Windows runner (e.g. FlaUI) — must never perform real modifications
- ⬜ Localization
- ⬜ Upstream report for the `FixLKGStore` registry path issue
