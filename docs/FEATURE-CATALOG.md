# Feature catalog

The catalog holds **metadata** about feature IDs (name, description, category, build compatibility, source). It is
kept separate from feature **state**, which always comes from Windows through the engine.

## Sources, in increasing order of trust

1. `FeatureDictionary.pfs` from upstream ViVe — `Name,Id` pairs. Loaded as `verificationStatus: nameOnly` with the
   source recorded. A name says nothing about what a feature does or whether changing it is safe.
2. **mach2 import** (optional, `%LOCALAPPDATA%\ViVeToolStudio\catalog\mach2-import.json`) — generated on your PC
   from [mach2](https://github.com/riverar/mach2) feature dumps. See [mach2 import](#mach2-import) below.
3. `catalog/feature-catalog.json` — the curated catalog shipped with the app. **It is intentionally empty in the
   MVP**: no descriptions, categories or compatibility claims have been verified yet, and none are invented.
4. An optional local JSON file chosen in Settings.

Later sources override earlier ones per ID. A later entry without a name keeps the earlier name, and a different earlier
name is kept as a searchable alias (`aliases`), so a rename never hides a known name.

## mach2 import

mach2 by Rafael Rivera (riverar), GPL-3.0, scanned Windows debug symbols (PDBs) for feature names and IDs and published
one dump per build under `features/<branch>/<arch>/<build>.txt`. **Upstream stopped publishing dumps on 2024-12-14**;
the pinned commit `d6bcdce` holds 295 dumps for builds 17643 to 27764. Nothing newer will appear there.

Settings → *Feature names from mach2* imports them:

- **Download and import** fetches each dump from `raw.githubusercontent.com/riverar/mach2/<pinned commit>/...` (about
  46 MB). Every file must match the SHA-256 recorded in [`catalog/mach2-manifest.txt`](../catalog/mach2-manifest.txt)
  (embedded in the app); mismatches are skipped and reported.
- **Import from a local mach2 clone** reads the same files from disk. Files in the manifest are hash-checked; other
  dump-shaped files are imported with a warning that they are not covered by the pinned manifest.
- Dumps are parsed as text (`Name: Id` lines and `## <staging>:` headers). Nothing is executed. Diff/patch files and
  anything not shaped like a dump path are ignored. Entries with ID 0 or a numeric placeholder name are skipped.

What an imported entry contains, and nothing more:

| Field | Value |
|---|---|
| `name` | the name in the newest dump that lists the ID; older names become `aliases` |
| `verificationStatus` | `verified` (name and ID come from symbols for the listed builds) |
| `builds` | runs of consecutive dumped builds the ID appeared in. A run never spans a dump the ID was missing from. Builds between two dumps were not checked, and the note says so. The note also records mach2's staging section (`Always Enabled`, `Enabled By Default`, `Disabled By Default`, `Always Disabled`, `Unknown`) in the newest dump of the run. |
| `source` | mach2 citation with the pinned commit; each build run cites the dump file |
| `description`, `category` | empty: mach2 does not provide them |

For a build newer than 27764 the Feature Explorer shows "not listed for build N by its source (most recently listed for
builds A to B)". That is accurate: mach2 never dumped those builds.

### Cross-check with this PC

Settings → *Cross-check* lists the feature IDs that have a configuration in this PC's runtime store (what
`ViVeTool /query` lists) and reports how many have a known name, how many fall in a build range a verified source lists
for this build, and which IDs have no name. It only reads.

## Rules for curated entries

- Every entry above `nameOnly` must have a `source` (URL or citation). Entries without one are rejected on load.
- `verified` entries should list the builds the source confirmed in `builds`; compatibility is otherwise shown as
  unknown. Presence on a build never implies safety.
- Names must match `^[A-Za-z0-9_.-]{1,256}$`; ID 0 and duplicate IDs are rejected.
- Catalogs are data only. Nothing in a catalog is executed, and catalog updates must never download code.

## Schema

See [feature-catalog.schema.json](feature-catalog.schema.json). Example (illustrative values, not a real feature):

```json
{
  "schemaVersion": 1,
  "catalogVersion": "0.1.0",
  "features": [
    {
      "id": 12345678,
      "name": "ExampleFeature",
      "description": "Example description",
      "category": "Explorer",
      "builds": [ { "minBuild": 26100, "maxBuild": 26100, "source": "https://example.invalid/post" } ],
      "source": "https://example.invalid/post",
      "verificationStatus": "communityReported"
    }
  ]
}
```

## Remote updates (not implemented)

Planned design: download a signed or hash-pinned JSON document over HTTPS, validate it with the same loader, keep the
previous catalog on failure, and never execute content. See the roadmap.
