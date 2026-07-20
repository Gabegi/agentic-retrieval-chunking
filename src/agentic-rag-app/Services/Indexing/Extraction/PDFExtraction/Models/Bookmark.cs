namespace ProtocolsIndexer.Models;

// One node from a PDF's outline/bookmark tree, as read by PdfNativeMetadataExtractor.
// PageNumber is null when the node's destination couldn't be resolved to a page in
// this document (an external-file link, or an unresolvable internal destination) -
// IsExternal tells PdfSectionBreadCrumbBuilder which of those two it was, for
// separate diagnostics (both already collapse to PageNumber=null by the time this
// record exists, so that distinction would otherwise be lost).
public sealed record Bookmark(string Title, int Level, int? PageNumber, bool IsExternal);
