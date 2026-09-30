using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Shell;
using Shenora.Engine.Files;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell registers who-holds-a-file, as the WinForms shell does, and the file engine finds it: unregistered,
/// a failed change could only answer "cannot tell".
/// </summary>
public class ChromiumFileLockInspectorTests
{
    [Fact]
    public void The_shell_registers_the_inspector_and_the_file_engine_takes_it()
    {
        using var root = TempDir.Create();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Inspector test",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Root },
        });
        builder.UseChromium(new ChromiumHostOptions());
        builder.UseFileSystem();
        using var app = builder.Build();

        var inspector = Assert.IsType<FileLockInspector>(app.Services.GetRequiredService<IFileLockInspector>());
        _ = app.Services.GetRequiredService<IFileUpdateQueue>();   // building the queue is what wires it
        Assert.Same(inspector, app.Services.GetRequiredService<FileUpdateQueueOptions>().LockInspector);
    }
}
