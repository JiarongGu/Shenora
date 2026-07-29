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
    public void No_matching_resources_and_no_directory_serves_nothing()
    {
        var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = Assembly.GetExecutingAssembly(),
            ResourcePrefix = "Shenora.Tests.NoSuchPrefix",
        });
        Assert.False(provider.IsEmbedded);
        Assert.Null(provider.GetResourceStream("index.html"));
        Assert.False(provider.Exists("index.html"));
    }
}
