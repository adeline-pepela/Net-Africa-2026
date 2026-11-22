namespace Jozi.TravelAgent.Evals;

internal sealed class CliOptions(Dictionary<string, string> values)
{
    public static CliOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            values[args[i][2..]] = hasValue ? args[++i] : "true";
        }
        return new CliOptions(values);
    }

    public string Get(string name, string fallback) => values.GetValueOrDefault(name, fallback);
    public string? Get(string name) => values.GetValueOrDefault(name);
    public int GetInt(string name, int fallback) => values.TryGetValue(name, out var v) && int.TryParse(v, out var i) ? i : fallback;
    public double GetDouble(string name, double fallback) =>
        values.TryGetValue(name, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
