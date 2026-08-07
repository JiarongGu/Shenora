using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core;

namespace Shenora.IO;

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

        // Defaulted AFTER configure, so an explicit value always wins.
        options.Journal ??= new FileUpdateJournal(new FileUpdateJournalOptions
        {
            Directory = builder.Paths.DataArea("journal"),
            Log = options.Log,
        });
        options.Locker ??= new FilePathLocker(new FilePathLockerOptions
        {
            // ⚠ NOT inside the tree being managed. A sidecar lock in the app's content directory gets
            // synced, committed and outlives the process — the trap D31 records.
            LockDirectory = builder.Paths.DataArea("locks"),
        });

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<IFileUpdateQueue>(services =>
            new FileUpdateQueue(services.GetRequiredService<FileUpdateQueueOptions>()));

        return builder;
    }
}
