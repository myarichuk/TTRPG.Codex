using System.Reflection;
using System.Runtime.Loader;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>
/// One load context per folder of DLLs, for the DLLs it holds now (overhaul steps 9 and 10): a data system's compiled suppliers
/// (<see cref="Rules.CompiledSuppliers"/>) and the compiled plugins directly in a content root (<see cref="PluginLoader"/>).
/// <list type="bullet">
/// <item><b>Same DLLs, same context.</b> The context is keyed by the folder and by the name and hash of every DLL in it, so a
/// reload that finds the same files gets the same types, and one that finds a DLL added, removed or changed gets a new context.
/// Assemblies are read from their bytes, so the files are never locked and can be replaced while the server runs.</item>
/// <item><b>Shared with the host.</b> An assembly the host has (Codex.Plugin.Abstractions above all, whose interfaces must be
/// the host's types) comes from the host; the rest from the folder, by file name.</item>
/// <item><b>Unloaded when nothing uses it.</b> A replaced context is retired, not unloaded at once: a table that started under the
/// old system keeps its instances, and an unloading context cannot resolve the host assemblies a method JIT-compiles against,
/// so the table's next call could fail. Every instance a context makes goes through <see cref="FolderLoadContext.Create"/>,
/// which keeps a weak reference to it; <see cref="Sweep"/> unloads a retired context once all of them have been collected,
/// when nothing can call into it any more. Each load sweeps, and so does a table that ends.</item>
/// </list>
/// </summary>
public static class FolderLoadContexts
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, FolderLoadContext> Current = new(StringComparer.Ordinal);
    private static readonly List<FolderLoadContext> Retired = new();

    /// <summary>The context for the DLLs <paramref name="folder"/> holds now, held (never unloaded) until the returned handle is
    /// disposed: long enough to load from it and make what it is for, after which those instances keep it.</summary>
    public static IDisposable Hold(string folder, out FolderLoadContext context)
    {
        folder = Path.GetFullPath(folder);
        var stamp = string.Join(";", Directory.GetFiles(folder, "*.dll").Order(StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + "=" + SystemFingerprint.Hash(f)));
        lock (Gate)
        {
            if (!Current.TryGetValue(folder, out var existing) || existing.Stamp != stamp)
            {
                if (existing != null) Retired.Add(existing);
                Current[folder] = new FolderLoadContext(folder, stamp);
            }

            context = Current[folder];
            var hold = context.Hold();
            SweepLocked();
            return hold;
        }
    }

    /// <summary>Retires the context of every folder under <paramref name="roots"/> but those in <paramref name="inUse"/>: after a
    /// reload, those of systems that are gone. They are unloaded once nothing they made is alive. Folders outside the roots are
    /// another loader's, or a check's, and are left alone.</summary>
    public static void RetireUnder(IEnumerable<string> roots, IEnumerable<string> inUse)
    {
        var under = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToList();
        var keep = inUse.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).ToHashSet(StringComparer.Ordinal);
        lock (Gate)
        {
            foreach (var folder in Current.Keys.Where(f => !keep.Contains(f) && under.Any(r => f == r || f.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal))).ToList())
            {
                Retired.Add(Current[folder]);
                Current.Remove(folder);
            }

            SweepLocked();
        }
    }

    /// <summary>Unloads every retired context none of whose instances is alive; returns how many it unloaded. The memory goes at
    /// the garbage collector's next pass after that.</summary>
    public static int Sweep()
    {
        lock (Gate) return SweepLocked();
    }

    /// <summary>How many replaced contexts are still in memory, waiting for what they made to be collected.</summary>
    public static int RetiredCount
    {
        get
        {
            lock (Gate) return Retired.Count;
        }
    }

    private static int SweepLocked()
    {
        var unloaded = 0;
        foreach (var context in Retired.Where(c => !c.InUse).ToList())
        {
            Retired.Remove(context);
            context.Unload();
            unloaded++;
        }

        return unloaded;
    }
}

/// <summary>One folder's assemblies: collectible, read from bytes, the host's from the host. See <see cref="FolderLoadContexts"/>.</summary>
public sealed class FolderLoadContext : AssemblyLoadContext
{
    private readonly object _gate = new();
    private readonly List<WeakReference> _made = new();
    private int _holds;

    internal FolderLoadContext(string folder, string stamp) : base("codex " + folder, isCollectible: true)
    {
        Folder = folder;
        Stamp = stamp;
    }

    public string Folder { get; }

    /// <summary>The DLLs' names and hashes when this context was made.</summary>
    public string Stamp { get; }

    /// <summary>The folder's own assembly <paramref name="name"/> from <paramref name="file"/>, or null when the name is the
    /// host's (a system may not name a host type, and a host DLL in a content root is not a plugin).</summary>
    public Assembly? Main(string name, string file)
    {
        if (Host(new AssemblyName(name)) != null) return null;
        lock (_gate)
        {
            return Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) ?? FromBytes(file);
        }
    }

    /// <summary>An instance of <paramref name="type"/>, one of this context's, made with its parameterless constructor. The
    /// context stays loaded while it is alive.</summary>
    public object Create(Type type)
    {
        var instance = Activator.CreateInstance(type)!;
        lock (_gate) _made.Add(new WeakReference(instance));
        return instance;
    }

    /// <summary>Whether something this context made is still alive, or someone is loading from it.</summary>
    internal bool InUse
    {
        get
        {
            lock (_gate)
            {
                _made.RemoveAll(w => !w.IsAlive);
                return _made.Count > 0 || _holds > 0;
            }
        }
    }

    internal IDisposable Hold()
    {
        lock (_gate) _holds++;
        return new Release(this);
    }

    private sealed class Release(FolderLoadContext context) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (context._gate) context._holds--;
        }
    }

    private Assembly FromBytes(string file)
    {
        using var bytes = new MemoryStream(File.ReadAllBytes(file));
        return LoadFromStream(bytes);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (Host(name) is { } shared) return shared;
        var file = Path.Combine(Folder, name.Name + ".dll");
        return File.Exists(file) ? FromBytes(file) : null;
    }

    private static Assembly? Host(AssemblyName name)
    {
        try
        {
            return Default.LoadFromAssemblyName(new AssemblyName(name.Name!));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}
