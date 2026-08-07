using System.Text.Json;
using System.Text.RegularExpressions;
using Shenora.Engine.Files;

namespace Shenora.Windows;

/// <summary>
/// The document-created scripts <see cref="WebViewHost"/> injects. Ported from the family apps;
/// the global-injection builder replaces the source's raw string interpolation (unescaped values
/// could break out of the script — the audit's escaping gap) with real JSON serialization.
/// </summary>
internal static partial class WebViewScripts
{
    // Values serialize camelCase to match the family's JS-side conventions (same policy the IPC
    // serializer defaults will use). The default STJ encoder escapes '<', '>', '&' and quotes as
    // \uXXXX, so a value containing "</script>" cannot terminate the injected block.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex JsIdentifier();

    /// <summary>Builds <c>window.&lt;name&gt; = &lt;json&gt;;</c> for an injected global.</summary>
    public static string BuildGlobalScript(string name, object? value)
    {
        if (string.IsNullOrEmpty(name) || !JsIdentifier().IsMatch(name))
            throw new ArgumentException(
                $"'{name}' is not a valid JavaScript identifier for an injected global.", nameof(name));
        return $"window.{name} = {JsonSerializer.Serialize(value, JsonOptions)};";
    }

    /// <summary>
    /// Prevent the browser's default file-drop behavior (navigating to the dropped file) for
    /// EXTERNAL file drags only — internal HTML5 drag-drop (React DnD etc.) keeps working.
    /// External drops are the native drop-zone overlay's job at the Form level.
    /// </summary>
    public const string PreventDefaultFileDrop = """
        (function() {
            document.addEventListener('dragover', function(e) {
                var types = e.dataTransfer.types;
                if (types && types.indexOf('Files') !== -1) {
                    e.preventDefault();
                }
            }, true);
            document.addEventListener('drop', function(e) {
                var types = e.dataTransfer.types;
                if (types && types.indexOf('Files') !== -1) {
                    e.preventDefault();
                }
            }, true);
        })();
        """;

    /// <summary>
    /// Block browser chrome shortcuts in production (find, print, save, view-source, zoom,
    /// devtools…). Editing shortcuts (copy/paste/undo) and Ctrl+R stay available. JavaScript
    /// injection because the WinForms WebView2 control does not expose AcceleratorKeyPressed.
    /// Capture phase so the block runs before the app's own handlers.
    /// </summary>
    public const string BlockBrowserShortcuts = """
        (function() {
            document.addEventListener('keydown', function(e) {
                var ctrl = e.ctrlKey || e.metaKey;
                var key = e.key;
                if (ctrl) {
                    switch (key.toLowerCase()) {
                        case 'f': case 'g': case 'h': case 'j': case 'p':
                        case 's': case 'u': case '0': case '+': case '=':
                        case '-': case '_':
                            e.preventDefault();
                            e.stopPropagation();
                            return false;
                    }
                    if (e.shiftKey && key.toLowerCase() === 'i') {
                        e.preventDefault();
                        e.stopPropagation();
                        return false;
                    }
                }
                if (key === 'F12') {
                    e.preventDefault();
                    e.stopPropagation();
                    return false;
                }
            }, true);
        })();
        """;
}
