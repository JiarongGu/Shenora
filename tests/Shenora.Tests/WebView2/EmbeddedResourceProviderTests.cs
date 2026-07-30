using System.Reflection;
using Shenora.WebView2;

namespace Shenora.Tests.WebView2;

public class EmbeddedResourceProviderTests
{
    private const string Prefix = "Shenora.Tests.TestAssets.wwwroot";

    private static EmbeddedResourceProvider Embedded() => new(new EmbeddedResourceProviderOptions
    {
        Assembly = Assembly.GetExecutingAssembly(),
        ResourcePrefix = Prefix,
    });

    private static string ReadAll(Stream? stream)
    {
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Embedded_mode_serves_root_and_nested_paths()
    {
        var provider = Embedded();
        Assert.True(provider.IsEmbedded);
        Assert.Contains("shenora-test-index", ReadAll(provider.GetResourceStream("index.html")));
        Assert.Contains("shenora-test-asset", ReadAll(provider.GetResourceStream("assets/app-abc123.js")));
        Assert.True(provider.Exists("index.html"));
        Assert.False(provider.Exists("missing.html"));
        Assert.Null(provider.GetResourceStream("missing.html"));
    }

    [Fact]
    public void Dotted_file_names_resolve()
    {
        // The source app parsed manifest names BACK to paths (dots → slashes), so vendor.min.js
        // could ONLY be requested as vendor/min.js. The path→name direction here makes the real
        // path work. (MSBuild collapses dots and slashes identically at embed time, so the alias
        // spelling also hits the same resource — that information is gone in the manifest name;
        // harmless, but the canonical path resolving is what the source got wrong.)
        var provider = Embedded();
        Assert.Contains("shenora-dotted-filename", ReadAll(provider.GetResourceStream("vendor.min.js")));
    }

    [Fact]
    public void Lookups_normalize_case_slashes_and_leading_separators()
    {
        var provider = Embedded();
        Assert.True(provider.Exists("INDEX.HTML"));
        Assert.True(provider.Exists("/index.html"));
        Assert.True(provider.Exists(@"assets\app-abc123.js"));
    }

    [Fact]
    public void Warmup_is_idempotent_and_serving_still_works()
    {
        var provider = Embedded();
        provider.BeginWarmup();
        provider.BeginWarmup();
        Assert.Contains("shenora-test-index", ReadAll(provider.GetResourceStream("index.html")));
    }

    [Fact]
    public void File_mode_serves_disk_content_and_sees_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "from-disk-v1");
            var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = Assembly.GetExecutingAssembly(),
                ResourcePrefix = Prefix,
                FileFallbackDirectory = root,
                PreferFiles = true, // the dev-mode wiring
            });

            Assert.False(provider.IsEmbedded);
            Assert.Equal("from-disk-v1", ReadAll(provider.GetResourceStream("index.html")));

            // Dev rebuilds overwrite the bundle mid-session — file mode must not cache.
            File.WriteAllText(Path.Combine(root, "index.html"), "from-disk-v2");
            Assert.Equal("from-disk-v2", ReadAll(provider.GetResourceStream("index.html")));

            Assert.False(provider.Exists("missing.html"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prefer_files_without_an_existing_directory_stays_embedded()
    {
        var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = Assembly.GetExecutingAssembly(),
            ResourcePrefix = Prefix,
            FileFallbackDirectory = Path.Combine(Path.GetTempPath(), "shenora-no-such-" + Guid.NewGuid().ToString("n")),
            PreferFiles = true,
        });
        Assert.True(provider.IsEmbedded);
    }

    [Fact]
    public void No_matching_resources_and_no_directory_reports_that_it_serves_nothing()
    {
        // A mistyped or stale ResourcePrefix matches no manifest names, so every request 404s and the
        // app opens a BLACK WINDOW with no error anywhere — the prefix depends on MSBuild's name
        // mangling, so it is the last thing anyone suspects (P5.5 H3).
        //
        // The provider REPORTS this rather than throwing, and that split is deliberate: a provider with
        // nothing to serve is perfectly valid when the page loads from a dev URL, which is the normal
        // state of a fresh clone whose bundle has not been built. Only the host knows whether the bundle
        // IS the start document, so the loud failure lives there (see WebViewHostTests).
        var messages = new List<string>();
        var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = Assembly.GetExecutingAssembly(),
            ResourcePrefix = "Shenora.Tests.NoSuchPrefix",
            Log = messages.Add,
        });

        Assert.False(provider.CanServe);
        Assert.False(provider.IsEmbedded);
        Assert.Null(provider.GetResourceStream("index.html"));
        Assert.False(provider.Exists("index.html"));

        // The notice has to be self-servicing: the whole difficulty is not being able to see the
        // manifest, so it names the bad prefix and what the assembly ACTUALLY contains.
        var notice = Assert.Single(messages);
        Assert.Contains("SERVES NOTHING", notice, StringComparison.Ordinal);
        Assert.Contains("Shenora.Tests.NoSuchPrefix", notice, StringComparison.Ordinal);
        Assert.Contains("available manifest prefixes", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_file_directory_is_enough_on_its_own()
    {
        // No embedded match is FINE when file mode can serve — the unpackaged/dev shape.
        var directory = Path.Combine(Path.GetTempPath(), "shenora-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = Assembly.GetExecutingAssembly(),
                ResourcePrefix = "Shenora.Tests.NoSuchPrefix",
                FileFallbackDirectory = directory,
                PreferFiles = true,
            });

            Assert.True(provider.CanServe);
            Assert.False(provider.IsEmbedded);
            Assert.Null(provider.GetResourceStream("index.html")); // nothing there yet, but serviceable
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── Path containment (P5.5 H1) ────────────────────────────────────────────────────────────────
    // File-mode serving had NO containment: the host unescapes the request path before calling the
    // provider (it must, so CJK/spaced bundle filenames resolve), so "%2e%2e%2f…" arrived as "../",
    // and a ROOTED path escaped even more simply because Path.Combine discards its first argument
    // when the second is rooted. Responses carry Access-Control-Allow-Origin: *.

    [Theory]
    // Traversal, in both separator spellings and nested forms.
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("assets/../../secret.txt")]
    [InlineData("../../Windows/win.ini")]
    // Rooted paths — the Path.Combine vector.
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/C:/Windows/win.ini")]
    public void File_mode_refuses_paths_that_escape_the_root(string virtualPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            // A real file immediately OUTSIDE the root: a successful traversal would return it.
            File.WriteAllText(Path.Combine(root, "..", "shenora-outside-marker.txt"), "escaped");
            File.WriteAllText(Path.Combine(root, "index.html"), "inside");

            var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = Assembly.GetExecutingAssembly(),
                ResourcePrefix = "Shenora.Tests.NoSuchPrefix", // force file mode
                FileFallbackDirectory = root,
                PreferFiles = true,
            });
            Assert.False(provider.IsEmbedded);

            Assert.Null(provider.GetResourceStream(virtualPath));
            Assert.False(provider.Exists(virtualPath));
            // …and the legitimate path still works, i.e. containment didn't just break serving.
            Assert.Equal("inside", ReadAll(provider.GetResourceStream("index.html")));
        }
        finally
        {
            try { File.Delete(Path.Combine(root, "..", "shenora-outside-marker.txt")); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    // The unescaping the host does exists FOR these — containment must not regress them.
    [InlineData("assets/my app.js")]
    [InlineData("assets/日本語.js")]
    [InlineData("nested/deep/file.css")]
    public void File_mode_still_serves_legitimate_paths_including_spaces_and_cjk(string virtualPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-tests-" + Guid.NewGuid().ToString("n"));
        try
        {
            var full = Path.Combine(root, virtualPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "served");

            var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = Assembly.GetExecutingAssembly(),
                ResourcePrefix = "Shenora.Tests.NoSuchPrefix",
                FileFallbackDirectory = root,
                PreferFiles = true,
            });

            Assert.True(provider.Exists(virtualPath));
            Assert.Equal("served", ReadAll(provider.GetResourceStream(virtualPath)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_sibling_directory_sharing_the_root_prefix_is_not_inside_the_root()
    {
        // "…/bundle-evil" must not pass as a child of "…/bundle" — the prefix check appends the
        // separator for exactly this case.
        var baseDir = Path.Combine(Path.GetTempPath(), "shenora-tests-" + Guid.NewGuid().ToString("n"));
        var root = Path.Combine(baseDir, "bundle");
        var sibling = Path.Combine(baseDir, "bundle-evil");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "escaped");

            Assert.Null(EmbeddedResourceProvider.ResolveContained(root, "../bundle-evil/secret.txt"));
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
}
