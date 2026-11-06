namespace LolHighlight.Desktop.Infrastructure;

public static class SliderValueScale
{
    public static double[] ParseStops(string stops) => stops.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : double.NaN)
        .Where(double.IsFinite).Distinct().Order().ToArray();

    public static int ClosestStop(double value, IReadOnlyList<double> stops)
    {
        var closest = 0;
        for (var i = 1; i < stops.Count; i++)
            if (Math.Abs(stops[i] - value) < Math.Abs(stops[closest] - value)) closest = i;
        return closest;
    }

    public static double StopAtPosition(double position, IReadOnlyList<double> stops)
    {
        if (stops.Count == 0) throw new ArgumentException("滑块没有可用档位", nameof(stops));
        return stops[Math.Clamp((int)Math.Round(position), 0, stops.Count - 1)];
    }

    public static double StepStop(double value, IReadOnlyList<double> stops, int direction)
    {
        if (stops.Count == 0) return value;
        return direction > 0
            ? stops.FirstOrDefault(stop => stop > value + 0.000001, stops[^1])
            : stops.LastOrDefault(stop => stop < value - 0.000001, stops[0]);
    }

    public static double ToPosition(double value, double minimum, double maximum, bool logarithmic)
    {
        if (maximum <= minimum) return 0;
        value = Math.Clamp(value, minimum, maximum);
        return logarithmic && minimum > 0
            ? Math.Log(value / minimum) / Math.Log(maximum / minimum) * 1000
            : (value - minimum) / (maximum - minimum) * 1000;
    }

    public static double FromPosition(double position, double minimum, double maximum, double step, bool logarithmic)
    {
        var fraction = Math.Clamp(position, 0, 1000) / 1000;
        var value = logarithmic && minimum > 0
            ? minimum * Math.Pow(maximum / minimum, fraction)
            : minimum + (maximum - minimum) * fraction;
        if (step > 0) value = minimum + Math.Round((value - minimum) / step) * step;
        return Math.Round(Math.Clamp(value, minimum, maximum), 6);
    }
}
