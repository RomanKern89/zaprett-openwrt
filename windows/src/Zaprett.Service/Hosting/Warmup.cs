using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Zaprett.Service.Hosting;

/// <summary>
/// zaprett-svc --warmup: the installer runs it (deferred, as SYSTEM) before StartServices. After a fresh install
/// Defender scans every file when it is first loaded, and the first service start lost 15-27 s to that before the SCM
/// handshake (D13). Here the same files are loaded ahead: the assemblies the service references (transitively), our own
/// code prepared method by method, and the engine files read once. Nothing is started or written (no service, no
/// ProgramData, no registry); the exit code is 0 whatever happens, within <see cref="Limit"/>.
/// </summary>
internal static class Warmup
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(25);
    private static readonly string[] Ours = ["zaprett-svc", "Zaprett.Core", "Zaprett.Ipc"];

    public static int Run(TextWriter output, TimeSpan? limit = null, string? baseDir = null)
    {
        var deadline = limit ?? Limit;
        var sw = Stopwatch.StartNew();
        int loaded = 0, prepared = 0, failed = 0, files = 0;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<AssemblyName>();
            queue.Enqueue(typeof(Warmup).Assembly.GetName());
            var ours = new List<Assembly>();
            while (queue.Count > 0 && sw.Elapsed < deadline)
            {
                var name = queue.Dequeue();
                if (name.Name is null || !seen.Add(name.Name))
                    continue;
                try
                {
                    var asm = Assembly.Load(name);
                    loaded++;
                    if (Ours.Contains(name.Name, StringComparer.OrdinalIgnoreCase))
                        ours.Add(asm);
                    foreach (var r in asm.GetReferencedAssemblies())
                        queue.Enqueue(r);
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    failed++;
                }
            }
            foreach (var asm in ours)
                prepared += Prepare(asm, sw, deadline, ref failed);
            files = ReadEngineFiles(baseDir ?? AppContext.BaseDirectory, sw, deadline);
        }
        catch (Exception e)
        {
            // the type only: the message may be localized, and the MSI log takes this output as OEM text
            output.WriteLine("zaprett-svc warmup: " + e.GetType().Name);
        }
        // one ASCII line, the same on every system language (the MSI log shows it as it is)
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"zaprett-svc warmup: {loaded} assemblies, {prepared} methods, {files} engine files, {failed} failed, {sw.Elapsed.TotalSeconds:0.0} s"));
        return 0;
    }

    /// <summary>Compiles (or binds the ready-to-run code of) the methods of our assemblies; no static constructor runs.</summary>
    internal static int Prepare(Assembly asm, Stopwatch sw, TimeSpan deadline, ref int failed)
    {
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t is not null).ToArray()!;
        }
        int n = 0;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
                                 BindingFlags.DeclaredOnly;
        foreach (var t in types)
        {
            if (t.ContainsGenericParameters)
                continue;
            foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                if (sw.Elapsed >= deadline)
                    return n;
                if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() is null)
                    continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(m.MethodHandle);
                    n++;
                }
                catch (Exception)
                {
                    // [UnmanagedCallersOnly], methods the runtime refuses to prepare: they load on first use as before
                    failed++;
                }
            }
        }
        return n;
    }

    /// <summary>The engine files are read once, so the scan of winws and WinDivert happens now, not at the first start.</summary>
    private static int ReadEngineFiles(string baseDir, Stopwatch sw, TimeSpan deadline)
    {
        int n = 0;
        foreach (var sub in new[] { "engine", "engine2" })
        {
            string dir = Path.Combine(baseDir, sub);
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (sw.Elapsed >= deadline)
                    return n;
                if (!f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !f.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    s.ReadByte();
                    n++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return n;
    }
}
