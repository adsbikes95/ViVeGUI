# ViVeTool CLI compatibility

The `ViVeTool` command-line tool from upstream v0.3.4 is preserved. Its branding, help text, argument syntax and
console output are unchanged. It now uses the shared `ViVe.Core` engine for building updates, writing to the stores
and Last Known Good handling, so the CLI and ViVeTool Studio cannot drift apart.

## Supported commands (unchanged)

`/query`, `/enable`, `/disable`, `/reset`, `/fullreset`, `/changestamp`, `/querysubs`, `/addsub`, `/delsub`,
`/notifyusage`, `/lkgstatus`, `/export`, `/import`, `/fixlkg`, `/fixpriority`, `/appupdate`, `/dictupdate`,
`/?` / `/help`, plus `/setlkg` in Debug builds, and the migration tips for the pre-0.3 command names.

Arguments (`/id:`, `/name:`, `/store:`, `/priority:`, `/variant:`, `/variantpayloadkind:`, `/variantpayload:`,
`/experiment`, `/reportingkind:`, `/reportingoptions:`, `/reportingtarget:`, `/filename:`, `/replace`) are parsed by
the original `ArgumentBlock` code. Regression tests in `tests/ViVe.Core.Tests/CliArgumentCompatibilityTests.cs` pin
the behaviour, including quirks that scripts may rely on:

- only a lowercase `0x` prefix is treated as hexadecimal in `/id:`;
- unparseable IDs in a list are reported and skipped, not fatal;
- unknown parameters are warnings, not errors;
- `/store:both` is accepted by `/enable`, `/disable`, `/reset`, `/fullreset`, `/export`, `/import` but not `/query`.

## Changes

| Change | Impact |
|---|---|
| **Exit codes.** Upstream always exited with 0. Now: `0` success, `1` a Windows API call failed, `2` invalid/missing arguments or unknown command (including migration tips), `3` Windows build older than 18963, `4` `/query /id:` found no configuration for a requested ID. | Scripts that ignore the exit code are unaffected. Scripts that treated any run as success may now see non-zero codes for runs that already printed errors. |
| `/reset`, `/fullreset`, `/fixpriority` no longer crash (`NullReferenceException`) if a store query fails; they print "Failed to query feature configurations". | Bug fix. |
| SDK-style project, multi-targeted `net481` (the distributed build, same runtime requirements as upstream) and `net10.0` (used for cross-platform tests). | No change for users of the net481 build. |
| `packages.config` replaced by `PackageReference` (same Newtonsoft.Json 13.0.3). | Build-only. |

There are no breaking changes to argument syntax or output text.

## Known upstream behaviour kept as-is

- `/fixlkg` opens `HKLM\CurrentControlSet\...` (missing `SYSTEM\`) and therefore always reports that no fix is
  needed. This is upstream library behaviour; it is documented in `ARCHITECTURE.md` rather than changed silently.
- `/appupdate` and `/dictupdate` check the upstream repository (thebookisclosed/ViVe).
