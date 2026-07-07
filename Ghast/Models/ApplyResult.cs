namespace Ghast.Models;

/// <summary>Outcome of one tweak during a Run / Restore pass, shown in the status strip.</summary>
public record ApplyResult(string Item, bool Success, string? Message = null);

/// <summary>
/// Progress tick for the Run/Stop popup: overall percent (0–100), the current step label,
/// and the just-completed per-item result (null for pure progress ticks).
/// </summary>
public record ApplyProgress(int Percent, string Step, ApplyResult? Result = null);
