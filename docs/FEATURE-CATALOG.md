# Feature catalog

The catalog holds **metadata** about feature IDs (name, description, category, build compatibility, source). It is
kept separate from feature **state**, which always comes from Windows through the engine.

## Sources, in increasing order of trust

1. `FeatureDictionary.pfs` from upstream ViVe — `Name,Id` pairs. Loaded as `verificationStatus: nameOnly` with the
   source recorded. A name says nothing about what a feature does or whether changing it is safe.
2. `catalog/feature-catalog.json` — the curated catalog shipped with the app. **It is intentionally empty in the
   MVP**: no descriptions, categories or compatibility claims have been verified yet, and none are invented.
3. An optional local JSON file chosen in Settings.

Later sources override earlier ones per ID (keeping the dictionary name when the curated entry has none).

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
