using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OsuCompanion.Calculation;

public sealed class OppaiNgPpCalculator : IPpCalculator
{
    private readonly string _oppaiPath;
    private readonly BeatmapLocator _beatmaps;

    public OppaiNgPpCalculator(string oppaiPath, BeatmapLocator beatmaps)
    {
        _oppaiPath = oppaiPath;
        _beatmaps = beatmaps ?? throw new ArgumentNullException(nameof(beatmaps));
    }

    public double CalculatePp(PlaySnapshot play)
    {
        if (string.IsNullOrWhiteSpace(_oppaiPath) || !File.Exists(_oppaiPath))
        {
            return 0;
        }

        var map = play.BeatmapPath ?? _beatmaps.Find(play.MapId);
        if (string.IsNullOrWhiteSpace(map) || !File.Exists(map))
        {
            return 0;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _oppaiPath,
                    Arguments = BuildArguments(map, play),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return ParsePp(output);
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildArguments(string map, PlaySnapshot play)
    {
        var mods = play.Mods != 0 ? "+" + FormatMods(play.Mods) : string.Empty;
        return $"{mods} {play.Combo}x {play.Misses}m {play.Hit300} {play.Hit100} {play.Hit50} \"{map}\" -ojson"
            .Trim();
    }

    private static string FormatMods(uint mods)
    {
        var result = string.Empty;

        if ((mods & 0x0002) != 0) result += "EZ";
        if ((mods & 0x0008) != 0) result += "HD";
        if ((mods & 0x0010) != 0) result += "HR";
        if ((mods & 0x0020) != 0) result += "SD";
        if ((mods & 0x0040) != 0) result += "DT";
        if ((mods & 0x0080) != 0) result += "RX";
        if ((mods & 0x0100) != 0) result += "HT";
        if ((mods & 0x0200) != 0) result += "NC";
        if ((mods & 0x0400) != 0) result += "FL";
        if ((mods & 0x4000) != 0) result += "PF";

        return result;
    }

    private static double ParsePp(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("pp", out var pp))
            {
                return pp.GetDouble();
            }
        }
        catch
        {
        }

        foreach (var token in output.Split(' ', '\t', '\r', '\n'))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }
}
