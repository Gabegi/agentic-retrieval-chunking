# Questions for the data engineers — Zenya CSV export

The CSV extraction pipeline (`CsvExtractor.cs`, see `docs/extraction-pipeline.md`)
validates a fixed list of "required" headers up front and rejects the whole file if
any are missing. That list was inferred from the sample exports we have, not from any
documented contract with the Zenya export process — so we don't actually know which
columns are guaranteed to always be present, or in what format. This doc collects the
open questions and, until we get answers, the assumptions the pipeline currently makes.

## Questions

1. **Which columns are actually guaranteed to be present in every export?**
   We currently require (and reject the whole file if any is missing):
   - `zenya_pages.csv`: `DOCUMENT_ID`, `TITLE`, `QUICK_CODE`, `FOLDER_MINI_FULL_PATH`,
     `LAST_MODIFIED_DATETIME`, `PAGE_INDEX`, `PAGE_CONTENT`, `RELATIVE_PATH`
   - `zenya_index.csv`: `DOCUMENT_ID`, `DOCUMENT_TYPE_NAME`, `SUMMARY`, `VERSION`,
     `CHECK_DATE`, `ATTENTION_REQUIRED_FLAGS`

   Is this list correct? Are any of these actually optional in some export runs, and
   are there other columns (e.g. `ACTIVE`, `LANGUAGE`) that are always present that we
   could safely add to the required list?

2. **What format does `ACTIVE` use?** We currently only accept values `bool.TryParse`
   understands (case-insensitive `"True"`/`"False"`, per the .NET docs — notably
   *not* `"0"`/`"1"`). We don't know whether the real export ever emits numeric
   booleans, `"Y"`/`"N"`, or Dutch `"ja"`/`"nee"`/`"waar"`/`"onwaar"`. This matters
   because a value we can't parse currently causes that entire index row to be
   rejected (see "Working assumption" below) — if the real export regularly uses one
   of these other formats, we'd be silently losing documents rather than harmlessly
   defaulting them.

3. **Is `ACTIVE` ever entirely absent from a row/file** (no column at all, not just an
   empty value), **and is that expected?** We treat an absent or blank `ACTIVE` as
   "assume active," on the assumption this column is sometimes omitted rather than
   always present-but-empty. Is that assumption correct?

4. **Is header casing stable?** We've had to defensively normalize header matching to
   be case-insensitive (`DOCUMENT_ID` vs `Document_Id` vs `document_id`) because we
   don't know whether casing is guaranteed consistent across export runs. Can you
   confirm it's always exactly as documented (all caps, underscores), or should we
   keep treating it as unreliable?

5. **Can `DOCUMENT_ID` legitimately contain leading/trailing whitespace**, or is that
   always an export artifact/bug on Zenya's side? We currently trim it unconditionally
   before using it as the join key between the two files.

6. **Is `DOCUMENT_ID` casing guaranteed consistent between the two files** (e.g. always
   the same case as it appears in both `zenya_pages.csv` and `zenya_index.csv`), or
   could the same document show up as `ABC-123` in one file and `abc-123` in the other?
   We now match them case-insensitively in `CsvJoiner` as a precaution, but don't have
   real data confirming whether that's ever actually needed.

7. **What is the full set of possible values in `ATTENTION_REQUIRED_FLAGS`?** We parse
   it as an arbitrary JSON string array (`DataCleaner.ParseAttentionFlags`) and only
   `PipelineValidator` acts on it — and only on one specific value, `check_date_exceeded`
   (surfaced as a validation red flag, not a hard failure). We don't have a documented
   list of every flag Zenya can emit here, or a defined behavior for values we don't
   recognize. Are there other flags we should also be surfacing or reacting to?

8. **Is the export always encoded as UTF-8?** CSV has no way to declare its own
   encoding, so this can only be confirmed from Zenya's side, not inferred from a file.
   We currently detect the encoding from the file's byte-order-mark if present (UTF-8,
   UTF-16LE, UTF-16BE all recognized), falling back to UTF-8 if there's no BOM, and
   **reject the whole file outright if the detected encoding isn't UTF-8**
   (`CsvExtractor.EnsureHeadersAreCorrect`). Is UTF-8 (with or without BOM) genuinely
   the only encoding this export ever uses, or could a different export path/locale
   produce something else (e.g. Windows-1252)? If the latter, this hard rejection would
   need to become a whitelist of accepted encodings instead of a single one.

## Working assumption until we hear back

**A document whose `ACTIVE` value parses as `false` is treated as withdrawn — its
pages are excluded from the search index** (`CsvJoiner.Join`, the `!indexRecord.Active`
check skips every page for that `DOCUMENT_ID`, logged as a data-quality warning, not an
error). This is the existing, implemented behavior; we're recording it here as an
explicit assumption pending confirmation that "inactive in the index" really does mean
"withdrawn protocol — shouldn't be searchable," rather than something else (e.g. a
draft not yet published).

Two related outcomes, in case they matter for the answer to Q2/Q3:

- **`ACTIVE` missing or blank** → treated as active (document is kept, indexed
  normally). This is the "column is optional" default.
- **`ACTIVE` present but unparseable** (anything that isn't `"true"`/`"false"`) → the
  *entire index row is rejected* as a parse error, not defaulted either way. That
  document gets no `IndexRecord` at all, which in turn makes every one of its pages
  fail to join ("no index record found") and drop out of the run entirely. This is a
  deliberate "fail loud" choice for now (see `ParseActive` in `CsvExtractor.cs`) rather
  than guessing active/inactive for a value we don't recognize — but if the export
  turns out to regularly use a format we don't parse (Q2), this would need to change
  to a whitelist instead of a hard failure.
