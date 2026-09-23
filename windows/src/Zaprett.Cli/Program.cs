using System.Text;
using Zaprett.Cli;
using Zaprett.Ipc;

// zaprett.exe — command line client of the zaprett service (same syntax as the router CLI).
// ZAPRETT_PIPE selects another pipe name (development only).
// UTF-8 so that Russian and Chinese print correctly in cmd and PowerShell; the console's own code page is put back
// on exit (SetConsoleOutputCP outlives the process otherwise)
var consoleOut = Console.IsOutputRedirected ? null : Console.OutputEncoding;
Console.OutputEncoding = new UTF8Encoding(false);
string pipe = Environment.GetEnvironmentVariable("ZAPRETT_PIPE") is { Length: > 0 } p ? p : ZaprettPipeClient.DefaultPipeName;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
try
{
    return await CliApp.RunAsync(args, () => new ZaprettPipeClient(pipe), Console.OpenStandardInput(), Console.Out, Console.Error, cts.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception e)
{
    Console.Error.WriteLine("zaprett: " + e.Message);
    return CliApp.ExitFailed;
}
finally
{
    Console.Out.Flush();
    Console.Error.Flush();
    if (consoleOut is not null)
        Console.OutputEncoding = consoleOut;
}
