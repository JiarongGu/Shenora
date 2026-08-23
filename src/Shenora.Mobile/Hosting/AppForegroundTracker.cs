using Shenora.Modules.Platform;

namespace Shenora.Mobile;

/// <summary>
/// Decides which of a window's raw visibility signals are the app actually LEAVING and COMING BACK, and
/// reports those to <see cref="AppLifecycle"/>.
/// </summary>
/// <remarks>
/// 🔴 <b>A RECREATION IS NOT A DEPARTURE, and a repeat is not a transition.</b> Both are ordinary here —
/// the platform destroys and rebuilds the window for a configuration change, and a second reporter (which
/// that same rebuild produces) sees every transition too. Forwarded one for one, both read as the user
/// leaving, and the consequence is not cosmetic: the resume carries a DURATION, so a page whose rule is
/// <c>away &gt; 30s → reconnect</c> reconnects on every rotation, and a duplicate resume carries
/// <c>null</c>, which the same page reads as "could not measure" and also reconnects on.
/// <para>
/// ⚠ <b>It is seeded as ON SCREEN.</b> This is built by a live page, so the app is in the foreground by
/// construction — and the first signal it ever sees is therefore a HIDE. Starting the other way round
/// swallows that first departure, which is indistinguishable from the feature being broken.
/// </para>
/// <para>
/// Pure arithmetic, no platform type: it is compiled into <c>Shenora.Tests</c> as well as into the
/// shells, because a rotation and a duplicated signal are exactly what a device run forgets to try.
/// </para>
/// </remarks>
internal sealed class AppForegroundTracker
{
    private readonly AppLifecycle _lifecycle;
    private readonly Lock _gate = new();
    private bool _foreground = true;

    /// <param name="lifecycle">The reporter. Register it with <c>AddShenoraAppLifecycle</c>.</param>
    public AppForegroundTracker(AppLifecycle lifecycle) =>
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    /// <summary>The app's window became visible.</summary>
    public void Shown()
    {
        lock (_gate)
        {
            if (_foreground) return;
            _foreground = true;
        }
        // Reported outside the lock: `Emit` runs the bus's own dispatch, and a subscriber reporting back
        // in would meet a lock this shallow holds.
        _lifecycle.ReportResumed();
    }

    /// <summary>The app's window stopped being visible.</summary>
    /// <param name="forRecreation">
    /// True when the platform is destroying this window only to build it again — a configuration change.
    /// ⚠ The app never left, so the state does NOT move either: the rebuilt window's <see cref="Shown"/>
    /// then finds the app already in the foreground and stays quiet, which is what keeps the pair
    /// balanced instead of leaving a resume owed.
    /// </param>
    public void Hidden(bool forRecreation)
    {
        lock (_gate)
        {
            if (forRecreation || !_foreground) return;
            _foreground = false;
        }
        _lifecycle.ReportStopped();
    }
}
