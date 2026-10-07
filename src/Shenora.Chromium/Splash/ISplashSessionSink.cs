namespace Shenora.Chromium.Host;

/// <summary>What a <see cref="SplashContext"/> tells the session that owns it.</summary>
internal interface ISplashSessionSink
{
    /// <summary>A state the render function reads changed. Any thread.</summary>
    void Invalidate();

    /// <summary>The component asked to lift the splash now. Any thread.</summary>
    void Close();
}
