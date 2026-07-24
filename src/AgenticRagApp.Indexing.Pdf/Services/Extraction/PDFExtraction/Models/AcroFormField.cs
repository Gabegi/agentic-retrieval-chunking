namespace AgenticRagApp.Indexing.Pdf.Models;

// One interactive form field from a PDF's AcroForm, as read by
// PdfNativeMetadataExtractor.TryGetForm. PartialName is the field's own name segment
// (PdfPig's AcroFieldCommonInformation.PartialName - not the fully-qualified
// dotted name, since that requires walking the Parent chain PdfPig only exposes as an
// indirect reference, not resolved). FieldType is PdfPig's own AcroFieldType.ToString()
// (Text/CheckBox/RadioButton/ListBox/ComboBox/PushButton/Signature) - kept as a string
// here so this model doesn't need to reference PdfPig's own enum type.
public sealed record AcroFormField(string? PartialName, string FieldType, int? PageNumber);
