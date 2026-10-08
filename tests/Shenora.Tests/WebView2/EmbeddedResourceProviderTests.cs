using System.Reflection;
using Shenora.Tests.TestSupport;
using Shenora.Windows;
using Shenora.Core.WebView;

using Shenora;
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

    // MSBuild makes every FOLDER of an embedded file an identifier (measured: my-lib → my_lib, 1.0 → _1._0, "a b" →
    // a_b, -lead → _lead, 日本 kept) and keeps the file name as it is. Turning slashes into dots alone missed them all.
    [Theory]
    [InlineData("my-lib/1.0/x-1.js", "shenora-dashed-folder")]
    [InlineData("a b/c d.js", "shenora-spaced-folder")]
    [InlineData("-lead/a.js", "shenora-lead-folder")]
    public void Folders_resolve_as_MSBuild_names_them(string path, string marker) =>
        Assert.Contains(marker, ReadAll(Embedded().GetResourceStream(path)));

    // The names MSBuild wrote for these folders, measured on a throwaway project (2026-10-08). Their file names cannot be
    // fixtures here (doctor keeps tracked names ASCII), so an assembly holding exactly those names stands in.
    [Theory]
    [InlineData("_/f.js", "MyApp.wwwroot.__.f.js")]               // a lone underscore is doubled
    [InlineData("Ⅻ/f.js", "MyApp.wwwroot.__.f.js")]           // a letter NUMBER (Ⅻ) is no identifier character
    [InlineData("a‍b/f.js", "MyApp.wwwroot.a_b.f.js")]        // nor is a format character (a zero-width joiner)
    [InlineData("x⃝y/f.js", "MyApp.wwwroot.x⃝y.f.js")]    // an enclosing mark is
    [InlineData("9/f.js", "MyApp.wwwroot._9.f.js")]
    [InlineData("_x/f.js", "MyApp.wwwroot._x.f.js")]
    [InlineData("ab_/f.js", "MyApp.wwwroot.ab_.f.js")]
    public void Every_folder_resolves_to_the_name_MSBuild_wrote(string path, string manifestName) =>
        Assert.Equal(manifestName, ReadAll(new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = new ManifestOnly(manifestName),
            ResourcePrefix = "MyApp.wwwroot",
        }).GetResourceStream(path)));

    /// <summary>An assembly that is only a manifest: each resource's content is its own name.</summary>
    private sealed class ManifestOnly(params string[] names) : Assembly
    {
        public override string[] GetManifestResourceNames() => names;

        public override Stream? GetManifestResourceStream(string name) =>
            names.Contains(name) ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(name)) : null;

        public override AssemblyName GetName() => new("ManifestOnly");

        public override AssemblyName GetName(bool copiedName) => GetName();
    }

    // MSBuild reads msg.fr.json as a French resource and builds it into a satellite assembly, out of the main one —
    // unless the item says WithCulture="false", which this project's does, as an app's must (measured: without it both
    // files were missing from the manifest).
    [Theory]
    [InlineData("i18n/msg.fr.json", "shenora-culture-name")]
    [InlineData("chunk.en.js", "shenora-culture-script")]
    public void A_file_named_like_a_culture_is_embedded_too(string path, string marker) =>
        Assert.Contains(marker, ReadAll(Embedded().GetResourceStream(path)));

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
        using var temp = TempDir.Create();
        temp.WriteFile("index.html", "from-disk-v1");
        var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = Assembly.GetExecutingAssembly(),
            ResourcePrefix = Prefix,
            FileFallbackDirectory = temp.Root,
            PreferFiles = true, // the dev-mode wiring
        });

        Assert.False(provider.IsEmbedded);
        Assert.Equal("from-disk-v1", ReadAll(provider.GetResourceStream("index.html")));

        // Dev rebuilds overwrite the bundle mid-session — file mode must not cache.
        temp.WriteFile("index.html", "from-disk-v2");
        Assert.Equal("from-disk-v2", ReadAll(provider.GetResourceStream("index.html")));

        Assert.False(provider.Exists("missing.html"));
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
            Log = AppCallback.Logger(messages.Add),
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
        using var temp = TempDir.Create();
        var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = Assembly.GetExecutingAssembly(),
            ResourcePrefix = "Shenora.Tests.NoSuchPrefix",
            FileFallbackDirectory = temp.Root,
            PreferFiles = true,
        });

        Assert.True(provider.CanServe);
        Assert.False(provider.IsEmbedded);
        Assert.Null(provider.GetResourceStream("index.html")); // nothing there yet, but serviceable
    }

    // ── Path containment (P5.5 H1) ────────────────────────────────────────────────────────────────
    // File-mode serving had NO containment: the host unescapes the request path before calling the
    // provider (it must, so CJK/spaced bundle filenames resolve), so "%2e%2e%2f…" arrived as "../",
    // and a ROOTED path escaped even more simply because Path.Combine discards its first argument
    // when the second is rooted. Responses carry Access-Control-Allow-Origin: *.

    // EVERY case below must escape to a file that REALLY EXISTS, and the test asserts that as a
    // PRECONDITION (P5.5 H7). Four of the seven original cases were vacuous: they asked for
    // "../secret.txt" and friends while the fixture's only outside file was called
    // "shenora-outside-marker.txt", so nothing was there to leak — delete containment entirely and
    // those cases still went green, because Path.Combine resolved to a path that merely did not
    // exist. Only the three ROOTED cases (which land on the real win.ini) were doing any work. The
    // fixture now puts the escape target where the requested paths actually point.
    //
    //   <temp>/                  <- the parent the traversal cases reach
    //     secret.txt             <- what they would leak
    //     bundle/                <- the provider's root
    //       assets/
    //       index.html
    private const string OutsideContent = "escaped-the-root";

    [Theory]
    // Traversal, in both separator spellings and nested forms — all reaching <temp>/secret.txt.
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("assets/../../secret.txt")]
    // Rooted paths — the Path.Combine vector, landing on a real OS file.
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/C:/Windows/win.ini")]
    public void File_mode_refuses_paths_that_escape_the_root(string virtualPath)
    {
        using var temp = TempDir.Create("bundle/assets");
        var root = temp.Combine("bundle");
        temp.WriteFile("secret.txt", OutsideContent);
        File.WriteAllText(Path.Combine(root, "index.html"), "inside");

        // PRECONDITION: the escape target exists, so a refusal is containment working and not just
        // a missing file. Without this the assertions below can pass for the wrong reason.
        var escapeTarget = virtualPath.Contains("win.ini", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini")
            : temp.Combine("secret.txt");
        Assert.True(File.Exists(escapeTarget), $"fixture is not proving anything: {escapeTarget} does not exist");

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

    [Theory]
    // The unescaping the host does exists FOR these — containment must not regress them.
    [InlineData("assets/my app.js")]
    [InlineData("assets/日本語.js")]
    [InlineData("nested/deep/file.css")]
    public void File_mode_still_serves_legitimate_paths_including_spaces_and_cjk(string virtualPath)
    {
        using var temp = TempDir.Create();
        temp.WriteFile(virtualPath.Replace('/', Path.DirectorySeparatorChar), "served");

        var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = Assembly.GetExecutingAssembly(),
            ResourcePrefix = "Shenora.Tests.NoSuchPrefix",
            FileFallbackDirectory = temp.Root,
            PreferFiles = true,
        });

        Assert.True(provider.Exists(virtualPath));
        Assert.Equal("served", ReadAll(provider.GetResourceStream(virtualPath)));
    }

    [Fact]
    public void A_sibling_directory_sharing_the_root_prefix_is_not_inside_the_root()
    {
        // "…/bundle-evil" must not pass as a child of "…/bundle" — the prefix check appends the
        // separator for exactly this case.
        using var temp = TempDir.Create("bundle", "bundle-evil");
        var root = temp.Combine("bundle");
        var leaked = temp.WriteFile(Path.Combine("bundle-evil", "secret.txt"), "escaped");
        Assert.True(File.Exists(leaked)); // precondition: there IS something to leak

        Assert.Null(EmbeddedResourceProvider.ResolveContained(root, "../bundle-evil/secret.txt"));
    }

    /// <summary>
    /// 🔴 The reason `BeginWarmup` is on the INTERFACE. The startup call used to be
    /// `(GetRequiredService&lt;IWebViewResourceProvider&gt;() as EmbeddedResourceProvider)?.BeginWarmup()`
    /// — with no `else`, so an app that registered its OWN provider got no warmup and no diagnostic.
    /// That is the same shape as the `dispatcher is MessageDispatcher` defect this repo records, which
    /// "silently dropped three whole modules". A custom provider must be reachable through the contract.
    /// </summary>
    [Fact]
    public void A_CUSTOM_provider_is_warmed_through_the_interface()
    {
        IWebViewResourceProvider custom = new WarmupRecordingProvider();

        custom.BeginWarmup();

        Assert.True(((WarmupRecordingProvider)custom).Warmed);
    }

    /// <summary>A provider with nothing to warm inherits the default body and is not forced to write one.</summary>
    [Fact]
    public void A_provider_that_does_not_override_BeginWarmup_still_compiles_and_is_a_no_op()
    {
        IWebViewResourceProvider minimal = new MinimalProvider();

        minimal.BeginWarmup();   // the default interface member — must not throw
    }

    private sealed class WarmupRecordingProvider : IWebViewResourceProvider
    {
        public bool Warmed { get; private set; }

        public Stream? GetResourceStream(string virtualPath) => null;
        public bool Exists(string virtualPath) => false;
        public void BeginWarmup() => Warmed = true;
    }

    private sealed class MinimalProvider : IWebViewResourceProvider
    {
        public Stream? GetResourceStream(string virtualPath) => null;
        public bool Exists(string virtualPath) => false;
    }
}
