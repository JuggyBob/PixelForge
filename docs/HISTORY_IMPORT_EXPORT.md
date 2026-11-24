# History export and import

PixelForge history is stored in the local SQLite database. The History page can
export a versioned JSON document and import it on the same or another machine.

## Export

The export contains operation metadata, filenames, paths, sizes, status,
timestamps, settings JSON, verification summary, error summary, model/provider
information, and warnings. It does **not** contain source media, converted
outputs, credentials, or model weights.

Exports may contain local filenames and paths. Review an export before sharing
it.

## Import

Import validates the envelope schema version, record count, required fields,
enumerations, dates, path lengths, and diagnostic text sizes before writing.

- Records with an unknown newer schema version are rejected.
- Duplicate `(JobId, ItemId)` records are skipped.
- Invalid records are counted and do not abort the valid records.
- The entire import runs in a SQLite transaction.
- Imported paths are preserved even if the files no longer exist; Open/Reveal
  actions report missing files instead of recreating them.
- Existing history is never overwritten by an import.

## Schema

The export envelope is:

```json
{
  "schemaVersion": 1,
  "appVersion": "2.0.0",
  "exportedAt": "2026-11-22T00:00:00+01:00",
  "entries": []
}
```

Future schema versions must be handled explicitly. The current importer accepts
version 1 only.
