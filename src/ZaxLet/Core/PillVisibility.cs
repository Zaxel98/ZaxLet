namespace ZaxLet;

/// <summary>Keputusan tampil/sembunyi jendela pill sebagai fungsi murni, supaya bisa diuji tanpa jendela.</summary>
public static class PillVisibility
{
    /// <summary>
    /// Jendela tampil kecuali: fullscreen atau disembunyikan pengguna (keduanya tanpa intipan kursor), atau mode "saat ada aktivitas" tanpa aktivitas.
    /// notifForced: ada notifikasi yang boleh memaksa tampil di fullscreen/sembunyi. fsMusic: musik di layar penuh. ghost: Alt ditahan di atas pill.
    /// </summary>
    public static bool Want(bool fs, bool snooze, bool snoozePeek, bool peek, bool miniActive, bool ghost, bool notifForced, bool fsMusic, bool activityHide)
    {
        bool forced = (fs || snooze) && notifForced || fsMusic;
        bool hidden = fs && !peek && !miniActive || snooze && !(snoozePeek && peek) || activityHide && !peek;
        return !ghost && (forced || !hidden);
    }
}
