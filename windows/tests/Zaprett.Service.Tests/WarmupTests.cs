using System.Diagnostics;
using System.Text.RegularExpressions;
using Zaprett.Service.Hosting;

namespace Zaprett.Service.Tests;

/// <summary>zaprett-svc --warmup (the installer runs it before StartServices): loads, prepares, reads; writes nothing,
/// exits 0.</summary>
public partial class WarmupTests
{
    [GeneratedRegex(@"warmup: (\d+) assemblies, (\d+) methods, (\d+) engine files, (\d+) failed")]
    private static partial Regex Summary();

    private static (int Assemblies, int Methods, int Files) Parse(string output)
    {
        var m = Summary().Match(output);
        Assert.True(m.Success, output);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    [Fact]
    public void LoadsOurAssemblies_PreparesOurCode_ReadsTheEngine_WritesNothing()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "engine"));
        File.WriteAllText(Path.Combine(dir.Path, "engine", "winws.exe"), "MZ");
        File.WriteAllText(Path.Combine(dir.Path, "engine", "notes.txt"), "not read");
        var before = Directory.GetFileSystemEntries(dir.Path, "*", SearchOption.AllDirectories).Order().ToList();
        var output = new StringWriter();
        var sw = Stopwatch.StartNew();
        Assert.Equal(0, Warmup.Run(output, baseDir: dir.Path));
        Assert.True(sw.Elapsed < Warmup.Limit + TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        var (assemblies, methods, files) = Parse(output.ToString());
        // zaprett-svc, Zaprett.Core, Zaprett.Ipc and the hosting libraries they reference
        Assert.True(assemblies >= 10, output.ToString());
        Assert.True(methods > 100, output.ToString());
        Assert.Equal(1, files);
        // one ASCII line with a dot as the decimal mark, whatever the culture (the MSI log takes it as OEM text)
        string text = output.ToString();
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.All(text, ch => Assert.True(ch < 128, $"non-ASCII {(int)ch} in: {text}"));
        Assert.Matches(@" \d+\.\d s", text);
        Assert.Equal(before, Directory.GetFileSystemEntries(dir.Path, "*", SearchOption.AllDirectories).Order().ToList());
    }

    // the limit holds inside the preparation too (the installer must not wait more than about 25 s)
    [Fact]
    public void Prepare_StopsAtTheLimit()
    {
        int failed = 0;
        Assert.Equal(0, Warmup.Prepare(typeof(Warmup).Assembly, Stopwatch.StartNew(), TimeSpan.Zero, ref failed));
        Assert.True(Warmup.Prepare(typeof(Warmup).Assembly, Stopwatch.StartNew(), TimeSpan.FromSeconds(25), ref failed) > 100);
    }

    // negative control: no time at all and no install folder: nothing done, still exit code 0 (the installer goes on)
    [Fact]
    public void NoTime_NoFolder_StillExitsZero()
    {
        var output = new StringWriter();
        Assert.Equal(0, Warmup.Run(output, TimeSpan.Zero, Path.Combine(Path.GetTempPath(), "zaprett-missing-" + Guid.NewGuid().ToString("N"))));
        var (_, methods, files) = Parse(output.ToString());
        Assert.Equal(0, methods);
        Assert.Equal(0, files);
    }
}
