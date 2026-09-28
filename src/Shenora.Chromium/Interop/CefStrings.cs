namespace Shenora.Chromium.Interop;

/// <summary>
/// <c>cef_string_t</c> in both directions. A string handed INTO CEF (<c>const cef_string_t*</c>) needs no
/// allocation at all: a view over pinned managed characters with no destructor is valid input, and CEF
/// copies what it keeps. A string CEF hands BACK as <c>cef_string_userfree_t</c> is the caller's to free.
/// </summary>
internal static unsafe class CefStrings
{
    /// <summary>A non-owning view for passing <paramref name="length"/> pinned characters into CEF.</summary>
    public static _cef_string_utf16_t View(char* chars, int length) => new() { str = (ushort*)chars, length = (nuint)length };

    /// <summary>The characters, or "" for null.</summary>
    public static string Read(_cef_string_utf16_t* s) =>
        s == null || s->str == null ? "" : new string((char*)s->str, 0, (int)s->length);

    /// <summary>Read a <c>cef_string_userfree_t</c>, then free it, which the caller owes CEF.</summary>
    public static string TakeUserFree(_cef_string_utf16_t* s)
    {
        if (s == null) return "";
        var text = Read(s);
        Cef.cef_string_userfree_utf16_free(s);
        return text;
    }
}
