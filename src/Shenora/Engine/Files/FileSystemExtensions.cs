using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Engine.Files;
using Shenora.Core.Shell;

namespace Shenora;

/// <summary>
/// Wiring the file-operation engine into an application.
/// </summary>
public static class FileSystemExtensions
{
    /// <summary>
    /// Register the file-update engine — a journalled queue with rollback, plus the cross-process path
    /// locks it needs.
    /// <code>
    /// builder.UseFileSystem();                                        // journal + locks under the app's data dir
    /// builder.UseFileSystem(x => x.LeaseTimeout = TimeSpan.FromMinutes(2));
    /// </code>
    /// <para>
    /// Defaults the journal and lock directories under <see cref="ShenoraApplicationBuilder.Paths"/>.
    /// </para>
    /// <para>
    /// ⚠ <b>The journal must sit on the SAME VOLUME as the files being mutated</b>, because rollback
    /// depends on renames being atomic. The default satisfies that for an app whose data and content share
    /// a root; an app writing to a different volume must say so.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Optional. Anything left unset is defaulted below.</param>
    public static ShenoraApplicationBuilder UseFileSystem(
        this ShenoraApplicationBuilder builder,
        Action<FileUpdateQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseFileSystem((options, _) => configure?.Invoke(options));
    }

    /// <summary>
    /// Configure the file queue AND substitute any of its collaborators, in one place — e.g. an
    /// <see cref="IFileLockInspector"/> or an <see cref="IPathLocker"/> of the app's own.
    /// <para>🔴 <b>YOUR registration wins</b>, whatever the call order.</para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Receives the options and the container, before the kit registers anything.</param>
    public static ShenoraApplicationBuilder UseFileSystem(
        this ShenoraApplicationBuilder builder,
        Action<FileUpdateQueueOptions, IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FileUpdateQueueOptions();
        configure(options, builder.Services);

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<IFileUpdateQueue>(provider =>
        {
            var paths = provider.GetRequiredService<ShenoraPaths>();
            // 🔴 THE PER-PLATFORM PIECE IS PULLED OUT OF DI AT BUILD TIME, NOT AT `Use…` TIME: the shell's
            // `UseWindows`/`UseAndroid`/`UseIOS` may run after this, and reading it earlier would silently
            // resolve nothing.
            //
            // ⚠ EVERY default below lands on `resolved`, never on the captured `options` — `TryAddSingleton`
            // no-ops when the app registered its own `FileUpdateQueueOptions`, and then the captured
            // instance is one nothing will ever read. Defaulting onto it would build a journal for the
            // wrong object and hand the queue one with none.
            var resolved = provider.GetRequiredService<FileUpdateQueueOptions>();
            resolved.LockInspector ??= provider.GetService<IFileLockInspector>();

            // 🔴 `Paths.DataArea` CREATES the directory it names, so the journal and locker are built
            // here and never at `Use…` time — registration must not touch a disk. Defaulted after
            // `configure`, so an explicit value wins.
            resolved.Journal ??= new FileUpdateJournal(new FileUpdateJournalOptions
            {
                Directory = paths.DataArea("journal"),
                Log = resolved.Log,
            });
            resolved.Locker ??= new FilePathLocker(new FilePathLockerOptions
            {
                // ⚠ NOT inside the tree being managed: a sidecar lock in the app's content directory gets
                // synced, committed and outlives the process (D31).
                LockDirectory = paths.DataArea("locks"),
            });

            return new FileUpdateQueue(resolved);
        });

        return builder;

    }
}
