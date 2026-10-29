using System.Net;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            SelfTests.Run();
            return;
        }

        int port = 5000;
        if (args.Length > 0 && (!int.TryParse(args[0], out port) || port is < 1 or > 65535))
        {
            Console.Error.WriteLine("Usage: GTUTablesServer.Refactored [port: 1-65535]");
            Environment.ExitCode = 2;
            return;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        using var server = new GTUTablesServer(IPAddress.Any, port);
        await server.RunAsync(shutdown.Token);
    }
}
