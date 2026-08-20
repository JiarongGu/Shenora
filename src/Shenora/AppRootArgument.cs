namespace Shenora;

/// <summary>
/// Resolves the install ROOT the app should treat as its base directory. In a packaged loose-folder
/// bundle the native launcher lives at the install root and the runtime exe in a subfolder
/// (<c>libs/</c>), so <c>AppContext.BaseDirectory</c> would repoint every install-relative path at the
/// subfolder; the launcher passes the true root via <c>--app-root "&lt;path&gt;"</c>. Falls back when the
/// flag is absent (a dev run, or a direct double-click of the runtime exe). Feed the result into
/// <see cref="ShenoraPathsOptions.ExplicitRoot"/>.
/// </summary>
public static class AppRootArgument
{
    /// <summary>The launcher flag name (<c>--app-root</c>).</summary>
    public const string Flag = "--app-root";

    /// <summary>
    /// Returns the value passed with <c>--app-root</c> (space-separated <c>--app-root &lt;path&gt;</c>
    /// or joined <c>--app-root=&lt;path&gt;</c>, surrounding quotes/whitespace stripped), or
    /// <paramref name="fallback"/> when the flag is missing or its value is blank.
    /// </summary>
    public static string Resolve(string[]? args, string fallback)
    {
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == null) continue;

                // --app-root <path>
                if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        var val = Clean(args[i + 1]);
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                    break;
                }

                // --app-root=<path>
                if (arg.StartsWith(Flag + "=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = Clean(arg.Substring(Flag.Length + 1));
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                    break;
                }
            }
        }
        return fallback;
    }

    private static string Clean(string? s) => s?.Trim().Trim('"').Trim() ?? string.Empty;
}
