using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Shenora.Tests.Chromium;

/// <summary>
/// <c>ShenoraChromiumCodecs</c> (D99) through the REAL targets file: the opt-in fetches the kit's build, pinned by its own
/// SHA-1, into a cache of its own, and says so when the kit has published none. The download is a local server's junk, so
/// what is proven is which file is asked for, from where, and against which pin; extracting a real build is the sample's
/// (<c>dev.mjs chromium-codecs</c>).
/// </summary>
public class ChromiumCodecsFetchTests
{
    private const string File_ = "cef_binary_test_windows64_minimal.tar.bz2";

    [Fact]
    public void Opting_in_with_no_build_published_for_the_platform_says_so()
    {
        var root = NewRoot();
        try
        {
            var output = Fetch(root, codecs: true, pin: false, source: null);
            Assert.Contains("the kit has published none for windows64", output);
            Assert.DoesNotContain("spotifycdn", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Opting_in_asks_the_codecs_source_for_the_pinned_file_and_checks_the_pin()
    {
        var root = NewRoot();
        using var server = Server.Start();
        try
        {
            var output = Fetch(root, codecs: true, pin: true, source: server.Origin);
            Assert.Equal(["/" + File_], server.Requested);
            Assert.Contains("failed its SHA-1 check", output);
            // Its own cache folder, beside the standard build's and never in it.
            Assert.EndsWith("-codecs", Directory.GetDirectories(Path.Combine(root, "cache")).Single(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Without_the_opt_in_the_codecs_pin_is_never_read()
    {
        var root = NewRoot();
        using var server = Server.Start();
        try
        {
            // The standard pin has no windows64 file in this probe, so the build stops before any download.
            var output = Fetch(root, codecs: false, pin: true, source: server.Origin, standardPin: false);
            Assert.Empty(server.Requested);
            Assert.Contains("names no distribution for windows64", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Fetch(string root, bool codecs, bool pin, string? source, bool standardPin = false)
    {
        var shim = Path.Combine(root, "shim.dll");
        File.WriteAllText(shim, "shim");
        var project = Path.Combine(root, "probe.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                <AssemblyName>MyApp.App</AssemblyName>
                <ShenoraChromiumShim>{shim}</ShenoraChromiumShim>
                <ShenoraCefCache>{Path.Combine(root, "cache")}</ShenoraCefCache>
                <ShenoraChromiumCodecs>{(codecs ? "true" : "false")}</ShenoraChromiumCodecs>
                {(source is null ? "" : $"<ShenoraCefCodecsSource>{source}</ShenoraCefCodecsSource>")}
              </PropertyGroup>
              <Import Project="{TargetsFile()}" />
              <!-- The real pins are replaced: the probe names its own files. -->
              <ItemGroup>
                <ShenoraCefDistribution Remove="@(ShenoraCefDistribution)" />
                <ShenoraCefCodecsDistribution Remove="@(ShenoraCefCodecsDistribution)" />
                {(standardPin ? $"<ShenoraCefDistribution Include=\"windows64\" File=\"{File_}\" Sha1=\"{new string('0', 40)}\" />" : "")}
                {(pin ? $"<ShenoraCefCodecsDistribution Include=\"windows64\" File=\"{File_}\" Sha1=\"{new string('1', 40)}\" />" : "")}
              </ItemGroup>
            </Project>
            """);
        return Run("dotnet", ["msbuild", project, "-t:ShenoraChromiumFetchCef", "-nologo", "-noAutoResponse", "-v:minimal"]);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "codecs-fetch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    // The build is expected to FAIL in every case here, and what it says is the assertion.
    private static string Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), "the build did not finish in 3 minutes");
        Assert.NotEqual(0, process.ExitCode);
        return stdout.Result + stderr.Result;
    }

    private static string TargetsFile()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly."),
            "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets");
    }

    /// <summary>Answers every request with a few junk bytes and remembers the paths asked for.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _requested = [];
        public string Origin { get; private init; } = "";
        public string[] Requested { get { lock (_requested) return [.. _requested]; } }

        public static Server Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = new Server { Origin = $"http://localhost:{port}/" };
            server._listener.Prefixes.Add(server.Origin);
            server._listener.Start();
            _ = Task.Run(server.ServeAsync);
            return server;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                lock (_requested) _requested.Add(context.Request.Url!.AbsolutePath);
                await context.Response.OutputStream.WriteAsync("junk"u8.ToArray());
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}
