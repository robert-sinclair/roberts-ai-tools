using System.Reflection;

namespace rNotice.Probe;

/// <summary>
/// rNotice build 0.1 — a read-only check of what the app registration can actually reach.
///
/// It exists to answer one question before any real code is written: of the five delegated
/// permissions requested, which were consented to? Everything after this depends on the answer,
/// and guessing it would waste the build.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            PrintHelp();
            return 0;
        }

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.1.0";

        Console.WriteLine();
        Console.WriteLine($"  rNotice probe {version.Split('+')[0]}");
        Console.WriteLine($"  Tenant    {AppConfig.Tenant}");
        Console.WriteLine($"  Client ID {AppConfig.ClientId}");
        Console.WriteLine();
        Console.WriteLine("  Read-only. This build writes nothing to your mailbox, calendar or OneDrive.");
        Console.WriteLine();

        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        IReadOnlyList<CheckResult> results;
        try
        {
            results = await new Probe().RunAsync(cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  Cancelled.");
            return 2;
        }

        PrintResults(results);

        var failed = results.Count(r => !r.Succeeded);
        var signInFailed = results.Any(r => r.Name == "Sign-in" && !r.Succeeded);

        Console.WriteLine();
        if (signInFailed)
        {
            Console.WriteLine("  Sign-in failed, so nothing else could be checked.");
            Console.WriteLine("  See the Troubleshooting section of rNotice/README.md.");
        }
        else if (failed == 0)
        {
            Console.WriteLine("  All checks passed. Every requested permission is usable.");
        }
        else
        {
            Console.WriteLine($"  {results.Count - failed} of {results.Count} checks passed.");
            Console.WriteLine("  A failed check usually means that permission was not consented to.");
            Console.WriteLine("  That is a result, not a bug — send the table above to IT.");
        }

        Console.WriteLine();
        if (!Console.IsOutputRedirected && Environment.UserInteractive)
        {
            Console.WriteLine("  Press any key to close.");
            Console.ReadKey(intercept: true);
        }

        return failed == 0 ? 0 : 1;
    }

    private static void PrintResults(IReadOnlyList<CheckResult> results)
    {
        var nameWidth = Math.Max(5, results.Max(r => r.Name.Length));
        var scopeWidth = Math.Max(5, results.Max(r => r.Scope.Length));

        Console.WriteLine($"  {"",-2}{"Check".PadRight(nameWidth)}  {"Permission".PadRight(scopeWidth)}  Result");
        Console.WriteLine($"  {new string('─', 2 + nameWidth + 2 + scopeWidth + 2 + 40)}");

        foreach (var r in results)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = r.Succeeded ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"  {(r.Succeeded ? "✓ " : "✗ ")}");
            Console.ForegroundColor = previous;
            Console.WriteLine($"{r.Name.PadRight(nameWidth)}  {r.Scope.PadRight(scopeWidth)}  {r.Detail}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  rNotice probe — checks which Microsoft Graph permissions are usable.");
        Console.WriteLine();
        Console.WriteLine("  Usage: rNotice.Probe.exe [--help]");
        Console.WriteLine();
        Console.WriteLine("  Environment overrides:");
        Console.WriteLine("    RNOTICE_CLIENT_ID        Application (client) ID");
        Console.WriteLine("    RNOTICE_TENANT           Tenant domain or ID");
        Console.WriteLine("    RNOTICE_WORKBOOK_FOLDER  OneDrive folder to list (default: efficiency)");
        Console.WriteLine();
        Console.WriteLine("  Exit codes: 0 all passed, 1 one or more failed, 2 cancelled.");
        Console.WriteLine();
    }
}
