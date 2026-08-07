using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora;

namespace Shenora.Engine.Files;

/// <summary>
/// Wiring the file-operation engine into an application.
/// </summary>
public static class FileSystemExtensions
{
    /// <summary>
    /// Register the file-update engine — a journalled queue with rollback, plus the cross-process path
    /// locks it needs. **One call, and the app has crash-atomic file mutation.**
    /// <code>
    /// builder.UseFileSystem();                                        // journal + locks under the app's data dir
    /// builder.UseFileSystem(x => x.LeaseTimeout = TimeSpan.FromMinutes(2));
    /// </code>
    /// <para>
    /// <b>It exists because the price of entry was wrong.</b> Before this, an adopter wrote the wiring by
    /// hand — a <c>FileUpdateQueue</c> holding a <c>FileUpdateJournal</c> holding a
    /// <c>FileUpdateJournalOptions</c> holding a directory they had to choose — three nested constructors
    /// to get the default behaviour. That is the same tax <c>UseMediaPlayer</c> removed on the media side,
    /// and an adopter should meet both capabilities the same way.
    /// </para>
    /// <para>
    /// <b>What it defaults, and why it can.</b> The journal and lock directories go under
    /// <see cref="ShenoraApplicationBuilder.Paths"/> — the app's OWN storage, so choosing them for you
    /// decides nothing the app cares about. ⚠ Note the contrast with <c>UseMediaPlayer</c>, where
    /// <c>AllowedRoots</c> could NOT be defaulted because it is a containment boundary. The test is the
    /// same each time: **may the kit make this choice on the app's behalf without changing what the app is
    /// exposed to?** Here, yes.
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

        var options = new FileUpdateQueueOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<IFileUpdateQueue>(provider =>
        {
            // ⚠ Paths from DI rather than the captured builder: the app's storage layout is a registered
            // service (the builder adds it in its CONSTRUCTOR), so the factory needs nothing closed over
            // and this reads the same whether it was called by an app or by the D64 default in Build().
            var paths = provider.GetRequiredService<ShenoraPaths>();
            // 🔴 PULL THE PER-PLATFORM PIECE OUT OF DI, at build time rather than at Use time — the shell's
            // `UseWindows`/`UseMobile` may run after this. This is the file system's version of what
            // `IMediaCapability` and `IMediaAudioConversion` are for media: the ENGINE is portable, the
            // answers are not. "Who holds this file open?" is Restart Manager on Windows and something else
            // everywhere else.
            //
            // ⚠ Registering a capability is not the same as CONSULTING it, and this repo has now paid for
            // that twice (D59, and RestartManagerLockInspector going unregistered entirely). Whenever the
            // kit says "supply an implementation and we will use it", something must actually ask.
            //
            // ⚠ EVERY default below lands on `resolved`, never on the captured `options` — `TryAddSingleton`
            // no-ops when the app registered its own `FileUpdateQueueOptions`, and then the captured
            // instance is one nothing will ever read. Defaulting onto it would build a journal for the
            // wrong object and hand the queue one with none.
            var resolved = provider.GetRequiredService<FileUpdateQueueOptions>();
            resolved.LockInspector ??= provider.GetService<IFileLockInspector>();

            // 🔴 THE JOURNAL AND LOCKER ARE BUILT HERE, NOT AT `Use…` TIME, and that is what lets this
            // engine be registered by DEFAULT (D64). `Paths.DataArea` CREATES the directory it names, so
            // constructing them eagerly meant every app got a `journal/` and a `locks/` folder whether or
            // not it ever mutated a file — the cost that made an "on by default" framework impossible.
            // Registration is free; only RESOLVING this touches a disk, and nothing resolves it until the
            // app asks for a file mutation. Still defaulted after `configure`, so an explicit value wins.
            resolved.Journal ??= new FileUpdateJournal(new FileUpdateJournalOptions
            {
                Directory = paths.DataArea("journal"),
                Log = resolved.Log,
            });
            resolved.Locker ??= new FilePathLocker(new FilePathLockerOptions
            {
                // ⚠ NOT inside the tree being managed. A sidecar lock in the app's content directory gets
                // synced, committed and outlives the process — the trap D31 records.
                LockDirectory = paths.DataArea("locks"),
            });

            return new FileUpdateQueue(resolved);
        });

        return builder;

    }
}
