# Questions for the data engineers — Zenya CSV export

The CSV extraction pipeline (`CsvExtractor.cs`, see `docs/extraction-pipeline.md`)
validates a fixed list of "required" headers up front and rejects the whole file if
any are missing. That list was inferred from the sample exports we have, not from any
documented contract with the Zenya export process — so we don't actually know which
columns are guaranteed to always be present, or in what format. This doc collects the
open questions and, until we get answers, the assumptions the pipeline currently makes.

## Questions

1. **Which columns are truly guaranteed present?** — the required-header list drives a
   hard file-level rejection if any are missing.
2. **Is the delimiter always a comma?** — Dutch/EU locale exports often use semicolons;
   a semicolon file currently misparses into one garbage column (fails loud, but on the
   wrong-looking error).
3. **Is the export always UTF-8?** — any other detected encoding is currently
   hard-rejected outright.
4. **Is header casing stable?** — determines if case-insensitive matching is necessary
   or just defensive.
5. **Does the export ever use `NULL`/`N/A`/`-` for "no value"?** — these would pass
   through as literal text in string fields instead of being treated as empty.
6. **What format does `ACTIVE` use (bool / Y-N / ja-nee)?** — an unparseable value
   currently drops the entire index row and every one of its pages.
7. **Can `ACTIVE` be absent vs. present-but-blank?** — both currently default to
   "active"; a wrong assumption means withdrawn documents stay searchable.
8. **Can `DOCUMENT_ID` legitimately have leading/trailing whitespace?** — confirms
   unconditional trim is safe, not masking an export bug.
9. **Is `DOCUMENT_ID` casing consistent between the two files?** — confirms
   case-insensitive join is actually needed, not guesswork.
10. **Is `PAGE_INDEX` guaranteed unique and contiguous per document?** — retrieval
    relies on it for page order; gaps would scramble reconstructed documents.
11. **If `(DOCUMENT_ID, PAGE_INDEX)` repeats within a file, does the first or last row
    win?** — we currently keep the first; if duplicates are corrections, we'd index the
    stale version.
12. **Are `LAST_MODIFIED_DATETIME` / `CHECK_DATE` formats and timezone stable?** — a
    format drift fails every date field at once, silently, until noticed.
13. **Is `VERSION` always numeric, and can `REVISION` exist without it?** — we silently
    fall back to bare `VERSION` on any parse issue; worth confirming that's correct, not
    just safe.
14. **What is the full set of possible `ATTENTION_REQUIRED_FLAGS` values?** — only
    `check_date_exceeded` is currently acted on; other flags may be silently ignored.
15. **Is each export a full snapshot or an incremental delta?** — downstream
    reconciliation treats anything missing from a run as deleted; a partial export would
    wrongly remove documents from the index.

## Working assumption until we hear back

**A document whose `ACTIVE` value parses as `false` is treated as withdrawn — its
pages are excluded from the search index** (`CsvJoiner.Join`, the `!indexRecord.Active`
check skips every page for that `DOCUMENT_ID`, logged as a data-quality warning, not an
error). This is the existing, implemented behavior; we're recording it here as an
explicit assumption pending confirmation that "inactive in the index" really does mean
"withdrawn protocol — shouldn't be searchable," rather than something else (e.g. a
draft not yet published).

Two related outcomes, in case they matter for the answer to Q6/Q7:

- **`ACTIVE` missing or blank** → treated as active (document is kept, indexed
  normally). This is the "column is optional" default.
- **`ACTIVE` present but unparseable** (anything that isn't `"true"`/`"false"`) → the
  *entire index row is rejected* as a parse error, not defaulted either way. That
  document gets no `IndexRecord` at all, which in turn makes every one of its pages
  fail to join ("no index record found") and drop out of the run entirely. This is a
  deliberate "fail loud" choice for now (see `ParseActive` in `CsvExtractor.cs`) rather
  than guessing active/inactive for a value we don't recognize — but if the export
  turns out to regularly use a format we don't parse (Q6), this would need to change
  to a whitelist instead of a hard failure.
