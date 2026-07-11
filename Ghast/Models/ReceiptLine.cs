namespace Ghast.Models;

/// <summary>
/// One row of the "what changed" receipt / dry-run preview: a plain-English setting name,
/// where it lives, the value before Ghast touched it, and the value now (or the value a
/// dry run WOULD write). "(not set)" means the value does not exist.
/// </summary>
public record ReceiptLine(string Setting, string Location, string Before, string Now);
