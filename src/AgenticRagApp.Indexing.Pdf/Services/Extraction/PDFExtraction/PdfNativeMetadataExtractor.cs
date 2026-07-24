using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace AgenticRagApp.Indexing.Pdf.Services
{
    // The PDF's own Info-dictionary + bookmark tree + AcroForm fields + XMP packet,
    // read via PdfPig.
    internal static class PdfNativeMetadataExtractor
    {
        private static readonly XNamespace RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        private static readonly XNamespace DcNs  = "http://purl.org/dc/elements/1.1/";
        private static readonly XNamespace PdfNs = "http://ns.adobe.com/pdf/1.3/";
        private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";

        // Reads pdf native Title/Author/CreationDate/ModDate/Producer/Creator/Subject/
        // Keywords, the bookmark tree, AcroForm fields, IsEncrypted, and the XMP metadata
        // packet off an open pdf.
        // - Takes ownership of pdf's lifetime (disposes it here, not in the caller).
        // - Called once, by DocumentIntelligenceExtractor, after preflight opens pdf.
        // diagnostics is report/diagnostic material only (see PdfStepDiagnostics) - never
        // fails the file, since native metadata is a nice-to-have, not required for DI to
        // process the document.
        public static DocMetadata ExtractPdfNativeMetadata(
            PdfDocument pdf, string blobName, ILogger logger, out PdfStepDiagnostics diagnostics)
        {
            using (pdf)
            {
                var warnings = new List<ExtractionWarning>();

                var info       = pdf.Information;
                var bookmarks  = TryGetBookmarks(pdf, blobName, logger, warnings);
                var formFields = TryGetFormFields(pdf, blobName, logger, warnings);
                var xmp        = GetXmpMetadata(pdf, blobName, logger, warnings);

                var title     = string.IsNullOrWhiteSpace(info.Title)     ? null : info.Title;
                var author    = string.IsNullOrWhiteSpace(info.Author)    ? null : info.Author;
                var producer  = string.IsNullOrWhiteSpace(info.Producer)  ? null : info.Producer;
                var creator   = string.IsNullOrWhiteSpace(info.Creator)   ? null : info.Creator;
                var subject   = string.IsNullOrWhiteSpace(info.Subject)   ? null : info.Subject;
                var keywords  = string.IsNullOrWhiteSpace(info.Keywords)  ? null : info.Keywords;

                // Parsing itself is PdfPig's own (GetCreatedDateTimeOffset/GetModifiedDateTimeOffset)
                // rather than hand-rolled - ResolveDate still needs the raw string alongside the
                // parsed value, since the library's Nullable<DateTimeOffset> return can't tell
                // "field absent" apart from "field present but unparseable" on its own, and the
                // warning messages below (and the tests that check them) depend on that distinction.
                var createdAt = ResolveDate(info.CreationDate, info.GetCreatedDateTimeOffset(), blobName, "CreationDate", warnings);
                var modDate   = ResolveDate(info.ModifiedDate, info.GetModifiedDateTimeOffset(), blobName, "ModDate", warnings);

                // Title and Producer get their own message - each explains a real
                // consequence (Title falls back to a filename-derived value; a missing
                // Producer suggests a non-standard export pipeline), not just "it's
                // missing". The rest are plain presence warnings - none of these are
                // blocking, so there's no reason Author got one before and
                // Creator/Subject/Keywords didn't; looped so all four are treated the same.
                if (title is null)
                    Warn(warnings, blobName, "No native Title in the PDF's Info dictionary - falls back to a filename-derived title downstream.");

                if (producer is null)
                    Warn(warnings, blobName, "No native Producer in the PDF's Info dictionary — possible non-standard export pipeline.");

                foreach (var (fieldName, value) in new (string Name, string? Value)[]
                {
                    ("Author", author), ("Creator", creator), ("Subject", subject), ("Keywords", keywords),
                })
                    if (value is null)
                        Warn(warnings, blobName, $"No native {fieldName} in the PDF's Info dictionary.");

                if (bookmarks is { Count: > 0 })
                    Warn(warnings, blobName, $"{bookmarks.Count} bookmark(s) found, max outline depth {bookmarks.Max(b => b.Level) + 1}.");

                if (pdf.IsEncrypted)
                    Warn(warnings, blobName, "PDF carries encryption/permission restrictions (opened successfully - not password-protected).");

                var metadata = new DocMetadata(
                    Title:      title,
                    Author:     author,
                    CreatedAt:  createdAt,
                    ModDate:    modDate,
                    Producer:   producer,
                    Creator:    creator,
                    Subject:    subject,
                    Keywords:   keywords,
                    PageCount:  pdf.NumberOfPages,
                    Bookmarks:  bookmarks,
                    IsEncrypted: pdf.IsEncrypted,
                    FormFields: formFields,
                    Xmp:        xmp);

                logger.LogDebug(
                    "PdfNativeMetadataExtractor: '{Blob}' — {Pages} page(s), title={Title}, author={Author}, created={Created}, modified={Modified}, producer={Producer}",
                    blobName, metadata.PageCount, metadata.Title, metadata.Author, metadata.CreatedAt, metadata.ModDate, metadata.Producer);

                diagnostics = new PdfStepDiagnostics(warnings, []);
                return metadata;
            }
        }

        // Bookmarks/outline tree - PdfPig only, best-effort.
        // - TryGetBookmarks can still throw on a malformed node despite the name; caught here.
        // - null = read failed (skip bookmarks). Empty list = read fine, PDF has none.
        private static IReadOnlyList<Bookmark>? TryGetBookmarks(
            PdfDocument pdf, string blobName, ILogger logger, List<ExtractionWarning> warnings)
        {
            try
            {
                if (!pdf.TryGetBookmarks(out var bookmarks))
                {
                    logger.LogInformation("No bookmarks/outline found in '{Blob}'.", blobName);
                    Warn(warnings, blobName, "No bookmarks/outline found.");
                    return Array.Empty<Bookmark>();
                }

                return bookmarks.GetNodes()
                    .Select(node => new Bookmark(node.Title, node.Level, TryGetPageNumber(node), node is ExternalBookmarkNode))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bookmark extraction failed for '{Blob}'; continuing without bookmarks.", blobName);
                Warn(warnings, blobName, $"Bookmark extraction failed: {ex.Message}");
                return null;
            }
        }

        // AcroForm fields - PdfPig only, best-effort, same pattern as TryGetBookmarks.
        // PartialName is the field's own name segment (AcroFieldCommonInformation doesn't
        // expose the fully-qualified dotted name - that requires walking the Parent chain,
        // which PdfPig only exposes as an unresolved indirect reference).
        private static IReadOnlyList<AcroFormField>? TryGetFormFields(
            PdfDocument pdf, string blobName, ILogger logger, List<ExtractionWarning> warnings)
        {
            try
            {
                if (!pdf.TryGetForm(out var form))
                    return Array.Empty<AcroFormField>();

                var fields = form.Fields
                    .Select(f => new AcroFormField(
                        f.Information.PartialName, f.Information.AlternateName, f.Information.MappingName,
                        f.FieldType.ToString(), f.FieldFlags, f.PageNumber))
                    .ToList();

                if (fields.Count > 0)
                    Warn(warnings, blobName, $"{fields.Count} AcroForm field(s) found.");

                return fields;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AcroForm extraction failed for '{Blob}'; continuing without form fields.", blobName);
                Warn(warnings, blobName, $"AcroForm extraction failed: {ex.Message}");
                return null;
            }
        }

        // XMP metadata packet (Catalog -> /Metadata stream) - a separate, newer metadata
        // mechanism from the legacy Info dictionary above, best-effort, same pattern as
        // TryGetBookmarks/TryGetFormFields. PdfPig only hands back the raw decoded stream
        // bytes (XmpMetadata.MetadataStreamToken.Data) - it does not parse the RDF/XML
        // itself, so only the handful of common Dublin Core / XMP Basic / PDF-schema
        // fields most export tools actually write are read here; see XmpFacts's own comment.
        private static XmpFacts? GetXmpMetadata(PdfDocument pdf, string blobName, ILogger logger, List<ExtractionWarning> warnings)
        {
            try
            {
                if (!pdf.TryGetXmpMetadata(out var xmp))
                    return null;

                var xml = System.Text.Encoding.UTF8.GetString(xmp.MetadataStreamToken.Data.Span);
                var doc = XDocument.Parse(xml);

                var description = doc.Descendants(RdfNs + "Description").FirstOrDefault();
                if (description is null)
                {
                    Warn(warnings, blobName, "XMP packet found but had no rdf:Description element.");
                    return new XmpFacts(null, null, null, null, null, null);
                }

                var facts = new XmpFacts(
                    Title:      NullIfEmpty(FirstRdfContainerItemOrText(description.Element(DcNs + "title"))),
                    Creator:    NullIfEmpty(FirstRdfContainerItemOrText(description.Element(DcNs + "creator"))),
                    Subject:    NullIfEmpty(FirstRdfContainerItemOrText(description.Element(DcNs + "subject"))),
                    Producer:   NullIfEmpty(description.Element(PdfNs + "Producer")?.Value),
                    CreateDate: ParseXmpDate(description.Element(XmpNs + "CreateDate")?.Value),
                    ModifyDate: ParseXmpDate(description.Element(XmpNs + "ModifyDate")?.Value));

                Warn(warnings, blobName, "XMP metadata packet found and parsed.");
                return facts;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "XMP metadata extraction failed for '{Blob}'; continuing without XMP.", blobName);
                Warn(warnings, blobName, $"XMP metadata extraction failed: {ex.Message}");
                return null;
            }
        }

        // dc:title/dc:creator/dc:subject are typically wrapped in an rdf:Alt/Seq/Bag
        // container with one or more rdf:li entries (localized/multi-value) rather than
        // being plain text - prefers the x-default-language item, else the first entry,
        // else falls back to the element's own text for the rare tool that writes a bare
        // string instead of a container.
        private static string? FirstRdfContainerItemOrText(XElement? element)
        {
            if (element is null) return null;

            var items = element.Descendants(RdfNs + "li").ToList();
            if (items.Count == 0) return element.Value.Trim();

            var defaultItem = items.FirstOrDefault(li => (string?)li.Attribute(XNamespace.Xml + "lang") == "x-default");
            return (defaultItem ?? items[0]).Value.Trim();
        }

        private static DateTimeOffset? ParseXmpDate(string? raw) =>
            !string.IsNullOrWhiteSpace(raw) &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt
                : null;

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // Resolves one Info-dictionary date field, using PdfPig's own parser (parsed) for
        // the value but the raw string (raw) to tell "field absent" apart from "field
        // present but unparseable" - the library's Nullable<DateTimeOffset> return can't
        // distinguish those on its own, and the two produce different warnings below.
        private static DateTimeOffset? ResolveDate(
            string? raw, DateTimeOffset? parsed, string blobName, string fieldName, List<ExtractionWarning> warnings)
        {
            if (string.IsNullOrEmpty(raw))
            {
                warnings.Add(new ExtractionWarning(RowNumber: null, DocumentId: blobName, Message: $"No native {fieldName} in the PDF's Info dictionary."));
                return null;
            }

            if (parsed is null)
            {
                warnings.Add(new ExtractionWarning(RowNumber: null, DocumentId: blobName, Message: $"{fieldName} '{raw}' could not be parsed."));
                return null;
            }

            if (parsed > DateTimeOffset.UtcNow)
                warnings.Add(new ExtractionWarning(RowNumber: null, DocumentId: blobName, Message: $"{fieldName} '{parsed:O}' is in the future."));

            return parsed;
        }

        // Page number for a bookmark node.
        // - ExternalBookmarkNode inherits DocumentBookmarkNode but points at another file -
        //   excluded explicitly, its PageNumber isn't a page in this document.
        // - PdfPig's page-number-defaults-to-0 fix (#736/#930) isn't in the pinned 0.1.9,
        //   so an unresolvable destination may be missing from the tree rather than 0.
        private static int? TryGetPageNumber(BookmarkNode node) =>
            node is ExternalBookmarkNode
                ? null
                : node is DocumentBookmarkNode doc && doc.PageNumber > 0
                    ? doc.PageNumber
                    : null;
    }
}
