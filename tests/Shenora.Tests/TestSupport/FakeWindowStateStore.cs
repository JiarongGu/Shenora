using Shenora.Windows;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// The ONE <see cref="IWindowStateStore"/> double (P5.5 H7). It replaced three per-class fakes —
/// <c>RecordingStore</c>, <c>MemoryStore</c> and <c>FakeStore</c> — of which the last was already a
/// superset of the other two, so this is that shape with a name saying what it is.
/// <para>
/// The seed and the assertion target are deliberately SEPARATE members: <see cref="Stored"/> is what
/// <see cref="Load"/> hands back, <see cref="Saved"/> is what the code under test wrote. The fake it
/// replaced in <c>WindowStateManagerTests</c> used one field for both, which reads as a round-trip
/// guarantee it never actually made (nothing re-read a saved value there) — and would have quietly
/// turned an assertion into a tautology the first time a test did seed AND assert.
/// </para>
/// </summary>
internal sealed class FakeWindowStateStore : IWindowStateStore
{
    /// <summary>Seeded state — what <see cref="Load"/> returns. Null models a first-ever launch.</summary>
    public WindowState? Stored { get; init; }

    /// <summary>True once the code under test actually consulted the store.</summary>
    public bool LoadCalled { get; private set; }

    /// <summary>The last state written, or null if nothing was saved.</summary>
    public WindowState? Saved { get; private set; }

    /// <inheritdoc />
    public WindowState? Load()
    {
        LoadCalled = true;
        return Stored;
    }

    /// <inheritdoc />
    public void Save(WindowState state) => Saved = state;
}
