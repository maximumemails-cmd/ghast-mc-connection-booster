namespace Ghast.ViewModels;

/// <summary>Live application state driving the stateful footer toggle (Run ⇄ Stop).</summary>
public enum AppRunState { Idle, Starting, Running, Stopping }

/// <summary>Which operation the Run/Stop progress popup is performing.</summary>
public enum RunProgressMode { Start, Stop }

/// <summary>How the Run/Stop progress popup ended.</summary>
public enum RunProgressOutcome
{
    /// <summary>Operation finished successfully and the user dismissed the popup.</summary>
    Completed,

    /// <summary>Operation failed (a Start pass rolls itself back before reporting this).</summary>
    Failed,

    /// <summary>User clicked Stop inside the Start-success popup — caller should run the stop flow.</summary>
    StopRequested,

    /// <summary>Popup closed without a definite result.</summary>
    Closed
}

/// <summary>User's answer to the "Ghast is still active" close prompt.</summary>
public enum CloseChoice { Revert, LeaveApplied, Cancel }
