namespace ZaxLet;

/// <summary>Pill window show/hide decision as a pure function, so it can be tested without a window.</summary>
public static class PillVisibility
{
    /// <summary>
    /// Window is shown unless: fullscreen or hidden by the user (both without cursor peek), or "when active" mode with no activity.
    /// notifForced: there is a notification allowed to force-show in fullscreen/hidden. fsMusic: music in fullscreen. ghost: Alt held over pill.
    /// </summary>
    public static bool Want(bool fs, bool snooze, bool snoozePeek, bool peek, bool miniActive, bool ghost, bool notifForced, bool fsMusic, bool activityHide)
    {
        bool forced = (fs || snooze) && notifForced || fsMusic;
        bool hidden = fs && !peek && !miniActive || snooze && !(snoozePeek && peek) || activityHide && !peek;
        return !ghost && (forced || !hidden);
    }
}
