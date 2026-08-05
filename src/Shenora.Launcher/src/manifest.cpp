#include "shenora/manifest.hpp"

#include <cctype>

namespace shenora {
namespace {

// ASCII lowering only — see the header for why `std::tolower` is deliberately not used here.
char lower_ascii(char c) { return (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c; }

void skip_ws(const std::string& s, std::size_t& i) {
    while (i < s.size() && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) ++i;
}

/// Read a JSON string starting at the opening quote. Handles the escapes a manifest can actually
/// contain: a Windows path carries backslashes, so `\\` is not optional, and a generator that emits
/// `\/` or a `\uXXXX` is within its rights.
bool read_string(const std::string& s, std::size_t& i, std::string& out) {
    if (i >= s.size() || s[i] != '"') return false;
    ++i;
    out.clear();
    while (i < s.size()) {
        char c = s[i++];
        if (c == '"') return true;
        if (c != '\\') { out.push_back(c); continue; }
        if (i >= s.size()) return false;
        char e = s[i++];
        switch (e) {
            case '"': out.push_back('"'); break;
            case '\\': out.push_back('\\'); break;
            case '/': out.push_back('/'); break;
            case 'b': out.push_back('\b'); break;
            case 'f': out.push_back('\f'); break;
            case 'n': out.push_back('\n'); break;
            case 'r': out.push_back('\r'); break;
            case 't': out.push_back('\t'); break;
            case 'u': {
                // Only the BMP-below-0x80 case can appear in a path the rest of this program can use,
                // but decoding to UTF-8 rather than dropping keeps a non-ASCII filename intact.
                if (i + 4 > s.size()) return false;
                unsigned code = 0;
                for (int k = 0; k < 4; ++k) {
                    char h = s[i + k];
                    code <<= 4;
                    if (h >= '0' && h <= '9') code |= static_cast<unsigned>(h - '0');
                    else if (h >= 'a' && h <= 'f') code |= static_cast<unsigned>(h - 'a' + 10);
                    else if (h >= 'A' && h <= 'F') code |= static_cast<unsigned>(h - 'A' + 10);
                    else return false;
                }
                i += 4;
                if (code < 0x80) {
                    out.push_back(static_cast<char>(code));
                } else if (code < 0x800) {
                    out.push_back(static_cast<char>(0xC0 | (code >> 6)));
                    out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
                } else {
                    out.push_back(static_cast<char>(0xE0 | (code >> 12)));
                    out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
                    out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
                }
                break;
            }
            default: return false;
        }
    }
    return false;
}

bool read_number(const std::string& s, std::size_t& i, std::int64_t& out) {
    skip_ws(s, i);
    bool negative = false;
    if (i < s.size() && (s[i] == '-' || s[i] == '+')) { negative = s[i] == '-'; ++i; }
    if (i >= s.size() || !std::isdigit(static_cast<unsigned char>(s[i]))) return false;
    std::int64_t value = 0;
    while (i < s.size() && std::isdigit(static_cast<unsigned char>(s[i]))) {
        value = value * 10 + (s[i] - '0');
        ++i;
    }
    out = negative ? -value : value;
    return true;
}

/// Skip one JSON value of any kind, so an unknown member (a generator's own metadata — the C# side
/// writes `generatedAt`, and an adopter's pipeline may write more) does not fail the parse. A parser
/// that rejects unrecognised members would make every future manifest field a breaking change.
bool skip_value(const std::string& s, std::size_t& i) {
    skip_ws(s, i);
    if (i >= s.size()) return false;
    char c = s[i];
    if (c == '"') { std::string ignored; return read_string(s, i, ignored); }
    if (c == '{' || c == '[') {
        char open = c, close = (c == '{') ? '}' : ']';
        int depth = 0;
        while (i < s.size()) {
            char d = s[i];
            if (d == '"') { std::string ignored; if (!read_string(s, i, ignored)) return false; continue; }
            if (d == open) ++depth;
            else if (d == close && --depth == 0) { ++i; return true; }
            ++i;
        }
        return false;
    }
    while (i < s.size() && s[i] != ',' && s[i] != '}' && s[i] != ']') ++i;
    return true;
}

bool expect(const std::string& s, std::size_t& i, char c) {
    skip_ws(s, i);
    if (i >= s.size() || s[i] != c) return false;
    ++i;
    return true;
}

}  // namespace

std::string normalize_path(std::string path) {
    for (char& c : path) {
        if (c == '\\') c = '/';
        else c = lower_ascii(c);
    }
    return path;
}

bool hashes_equal(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    for (std::size_t i = 0; i < a.size(); ++i) {
        if (lower_ascii(a[i]) != lower_ascii(b[i])) return false;
    }
    return true;
}

bool parse_manifest(const std::string& json, Manifest& out) {
    out = Manifest{};
    std::size_t i = 0;
    if (!expect(json, i, '{')) return false;

    skip_ws(json, i);
    if (i < json.size() && json[i] == '}') return true;  // `{}` — parses, but lists no files

    bool sawFiles = false;
    while (true) {
        std::string key;
        skip_ws(json, i);
        if (!read_string(json, i, key)) return false;
        if (!expect(json, i, ':')) return false;

        // camelCase on the wire, matching the C# `JsonNamingPolicy.CamelCase`; compared
        // case-insensitively because the C# reader sets `PropertyNameCaseInsensitive`.
        std::string lowered = key;
        for (char& c : lowered) c = lower_ascii(c);

        if (lowered == "version") {
            skip_ws(json, i);
            if (!read_string(json, i, out.version)) return false;
        } else if (lowered == "files") {
            sawFiles = true;
            if (!expect(json, i, '[')) return false;
            skip_ws(json, i);
            if (i < json.size() && json[i] == ']') { ++i; }
            else {
                while (true) {
                    if (!expect(json, i, '{')) return false;
                    ManifestFile file;
                    skip_ws(json, i);
                    if (i < json.size() && json[i] == '}') { ++i; }
                    else {
                        while (true) {
                            std::string fkey;
                            skip_ws(json, i);
                            if (!read_string(json, i, fkey)) return false;
                            if (!expect(json, i, ':')) return false;
                            for (char& c : fkey) c = lower_ascii(c);
                            if (fkey == "path") {
                                skip_ws(json, i);
                                if (!read_string(json, i, file.path)) return false;
                            } else if (fkey == "size") {
                                if (!read_number(json, i, file.size)) return false;
                            } else if (fkey == "sha256") {
                                skip_ws(json, i);
                                if (!read_string(json, i, file.sha256)) return false;
                            } else if (!skip_value(json, i)) {
                                return false;
                            }
                            skip_ws(json, i);
                            if (i < json.size() && json[i] == ',') { ++i; continue; }
                            if (!expect(json, i, '}')) return false;
                            break;
                        }
                    }
                    // A path is the only member this program cannot work without.
                    if (file.path.empty()) return false;
                    out.files.push_back(file);
                    skip_ws(json, i);
                    if (i < json.size() && json[i] == ',') { ++i; continue; }
                    if (!expect(json, i, ']')) return false;
                    break;
                }
            }
        } else if (!skip_value(json, i)) {
            return false;
        }

        skip_ws(json, i);
        if (i < json.size() && json[i] == ',') { ++i; continue; }
        if (!expect(json, i, '}')) return false;
        break;
    }

    (void)sawFiles;  // absent `files` is a manifest listing nothing, which the caller must refuse
    return true;
}

}  // namespace shenora
