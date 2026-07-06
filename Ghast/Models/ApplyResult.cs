namespace Ghast.Models;

/// <summary>Outcome of one tweak during a Run / Restore pass, shown in the status strip.</summary>
public record ApplyResult(string Item, bool Success, string? Message = null);
