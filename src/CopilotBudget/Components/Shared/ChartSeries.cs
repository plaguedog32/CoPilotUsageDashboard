namespace CopilotBudget.Components.Shared;

/// <summary>One line in a <see cref="LineChart"/>. A null value is a month with no data and leaves a gap.</summary>
public record ChartSeries(string Name, string Color, IReadOnlyList<decimal?> Values);

/// <summary>Shared chart helpers: categorical colors and clean axis ticks.</summary>
public static class ChartScale
{
    /// <summary>
    /// The validated categorical palette, dark-mode steps, in fixed order. Colors follow the entity
    /// (an org keeps its slot however the view is filtered), so callers index by a stable position.
    /// </summary>
    public static readonly string[] Categorical =
        ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

    /// <summary>Neutral color for an "Other" bucket once the categorical slots run out.</summary>
    public const string Other = "#8f8e86";

    /// <summary>A round axis maximum and step, e.g. 437 → (500, 125).</summary>
    public static (decimal Max, decimal Step) NiceAxis(decimal max, int ticks = 4)
    {
        if (max <= 0) return (ticks, 1);
        var raw = (double)max / ticks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * magnitude).First(s => s >= raw);
        return ((decimal)(step * ticks), (decimal)step);
    }

    /// <summary>Compact tick labels: 950, 1.2K, 12K.</summary>
    public static string Compact(decimal value) => value switch
    {
        >= 10_000 => $"{value / 1000:0}K",
        >= 1_000 => $"{value / 1000:0.#}K",
        _ => Services.Format.Amount(value),
    };
}
