namespace AgenticRagApp.Indexing.Pdf.Models;

// Best-effort read of the PDF's XMP metadata packet (Catalog -> /Metadata stream),
// as read by PdfNativeMetadataExtractor.GetXmpMetadata - a separate, newer metadata
// mechanism from the legacy Info dictionary (DocMetadata's own Title/Author/etc.),
// which some export tools populate instead of, or in addition to, the legacy fields.
// PdfPig only hands back the raw decoded stream bytes (XmpMetadata.MetadataStreamToken.Data) -
// it does not parse the RDF/XML itself, so this covers only the handful of common
// Dublin Core / XMP Basic / PDF-schema fields most tools actually write, not the full
// XMP spec (custom schemas, structured/array values beyond a first entry, etc.).
// Null means either no /Metadata stream exists, or the packet couldn't be parsed as
// XML - both logged as a warning, not distinguished here since the caller can't act on
// either differently.
public sealed record XmpFacts(
    string? Title,
    string? Creator,
    string? Subject,
    string? Producer,
    DateTimeOffset? CreateDate,
    DateTimeOffset? ModifyDate);
