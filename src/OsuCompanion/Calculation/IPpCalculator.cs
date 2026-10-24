namespace OsuCompanion.Calculation;

public interface IPpCalculator
{
    double CalculatePp(PlaySnapshot play);
}

public sealed class PlaySnapshot
{
    public string? BeatmapPath { get; set; }

    public int MapId { get; set; }

    public uint Mods { get; set; }

    public int Combo { get; set; }

    public int MaxCombo { get; set; }

    public int Misses { get; set; }

    public int Hit300 { get; set; }

    public int Hit100 { get; set; }

    public int Hit50 { get; set; }

    public double Accuracy { get; set; }
}

public sealed class NullPpCalculator : IPpCalculator
{
    public double CalculatePp(PlaySnapshot play)
    {
        return 0;
    }
}

public sealed class FallbackPpCalculator : IPpCalculator
{
    private readonly IPpCalculator _primary;
    private readonly IPpCalculator _fallback;

    public FallbackPpCalculator(IPpCalculator primary, IPpCalculator fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public double CalculatePp(PlaySnapshot play)
    {
        var primary = _primary.CalculatePp(play);
        return primary > 0 ? primary : _fallback.CalculatePp(play);
    }
}
