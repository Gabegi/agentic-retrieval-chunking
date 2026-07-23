namespace AgenticRagApp.Common.Models;

// Base for a structured, source-specific file-open/parse failure category. C# enums
// can't share a base type, and Pdf/Csv each have their own closed set of failure
// categories that make sense for that source - so each source defines its own sealed
// record of static instances deriving from this, instead of one shared enum neither
// source's categories cleanly fit.
public abstract record FailureReasonBase(string Code);
