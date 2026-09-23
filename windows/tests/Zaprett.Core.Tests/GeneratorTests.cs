using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Platform;
using Zaprett.Core.Store;
using Zaprett.Core.Strategy;
using Zaprett.Core.Tests.Support;
using Zaprett.Core.Util;

namespace Zaprett.Core.Tests;

/// <summary>What the router conformance does not cover: the Windows base options, the path rules and the dry-run.</summary>
public sealed class GeneratorTests
{
    static ZaprettConfig Cfg(JsonObject? main = null) => ConfigLoader.Normalize(new JsonObject { ["main"] = main?.DeepClone() ?? new JsonObject() });

    static StoreItem UserItem(string engine) =>
        new("user-t", Engines.ItemType(engine), "user-t", "", "", "", null, null, [], "", "user", null, null, null, null, null);

    static GenerateResult Text(TestPaths p, string text, JsonObject? main = null, string engine = "winws", IReadOnlyList<string>? nlm = null) =>
        new ArgsGenerator(p, new ItemStore(p)).Build(Cfg(main), new GenerateOptions { Engine = engine, Text = text, Item = UserItem(engine), NlmNetworks = nlm });

    [Fact]
    public void Winws_BaseOptions_FromPorts()
    {
        using var p = new TestPaths();
        var g = new ArgsGenerator(p, new ItemStore(p)).Build(Cfg(), new GenerateOptions { Strategy = "strategy-general" });
        Assert.True(g.Ok, g.Message);
        Assert.Equal("--wf-l3=ipv4", g.Args[0]);
        Assert.Equal("--wf-tcp=" + string.Join(',', g.TcpPorts), g.Args[1]);
        Assert.Equal("--wf-udp=" + string.Join(',', g.UdpPorts), g.Args[2]);
        Assert.Equal(g.StrategyArgs, g.Args.Skip(3).ToList());
        Assert.DoesNotContain(g.Args, a => a.StartsWith("--qnum", StringComparison.Ordinal) || a.StartsWith("--user", StringComparison.Ordinal));
        Assert.Contains(g.Args, a => a == "--hostlist=" + Path.Combine(p.DataDir, "guard", "hostlist-guard.txt"));
    }

    [Fact]
    public void Debug_Ipv6_Filters()
    {
        using var p = new TestPaths();
        var main = new JsonObject
        {
            ["debug"] = true, ["ipv6"] = true,
            ["network_filter"] = new JsonObject { ["mode"] = "ssids", ["ssids"] = new JsonArray("Home", "Dacha"), ["skip_corporate"] = true },
        };
        var g = Text(p, "--filter-tcp=443 ${hostlists} --dpi-desync=fake", main, nlm: ["Home net", "{guid}"]);
        Assert.True(g.Ok, g.Message);
        Assert.Equal("--debug=@" + Path.Combine(p.RunDir, "engine-debug.log"), g.Args[0]);
        Assert.Equal("--wf-l3=ipv4,ipv6", g.Args[1]);
        Assert.Equal("--wf-tcp=443", g.Args[2]);
        Assert.DoesNotContain(g.Args, a => a.StartsWith("--wf-udp", StringComparison.Ordinal));
        Assert.Contains("--ssid-filter=Home,Dacha", g.Args);
        Assert.Contains("--nlm-filter=Home net,{guid}", g.Args);
        var none = Text(p, "--filter-tcp=443 ${hostlists}", main);
        Assert.DoesNotContain(none.Args, a => a.StartsWith("--nlm-filter", StringComparison.Ordinal));
    }

    [Fact]
    public void Winws2_LoadsBaseLua_UnlessStrategyHasItsOwn()
    {
        using var p = new TestPaths();
        var g = new ArgsGenerator(p, new ItemStore(p)).Build(Cfg(), new GenerateOptions { Engine = "winws2", Strategy = "z2-general" });
        Assert.True(g.Ok, g.Message);
        var lua = g.Args.Where(a => a.StartsWith("--lua-init=", StringComparison.Ordinal)).ToList();
        Assert.Equal(["zapret-lib.lua", "zapret-antidpi.lua", "zapret-auto.lua"], lua.Select(a => Path.GetFileName(a)).ToList());
        Assert.All(lua, a => Assert.StartsWith("--lua-init=@" + Path.Combine(p.Engine2Dir, "lua"), a));
        // a path typed into a strategy must not contain spaces (the text is split on whitespace); placeholders expand later
        var own = Text(p, "--lua-init=@" + Path.Combine(p.DataDir, "installed", "files", "lua", "mine.lua") + " --filter-tcp=443 ${hostlists} --lua-desync=multisplit",
            engine: "winws2");
        Assert.True(own.Ok, own.Message);
        Assert.Single(own.Args, a => a.StartsWith("--lua-init=", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsInterceptionOptions_AreReserved()
    {
        using var p = new TestPaths();
        var g = Text(p, "--wf-raw=true --wf-tcp=1-65535 --ssid-filter=x --filter-tcp=443 ${hostlists} --dpi-desync=fake");
        Assert.True(g.Ok);
        Assert.Contains("strategy_option_ignored", g.Warnings);
        Assert.Equal("[\"wf-raw\",\"wf-tcp\",\"ssid-filter\"]", g.Details["ignored_options"]!.ToJsonString());
        Assert.Equal("--wf-tcp=443", g.Args[1]);
    }

    [Theory]
    [InlineData("--dpi-desync-fake-tls=C:\\Windows\\win.ini")]
    [InlineData("--dpi-desync-fake-tls=\\\\server\\share\\x.bin")]
    [InlineData("--dpi-desync-fake-tls=${zaprettdir}\\..\\..\\x.bin")]
    [InlineData("--dpi-desync-fake-tls=relative.bin")]
    [InlineData("--blob=noval")]
    [InlineData("--lua-init=print(1)")]
    [InlineData("--hostlist-auto=C:\\auto.txt")]
    public void ForbiddenPaths(string opt)
    {
        using var p = new TestPaths();
        var g = Text(p, "--filter-tcp=443 ${hostlists} " + opt);
        Assert.False(g.Ok);
        Assert.Contains(g.Error, new[] { "path_not_allowed", "bad_option" });
    }

    [Fact]
    public void AllowedPaths()
    {
        using var p = new TestPaths();
        var ok = Text(p, "--filter-tcp=443 ${hostlists} --dpi-desync-fake-tls=${zaprettdir}/bin/tls_clienthello_www_google_com.bin " +
            "--dpi-desync-fake-quic=0x0101 --hostlist-auto=" + Path.Combine(p.RunDir, "autohostlist", "a.txt") +
            " --dpi-desync-fake-tls=! --dpi-desync-split-seqovl-pattern=+3@" + Path.Combine(p.DataDir, "installed", "files", "bin", "x.bin"));
        Assert.True(ok.Ok, ok.Message);
        Assert.Contains(ok.Args, a => a == "--dpi-desync-fake-tls=" + Path.Combine(p.BundleDir, "files", "bin", "tls_clienthello_www_google_com.bin"));
        // --lua-init only from a Lua directory, even inside the bundle
        var lua = Text(p, "--lua-init=@" + Path.Combine(p.BundleDir, "files", "bin", "x.bin") + " --filter-tcp=443 ${hostlists}", engine: "winws2");
        Assert.Equal("path_not_allowed", lua.Error);
        var lua2 = Text(p, "--lua-init=@" + Path.Combine(p.DataDir, "installed", "files", "lua", "l.lua") + " --filter-tcp=443 ${hostlists}", engine: "winws2");
        Assert.True(lua2.Ok, lua2.Message);
    }

    [Fact]
    public void Errors()
    {
        using var p = new TestPaths();
        var gen = new ArgsGenerator(p, new ItemStore(p));
        Assert.Equal("no_strategy", gen.Build(Cfg(new JsonObject { ["strategy"] = "" }), null).Error);
        Assert.Equal("no_strategy", gen.Build(Cfg(), new GenerateOptions { Engine = "winws2" }).Error);
        Assert.Equal("strategy_not_found", gen.Build(Cfg(new JsonObject { ["strategy"] = "nope" }), null).Error);
        Assert.Equal("item_not_found", gen.Build(Cfg(new JsonObject { ["lists"] = new JsonArray("missing-list") }), null).Error);
        var src = gen.Build(Cfg(new JsonObject { ["lists"] = new JsonArray("src-never") }), null);
        Assert.True(src.Ok);
        Assert.Contains("source_not_downloaded", src.Warnings);
        Assert.Equal("dependency_not_declared", gen.Build(Cfg(), new GenerateOptions
        {
            Text = "--filter-tcp=443 --dpi-desync-fake-tls=${bin:tls_clienthello_vk_com}",
            Item = new StoreItem("s", "nfqws", "s", "", "", "", null, null, ["quic_initial_www_google_com"], "", "repo", null, null, null, null, null),
        }).Error);
        var big = Path.Combine(p.UserDir, "strategies", "winws");
        Directory.CreateDirectory(big);
        File.WriteAllText(Path.Combine(big, "user-big.txt"), new string('a', 70000));
        Assert.Equal("strategy_unreadable", gen.Build(Cfg(new JsonObject { ["strategy"] = "user-big" }), null).Error);
    }

    [Fact]
    public void GameFilter_NeedsBin()
    {
        using var p = new TestPaths();
        var main = new JsonObject { ["game_filter"] = true, ["ipsets"] = new JsonArray("zaprett-telegram-ipset") };
        var g = Text(p, "--filter-tcp=443 ${hostlists} --lua-desync=fake", main, "winws2");
        Assert.True(g.Ok, g.Message);
        Assert.Contains(g.StrategyArgs, a => a.StartsWith("--lua-desync=multisplit:pos=1:seqovl=568", StringComparison.Ordinal));
        Assert.Contains("1024-65535", g.TcpPorts);
        Assert.Contains("wide_port_range", g.Warnings);
    }

    [Fact]
    public async Task DryRun_UsesEngineSpecificFlag_AndDropsDebug()
    {
        using var fp = new FakePlatform();
        var gen = new ArgsGenerator(fp.Paths, new ItemStore(fp.Paths));
        var r = await gen.DryRunAsync(fp.Processes, "winws", ["--debug=@x", "--wf-tcp=443"], CancellationToken.None);
        Assert.Equal(0, r.Rc);
        Assert.True(fp.Processes.Calls.TryDequeue(out var call));
        Assert.EndsWith("winws.exe", call.File);
        Assert.Equal(["--dry-run", "--wf-tcp=443"], call.Args.Take(2));
        Assert.StartsWith("--wf-save=" + Path.Combine(fp.Paths.RunDir, "dryrun-filter-"), call.Args[2]);
        Assert.Empty(Directory.GetFiles(fp.Paths.RunDir, "dryrun-filter-*"));
        await gen.DryRunAsync(fp.Processes, "winws2", ["--x"], CancellationToken.None);
        fp.Processes.Calls.TryDequeue(out call);
        Assert.Equal("--intercept=0", call.Args[0]);
        fp.Processes.Handler = (_, _) => new ProcessResult(1, "github version v72\n", "bad option --x\n", false);
        var bad = await gen.DryRunAsync(fp.Processes, "winws", ["--x"], CancellationToken.None);
        Assert.Equal(1, bad.Rc);
        Assert.Equal("bad option --x", bad.Output);
        fp.Processes.Handler = (_, _) => new ProcessResult(0, "", "", true);
        Assert.Equal(-1, (await gen.DryRunAsync(fp.Processes, "winws", [], CancellationToken.None)).Rc);
        File.Delete(Path.Combine(fp.Paths.EngineDir, "winws.exe"));
        Assert.True((await gen.DryRunAsync(fp.Processes, "winws", [], CancellationToken.None)).Missing);
    }

    [Fact]
    public void EnsureFiles_CreatesUserListsAndGuards()
    {
        using var p = new TestPaths();
        var gen = new ArgsGenerator(p, new ItemStore(p));
        gen.EnsureFiles(Cfg(), ["--hostlist-auto=" + Path.Combine(p.RunDir, "autohostlist", "a.txt")]);
        Assert.True(File.Exists(Path.Combine(p.UserDir, "hosts-include.txt")));
        Assert.True(File.Exists(Path.Combine(p.UserDir, "ipset-exclude.txt")));
        Assert.True(Directory.Exists(Path.Combine(p.RunDir, "autohostlist")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(RepoPaths.GuardDir, "hostlist-guard.txt")),
            File.ReadAllBytes(Path.Combine(p.DataDir, "guard", "hostlist-guard.txt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(RepoPaths.GuardDir, "ipset-guard.txt")),
            File.ReadAllBytes(Path.Combine(p.DataDir, "guard", "ipset-guard.txt")));
        File.WriteAllText(Path.Combine(p.DataDir, "guard", "ipset-guard.txt"), "tampered");
        Guard.GuardFiles.Ensure(p);
        Assert.Equal(Guard.GuardFiles.IpsetContent, File.ReadAllText(Path.Combine(p.DataDir, "guard", "ipset-guard.txt")));
    }

    [Fact]
    public void Winws2_InterceptsOutgoingPorts()
    {
        using var p = new TestPaths();
        var g = Text(p, "--filter-tcp=443 ${hostlists} --lua-desync=multisplit --new --filter-udp=443 ${hostlists} --lua-desync=fake", engine: "winws2");
        Assert.True(g.Ok, g.Message);
        Assert.Contains("--wf-tcp-out=443", g.Args);
        Assert.Contains("--wf-udp-out=443", g.Args);
        Assert.DoesNotContain(g.Args, a => a.StartsWith("--wf-tcp=", StringComparison.Ordinal));
    }

    [Fact]
    public void AbbreviatedOptions_AreResolvedBeforeChecks()
    {
        using var p = new TestPaths();
        // getopt_long_only accepts unambiguous prefixes: they must not slip past the reserved and file checks
        var dbg = Text(p, @"--debu=@C:\Windows\x.txt --filter-tcp=443 ${hostlists} --dpi-desync=fake");
        Assert.True(dbg.Ok, dbg.Message);
        Assert.Contains("strategy_option_ignored", dbg.Warnings);
        Assert.DoesNotContain(dbg.Args, a => a.Contains("x.txt", StringComparison.Ordinal));
        Assert.Equal("path_not_allowed", Text(p, @"--filter-tcp=443 ${hostlists} --dpi-desync-fake-q=C:\Windows\win.ini").Error);
        Assert.Equal("bad_option", Text(p, @"--filter-tcp=443 ${hostlists} --dpi-desync-fake-t=C:\Windows\win.ini").Error);
        Assert.Equal("path_not_allowed", Text(p, @"-hostlist-auto=C:\auto.txt --filter-tcp=443").Error);
        // a required argument without "=" takes the next word, as getopt does: the path is checked all the same
        Assert.Equal("path_not_allowed", Text(p, @"--filter-tcp=443 --hostlist-auto C:\auto.txt").Error);
        Assert.Equal("bad_option", Text(p, "--filter-tcp=443 ${hostlists} --dpi-desync-fake=x").Error);
        Assert.Equal("bad_option", Text(p, "--filter-tcp=443 ${hostlists} --no-such-option=1").Error);
        Assert.Equal("bad_option", Text(p, "--filter-tcp=443 ${hostlists} --lua-desync=x").Error);
        var full = Text(p, "--filter-t=443 ${hostlists} --dpi-desync-r=6");
        Assert.True(full.Ok, full.Message);
        Assert.Contains("--filter-tcp=443", full.StrategyArgs);
        Assert.Contains("--dpi-desync-repeats=6", full.StrategyArgs);
        Assert.Equal(["443"], full.TcpPorts);
        Assert.Equal("dpi-desync-fake-tls", Strategy.EngineOptions.Resolve("dpi-desync-fake-tls", Strategy.EngineOptions.Known("winws")).Name);
        Assert.Null(Strategy.EngineOptions.Resolve("wf-tcp", Strategy.EngineOptions.Known("winws2")).Name);
        Assert.Null(Strategy.EngineOptions.Resolve("", Strategy.EngineOptions.Known("winws")).Name);
    }

    [Theory]
    // a required argument written without "=" takes the next word, even one that starts with "-"
    [InlineData("winws", "--dpi-desync fake --filter-tcp 443", "--dpi-desync=fake --filter-tcp=443")]
    [InlineData("winws", "--dpi-desync-fake-tls --dpi-desync-fake-quic=0x01", "--dpi-desync-fake-tls=--dpi-desync-fake-quic=0x01")]
    [InlineData("winws", "-filter-t 80 --dpi-desync-any-protocol --dpi-desync-autottl=2", "--filter-tcp=80 --dpi-desync-any-protocol --dpi-desync-autottl=2")]
    [InlineData("winws", "--new --skip", "--new --skip")]
    [InlineData("winws2", "--new=first --lua-desync fake", "--new=first --lua-desync=fake")]
    public void Canonicalize_TakesValuesLikeGetopt(string engine, string input, string output)
    {
        var (t, err) = Strategy.EngineOptions.Canonicalize(input.Split(' '), engine);
        Assert.Null(err);
        Assert.Equal(output, string.Join(' ', t!));
    }

    [Theory]
    [InlineData("winws", "--new=x")]
    [InlineData("winws", "--skip=1")]
    [InlineData("winws", "--filter-tcp")]
    [InlineData("winws", "--hostlist ${hostlists}")]
    [InlineData("winws", "-")]
    [InlineData("winws", "--")]
    [InlineData("winws", "---x")]
    [InlineData("winws", "--=x")]
    [InlineData("winws", "fake")]
    [InlineData("winws", "${bin:x}")]
    [InlineData("winws", "--dpi-desync-fake")]
    [InlineData("winws", "--nope=1")]
    [InlineData("winws2", "--wf-tcp=443")]
    public void Canonicalize_Refuses(string engine, string input)
    {
        var (t, err) = Strategy.EngineOptions.Canonicalize(input.Split(' '), engine);
        Assert.Null(t);
        Assert.NotNull(err);
    }

    [Fact]
    public void RouterOnlyOptions_AreRemoved_GluedValuesAreChecked()
    {
        using var p = new TestPaths();
        // qnum, user and marks exist only in the Linux engine: removed with a warning, even when written without "="
        var g = Text(p, "--qnum 200 --user daemon --dpi-desync-fwmark 0x1 --filter-tcp=443 ${hostlists} --dpi-desync=fake");
        Assert.True(g.Ok, g.Message);
        Assert.Equal("[\"qnum\",\"user\",\"dpi-desync-fwmark\"]", g.Details["ignored_options"]!.ToJsonString());
        Assert.DoesNotContain(g.Args, a => a is "200" or "daemon" or "0x1");
        // the glued value of a file option goes through the file check (the engine would read that file)
        Assert.Equal("path_not_allowed", Text(p, @"--filter-tcp=443 ${hostlists} --dpi-desync-fake-tls C:\Windows\win.ini").Error);
        Assert.Equal("bad_option", Text(p, "--filter-tcp=443 ${hostlists} --skip=1").Error);
        var named = Text(p, "--filter-tcp=443 ${hostlists} --lua-desync=multisplit --new=second --filter-udp=443 ${hostlists} --lua-desync=fake",
            engine: "winws2");
        Assert.True(named.Ok, named.Message);
        Assert.Contains("--new=second", named.StrategyArgs);
        Assert.DoesNotContain("--new", named.StrategyArgs);
        Assert.Equal(["443"], named.UdpPorts);
        // --new=<name> starts a profile of its own: an unfiltered second profile is reported as profile 2
        var two = Text(p, "--filter-tcp=443 ${hostlists} --lua-desync=multisplit --new=second --filter-udp=443 --lua-desync=fake", engine: "winws2");
        Assert.True(two.Ok, two.Message);
        Assert.Contains("profile_unfiltered", two.Warnings);
        Assert.Equal(2L, R.Long(two.Details["unfiltered_profiles"]![0]!["profile"]));
        // options only the Linux engine knows, reserved or not, never reach winws
        var linux = Text(p, "--filter-ssid=home --bind-fix4 --filter-tcp=443 ${hostlists} --dpi-desync=fake");
        Assert.True(linux.Ok, linux.Message);
        Assert.Equal("[\"filter-ssid\",\"bind-fix4\"]", linux.Details["ignored_options"]!.ToJsonString());
        Assert.DoesNotContain(linux.Args, a => a.StartsWith("--filter-ssid", StringComparison.Ordinal) || a == "--bind-fix4");
    }

    [Fact]
    public void PureSteps()
    {
        var (t, d) = ArgsGenerator.Tokenize("--a \\\n\t--b --comment x y --c\r\n--d \\");
        Assert.Equal(["--a", "--b", "--c", "--d"], t);
        Assert.Equal(["--comment", "x", "y"], d);
        Assert.Equal(["--dpi-desync=fakedsplit,multidisorder,fake"], ArgsGenerator.NormalizeModes(["--dpi-desync=split,disorder2,fake"]));
        Assert.Equal(3, ArgsGenerator.SplitProfiles(["a", "--new", "b", "--new"]).Count);
        Assert.Equal(["a", "--new", "b"], ArgsGenerator.JoinProfiles([["a"], ["b"]]));
        Assert.Null(ArgsGenerator.ExtractPorts(["--filter-tcp=x"]));
        var ports = ArgsGenerator.ExtractPorts(["--filter-udp=~443", "--new", "--filter-tcp=443", "--skip"])!.Value;
        Assert.Empty(ports.Tcp);
        Assert.Single(ports.Udp);
    }
}
