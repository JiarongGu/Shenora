namespace Shenora;

/// <summary>
/// The application's runtime environment — resolved ONCE and injected everywhere, so dev-mode detection
/// lives here and nowhere else. Development mode means either <c>DOTNET_ENVIRONMENT</c> (falling back to
/// <c>ASPNETCORE_ENVIRONMENT</c>) says so, or a <c>.dev</c> marker file sits next to the executable —
/// which is how a packaged build is flipped into dev mode without touching the machine's environment.
/// </summary>
public sealed class ShenoraEnvironment
{
    /// <summary>File name of the dev-mode marker looked up in <see cref="BaseDirectory"/>.</summary>
    public const string DevMarkerFileName = ".dev";

    private ShenoraEnvironment(string baseDirectory, bool isDevelopment)
    {
        BaseDirectory = baseDirectory;
        IsDevelopment = isDevelopment;
    }

    /// <summary>
    /// The directory dev-mode detection anchored at — the RESOLVED APP ROOT when composed through
    /// <c>ShenoraApplication.CreateBuilder</c> (in packaged bundles the install root beside the launcher,
    /// where the <c>.dev</c> marker lives, not the exe's <c>libs/</c> folder); plain
    /// <c>AppContext.BaseDirectory</c> in direct use.
    /// </summary>
    public string BaseDirectory { get; }

    /// <summary>True when running in development mode (env var or <c>.dev</c> marker).</summary>
    public bool IsDevelopment { get; }

    /// <summary>
    /// Detect the environment for <paramref name="baseDirectory"/>. The optional
    /// <paramref name="getEnvironmentVariable"/> seam exists for tests; production callers omit it.
    /// </summary>
    public static ShenoraEnvironment Detect(string baseDirectory, Func<string, string?>? getEnvironmentVariable = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var name = env("DOTNET_ENVIRONMENT") ?? env("ASPNETCORE_ENVIRONMENT");
        var isDevelopment =
            string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(baseDirectory, DevMarkerFileName));
        return new ShenoraEnvironment(baseDirectory, isDevelopment);
    }
}
