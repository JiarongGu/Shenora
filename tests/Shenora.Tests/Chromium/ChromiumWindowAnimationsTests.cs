using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// <see cref="ChromiumWindowOptions.Animations"/>. The window applies it before CEF shows it, which needs CEF and is the
/// Chromium probe's to measure; here, the default and the call a form stands in for.
/// </summary>
public class ChromiumWindowAnimationsTests
{
    [Fact]
    public void A_window_keeps_the_system_animations_by_default() =>
        Assert.Equal(WindowAnimations.System, new ChromiumWindowOptions().Animations);

    // The attribute is set-only (reading it back answers E_INVALIDARG), so this pins the call DWM accepts, not that it
    // turns them OFF rather than on: that rests on the A/B the cover's timing was measured with.
    [Fact]
    public void The_call_that_turns_them_off_is_one_DWM_accepts() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        Assert.True(ChromiumWindowAnimations.Disable(form.Handle));
    });
}
