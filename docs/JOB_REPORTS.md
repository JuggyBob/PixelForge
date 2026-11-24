# Job reports

Every queue job has a viewable, copyable, and savable diagnostic report. Batch jobs
include each item's report separately, so one failed file does not hide successful files.

Reports include:

- Job/item identity, operation, title, state, timestamps, duration, and attempt.
- Input and output paths and sizes.
- Requested and actual provider, fallback reason, runtime, and model.
- Verification summary and warnings.
- Error category, summary, details, and relevant engine log.
- Per-item status for batch jobs.

Reports are generated locally and written to the clipboard only when the user
selects **Copy diagnostic report**. **View** opens the full report in a read-only
dialog, and **Save** writes it to a text file chosen by the user. They are not
uploaded automatically.
Exported history contains the same persisted metadata, subject to privacy
warnings in the UI.

Credentials, environment blocks, media contents, and unrelated process output
are never included.
