using System;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using SimLab;

// SimLab (console): starts the lab and opens the page in the browser. With --cli it only writes pictures and numbers.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli") return LabServer.Cli(args);

        string url = Environment.GetEnvironmentVariable("SIMLAB_URL") ?? "http://localhost:5057";
        var app = LabServer.Create(url, false);
        app.Start();
        string address = LabServer.AddressOf(app);
        Console.WriteLine("Simulation lab: " + address + "  (Ctrl+C to stop)");
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); } catch { }
        app.WaitForShutdown();
        return 0;
    }
}
