using System.Text.Json;
using OsuCompanion.Diagnostics;
using OsuCompanion.Memory;
using OsuCompanion.Network;
using OsuCompanion.ProcessManagement;

namespace OsuCompanion;

internal static class Program
{
    private const int DefaultPort = 24051;

    public static async Task Main(string[] args)
    {
        var port = ParsePort(args);
        var pointerConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "pointers.json");
        var overlayRoot = Path.Combine(AppContext.BaseDirectory, "overlay");

        var config = LoadConfig(pointerConfigPath);
        config.Normalize();
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        if (args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
        {
            await RunProbe(config, args, cts.Token).ConfigureAwait(false);
            return;
        }

        Console.WriteLine("OsuCompanion");
        Console.WriteLine($"Overlay dashboard: http://localhost:{port}/");
        Console.WriteLine("Waiting for osu!.exe ...");

        await using var server = new GameplayWebSocketServer(port, overlayRoot);

        try
        {
            var serverTask = server.StartAsync(cts.Token);
            var reader = new GameplayReader(config);
            SafeMemoryReader? memory = null;
            var lastStatus = string.Empty;

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (memory is null || !memory.IsOpen)
                    {
                        memory?.Dispose();
                        memory = null;

                        var process = OsuProcessLocator.Find();
                        if (process is null)
                        {
                            await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        memory = new SafeMemoryReader(process);
                        if (!memory.Open())
                        {
                            Console.WriteLine($"Could not open osu!.exe (PID {process.Id}) - retrying.");
                            memory.Dispose();
                            memory = null;
                            await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        reader.Attach(memory);
                        Console.WriteLine($"Attached to osu!.exe (PID {process.Id}).");
                        new MemoryDiagnostics(memory, config).RunSummary();
                    }

                    if (HasExited(memory))
                    {
                        memory.Dispose();
                        memory = null;
                        reader.Detach();
                        Console.WriteLine("osu!.exe exited - waiting for restart.");
                        await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    var data = reader.Read();
                    server.UpdateSnapshot(data);

                    if (data.Status != lastStatus)
                    {
                        lastStatus = data.Status;
                        Console.WriteLine(data.Status switch
                        {
                            "playing" => $"In a map - streaming live stats (score {data.Score}, combo {data.Combo}).",
                            "no_player" => "Waiting for a map (player object not live yet).",
                            _ => $"Status: {data.Status}",
                        });
                    }

                    await Task.Delay(16, cts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                memory?.Dispose();
            }

            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
        }

        Console.WriteLine("Stopped.");
    }

    private static async Task RunProbe(PointerConfig config, string[] args, CancellationToken token)
    {
        Console.WriteLine("Signature probe - verifying config against the live game.");
        Console.WriteLine($"Config: {Path.Combine(AppContext.BaseDirectory, "config", "pointers.json")}");

        while (!token.IsCancellationRequested)
        {
            var process = OsuProcessLocator.Find();
            if (process is null)
            {
                Console.WriteLine("Waiting for osu!.exe ...");
                await Task.Delay(1000, token).ConfigureAwait(false);
                continue;
            }

            using var memory = new SafeMemoryReader(process);
            if (!memory.Open())
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                continue;
            }

            new MemoryDiagnostics(memory, config).Run();

            if (args.Any(a => string.Equals(a, "--sigprobe", StringComparison.OrdinalIgnoreCase)))
            {
                new MemoryDiagnostics(memory, config).ProbeOnly();
                return;
            }

            if (args.Any(a => string.Equals(a, "--watch", StringComparison.OrdinalIgnoreCase)))
            {
                new MemoryDiagnostics(memory, config).RunWatch(token);
            }

            return;
        }
    }

    private static bool HasExited(SafeMemoryReader memory)
    {
        try
        {
            return memory.Process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static PointerConfig LoadConfig(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PointerConfig>(json)
               ?? throw new InvalidOperationException($"Could not parse {path}.");
    }

    private static int ParsePort(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed))
            {
                return parsed;
            }
        }

        return DefaultPort;
    }
}
