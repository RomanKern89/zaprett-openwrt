using System.Runtime.InteropServices;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Tests;

public partial class CommandLineTests
{
    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgv(string commandLine, out int argc);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);

    /// <summary>What Windows itself makes of a command line.</summary>
    internal static string[] Split(string commandLine)
    {
        nint argv = CommandLineToArgv(commandLine, out int argc);
        Assert.NotEqual(0, argv);
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    public static readonly string[] Fixed =
    [
        "", " ", "a", "a b", "a\tb", "\"", "\"\"", "a\"b", "a\\b", "a\\\\b", "a\\", "a\\\\", "a\\\"b", "a\\\\\"b", "\\\"",
        "\\", "\\\\", "\\\\\\", "C:\\Program Files\\zaprett\\bundle\\files\\tls_clienthello.bin",
        "--dpi-desync-fake-tls=C:\\Program Files\\zaprett\\bundle\\files\\x.bin", "--hostlist=C:\\ProgramData\\zaprett\\run\\a b.txt",
        "C:\\Пользователи\\Иван\\список.txt", "--dpi-desync=fake,multisplit", "--wf-tcp=80,443", "--wf-udp=443,50000-50100",
        "--filter-tcp=80 <HOSTLIST>", "*.youtube.com", "a?b", "[x]", "{a,b}", "it's", "'quoted'", "\"quoted\"", "  lead",
        "trail  ", "tab\t", "new\nline", "\\\\server\\share\\", "C:\\dir with space\\", "%PATH%", "^&|<>", "--debug=@C:\\x y\\log.txt",
        "é ü ß 中文 😀", "a\\\\\\\"b", "end\\\\\\",
    ];

    public static IEnumerable<object[]> FixedCases() => Fixed.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(FixedCases))]
    public void SingleArgument_RoundTripsThroughCommandLineToArgvW(string arg)
    {
        string cl = WindowsCommandLine.Build(@"C:\Program Files\zaprett\engine\winws.exe", [arg]);
        var argv = Split(cl);
        Assert.Equal(2, argv.Length);
        Assert.Equal(@"C:\Program Files\zaprett\engine\winws.exe", argv[0]);
        Assert.Equal(arg, argv[1]);
    }

    [Fact]
    public void AllFixed_TogetherRoundTrip()
    {
        var argv = Split(WindowsCommandLine.Build(@"C:\x\winws.exe", Fixed));
        Assert.Equal(Fixed, argv[1..]);
    }

    [Fact]
    public void RandomArguments_RoundTrip()
    {
        const string alphabet = "ab \t\"\\\\\\'*?[]{}=,:;.-_/яЖё€😀\n";
        var rnd = new Random(20260923);
        int checkedCount = 0;
        for (int round = 0; round < 300; round++)
        {
            var args = new string[rnd.Next(1, 6)];
            for (int i = 0; i < args.Length; i++)
            {
                var chars = new char[rnd.Next(0, 12)];
                for (int j = 0; j < chars.Length; j++)
                    chars[j] = alphabet[rnd.Next(alphabet.Length)];
                // never split a surrogate pair of the emoji
                args[i] = new string(chars).Replace("\uD83D", "").Replace("\uDE00", "");
            }
            var argv = Split(WindowsCommandLine.Build(@"C:\x\winws.exe", args));
            Assert.Equal(args, argv[1..]);
            checkedCount += args.Length;
        }
        Assert.True(checkedCount >= 300);
    }

    [Theory]
    [InlineData("--wf-tcp=80,443", "--wf-tcp=80,443")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\\b", "a\\b")]
    [InlineData("a b\\", "\"a b\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("*.com", "\"*.com\"")]
    [InlineData("it's", "\"it's\"")]
    public void Quoting_OnlyWhenNeeded(string arg, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(arg));

    [Fact]
    public void BadInput_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a\"b.exe", []));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("", []));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a.exe", ["x\0y"]));
    }
}
