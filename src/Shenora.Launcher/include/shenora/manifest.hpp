// Shenora launcher — the update manifest, and the ONE normalisation rule.
//
// This file is the C++ half of a contract whose C# half is `Shenora.IO/UpdateManifest.cs`. The two
// must agree exactly, and `devtools/scripts/launcher-conformance.mjs` proves it against manifests the
// C# side actually wrote, rather than against a fixture written here. That is the whole reason the
// harness exists: the design doc's §0 records that BOTH donor apps wrote this twice — a C# model and a
// C++ parser — because the two phases are in different languages, and a rule implemented twice is a
// rule that can drift.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace shenora {

struct ManifestFile {
    std::string path;      // manifest-relative, forward-slashed, as written
    std::int64_t size = 0;
    std::string sha256;    // lower-case hex, but compare case-insensitively — see below
};

struct Manifest {
    std::string version;
    std::vector<ManifestFile> files;
};

/// The ONE path rule, mirroring `ManifestDiff.Normalize` exactly: backslashes become forward slashes,
/// then lower-case.
///
/// Both halves are load-bearing and both are sabotage-verified on the C# side. Without the separator
/// half, a Windows-built manifest matches nothing on any other host and every file reports as "added"
/// on every check, forever — the updater never converges. Without the case half, one letter's casing
/// turns a whole release into "not carried".
///
/// ASCII-only lowering, deliberately: `ToLowerInvariant` is what the C# side uses, and a locale-aware
/// `tolower` here would disagree with it on a Turkish-locale machine (the dotted-I problem) for exactly
/// the paths a release is least likely to test.
std::string normalize_path(std::string path);

/// Case-insensitive hash comparison, mirroring the C# `StringComparison.OrdinalIgnoreCase`. A
/// generator's hex casing is not part of the contract, and treating it as one reports EVERY file as
/// changed.
bool hashes_equal(const std::string& a, const std::string& b);

/// Parse a manifest document. Returns false on anything it cannot read — a caller must treat that as
/// "no manifest", never as "an empty manifest", because an empty release manifest legitimately means
/// every tracked path was removed.
///
/// Hand-written rather than a vendored JSON library, and the trade is recorded in D50: the document
/// shape is fixed and tiny, "small" is a stated requirement of this binary, and the correctness risk
/// that a library would remove is bought back by the conformance harness, which tests this parser
/// against real C#-produced output.
bool parse_manifest(const std::string& json, Manifest& out);

}  // namespace shenora
