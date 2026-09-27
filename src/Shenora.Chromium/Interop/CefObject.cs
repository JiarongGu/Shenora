using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Interop;

/// <summary>
/// A CEF struct the SHELL implements: an app, a handler, a delegate. CEF owns its lifetime through the
/// struct's own reference count, so the managed object lives exactly as long as someone holds a reference,
/// and the last <c>release</c> frees both.
/// <para>
/// The native block is <c>[header: GCHandle][the CEF struct]</c>. The header sits BEFORE the struct because
/// CEF reads the struct's <c>size</c> to decide which members exist, and that must be the CEF struct's own
/// size, never a wrapper's. A callback reaches its managed object from <c>self</c> through the header.
/// </para>
/// <para>
/// CEF's rules this encodes (<c>chromiumembedded.github.io/cef/using_the_capi.html</c>): a struct handed TO
/// CEF, as an argument or as a getter's return value, must carry a reference CEF can take, which is
/// <see cref="ForCef"/>. The creator holds one reference from construction and gives it up with
/// <see cref="Release"/>.
/// </para>
/// </summary>
internal abstract unsafe class CefObject
{
    /// <summary>16, so the struct after it keeps the alignment the allocation gives the block.</summary>
    private const int HeaderSize = 16;

    private readonly byte* _block;
    private int _references = 1;

    /// <param name="structSize">The CEF struct's size, which is what its base's <c>size</c> must say.</param>
    private protected CefObject(int structSize)
    {
        _block = (byte*)NativeMemory.AlignedAlloc((nuint)(HeaderSize + structSize), HeaderSize);
        NativeMemory.Clear(_block, (nuint)(HeaderSize + structSize));
        // A strong handle: while CEF holds references, nothing else may be keeping this object alive.
        *(nint*)_block = GCHandle.ToIntPtr(GCHandle.Alloc(this));

        // Every ref-counted CEF struct starts with its base at offset 0, at every level of derivation.
        var b = (_cef_base_ref_counted_t*)(_block + HeaderSize);
        b->size = (nuint)structSize;
        b->add_ref = &CefObjectCallbacks.AddRef;
        b->release = &CefObjectCallbacks.Release;
        b->has_one_ref = &CefObjectCallbacks.HasOneRef;
        b->has_at_least_one_ref = &CefObjectCallbacks.HasAtLeastOneRef;
    }

    /// <summary>The struct CEF sees. Valid until the last reference is released.</summary>
    private protected void* Native => _block + HeaderSize;

    /// <summary>References currently held, by CEF and by the creator together.</summary>
    internal int References => Volatile.Read(ref _references);

    /// <summary>
    /// Add a reference and return the struct, for handing it to CEF: CEF takes ownership of one reference
    /// for every struct it is given, and a getter it calls must return one too.
    /// </summary>
    internal void* ForCef()
    {
        AddRefCore();
        return Native;
    }

    /// <summary>Give up the creator's reference. The object is freed once CEF has released its own too.</summary>
    internal void Release() => ReleaseCore();

    /// <summary>The object this struct belongs to. <paramref name="self"/> must be a struct this type allocated.</summary>
    internal static T From<T>(void* self) where T : CefObject =>
        (T)GCHandle.FromIntPtr(*(nint*)((byte*)self - HeaderSize)).Target!;

    /// <summary>Called once, after the last reference is gone and before the memory is freed.</summary>
    private protected virtual void OnFreed() { }

    internal void AddRefCore() => Interlocked.Increment(ref _references);

    /// <returns>True when that was the last reference and the object was freed.</returns>
    internal bool ReleaseCore()
    {
        var left = Interlocked.Decrement(ref _references);
        if (left > 0) return false;
        if (left < 0) throw new InvalidOperationException($"{GetType().Name} was released more times than it was referenced.");

        OnFreed();
        var handle = GCHandle.FromIntPtr(*(nint*)_block);
        NativeMemory.AlignedFree(_block);
        handle.Free();
        return true;
    }
}

/// <summary>A <see cref="CefObject"/> whose struct is <typeparamref name="TStruct"/>.</summary>
internal abstract unsafe class CefObject<TStruct> : CefObject where TStruct : unmanaged
{
    private protected CefObject() : base(sizeof(TStruct)) { }

    /// <summary>The struct, for filling in the callbacks this object implements.</summary>
    private protected TStruct* Struct => (TStruct*)Native;

    /// <summary><see cref="CefObject.ForCef"/>, typed.</summary>
    internal new TStruct* ForCef() => (TStruct*)base.ForCef();
}

/// <summary>
/// The four base callbacks every <see cref="CefObject"/> shares. Static and non-generic, because an
/// <see cref="UnmanagedCallersOnlyAttribute"/> method may be neither generic nor in a generic type.
/// <para>
/// 🔴 An exception must never unwind into CEF's native frames. Here one can only mean a broken reference
/// count (a release past zero, or a throwing <c>OnFreed</c>), which leaves memory in an unknown state, so
/// the process ends with the reason rather than continuing on it.
/// </para>
/// </summary>
internal static unsafe class CefObjectCallbacks
{
    [UnmanagedCallersOnly]
    internal static void AddRef(_cef_base_ref_counted_t* self)
    {
        try { CefObject.From<CefObject>(self).AddRefCore(); }
        catch (Exception ex) { Fail(nameof(AddRef), ex); }
    }

    [UnmanagedCallersOnly]
    internal static int Release(_cef_base_ref_counted_t* self)
    {
        try { return CefObject.From<CefObject>(self).ReleaseCore() ? 1 : 0; }
        catch (Exception ex) { Fail(nameof(Release), ex); return 0; }
    }

    [UnmanagedCallersOnly]
    internal static int HasOneRef(_cef_base_ref_counted_t* self) => CefObject.From<CefObject>(self).References == 1 ? 1 : 0;

    [UnmanagedCallersOnly]
    internal static int HasAtLeastOneRef(_cef_base_ref_counted_t* self) => CefObject.From<CefObject>(self).References >= 1 ? 1 : 0;

    private static void Fail(string callback, Exception ex) =>
        Environment.FailFast($"Shenora.Chromium: a CEF reference count broke in {callback} ({ex.GetType().Name}: {ex.Message}).", ex);
}

/// <summary>
/// A reference to a struct CEF implements. It is released on <see cref="Dispose"/>, because a struct CEF
/// RETURNS, or passes INTO a callback (other than <c>self</c>), carries a reference that must be released.
/// </summary>
internal readonly unsafe struct CefRef<T> : IDisposable where T : unmanaged
{
    /// <summary>The struct, or null.</summary>
    public readonly T* Ptr;

    /// <summary>Take ownership of the reference <paramref name="ptr"/> already carries.</summary>
    public CefRef(T* ptr) => Ptr = ptr;

    /// <summary>True when there is no struct.</summary>
    public bool IsNull => Ptr == null;

    /// <summary>Add a reference for passing the struct INTO a CEF call, which consumes one; ours stays.</summary>
    public T* ForCef()
    {
        if (Ptr != null) Base->add_ref(Base);
        return Ptr;
    }

    /// <summary>Release the reference this holds.</summary>
    public void Dispose()
    {
        if (Ptr != null) Base->release(Base);
    }

    private _cef_base_ref_counted_t* Base => (_cef_base_ref_counted_t*)Ptr;
}
