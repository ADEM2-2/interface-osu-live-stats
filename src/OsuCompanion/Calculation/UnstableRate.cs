namespace OsuCompanion.Calculation;

public static class UnstableRate
{
    public static double Compute(IReadOnlyList<double> hitErrors)
    {
        if (hitErrors is null || hitErrors.Count == 0)
        {
            return 0;
        }

        double sum = 0;
        double sumSquares = 0;

        foreach (var error in hitErrors)
        {
            sum += error;
            sumSquares += error * error;
        }

        var mean = sum / hitErrors.Count;
        var variance = (sumSquares / hitErrors.Count) - (mean * mean);

        if (variance < 0)
        {
            variance = 0;
        }

        return 10.0 * Math.Sqrt(variance);
    }
}
