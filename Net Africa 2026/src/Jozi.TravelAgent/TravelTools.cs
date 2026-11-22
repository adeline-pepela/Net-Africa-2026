using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jozi.TravelAgent;

public sealed record ToolDefinition(string Name, string DefaultDescription, string InputSchemaJson, Func<JsonElement, object> Handler);

/// <summary>
/// Four deterministic, fake travel tools. Fixed data means the golden dataset can assert exact answers
/// (27°C, R1,450, 18.30 ZAR/USD), so Correctness scores only move when the agent's behaviour moves.
/// All airlines, flight numbers, fares and forecasts are made-up sample data.
/// </summary>
public static class TravelTools
{
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new("get_weather_forecast",
            "Get the weather forecast for a city on a specific date. Returns high and low temperature in Celsius, conditions and chance of rain.",
            """
            {"type":"object","properties":{
              "city":{"type":"string","description":"City name, e.g. Johannesburg"},
              "date":{"type":"string","description":"Date in YYYY-MM-DD format"}},
             "required":["city","date"]}
            """,
            GetWeather),

        new("search_flights",
            "Search one-way flights between two cities on a date. Returns flight number, airline, departure and arrival times, stops and fare in South African rand (ZAR).",
            """
            {"type":"object","properties":{
              "origin":{"type":"string","description":"Departure city"},
              "destination":{"type":"string","description":"Arrival city"},
              "date":{"type":"string","description":"Travel date in YYYY-MM-DD format"}},
             "required":["origin","destination","date"]}
            """,
            SearchFlights),

        new("convert_currency",
            "Convert an amount between currencies using today's exchange rate. Supports ZAR, USD, EUR, GBP, KES and NGN.",
            """
            {"type":"object","properties":{
              "amount":{"type":"number"},
              "from_currency":{"type":"string","description":"ISO code, e.g. USD"},
              "to_currency":{"type":"string","description":"ISO code, e.g. ZAR"}},
             "required":["amount","from_currency","to_currency"]}
            """,
            ConvertCurrency),

        new("get_visa_requirements",
            "Look up whether a passport holder needs a visa to visit a destination country, the maximum stay and how to apply.",
            """
            {"type":"object","properties":{
              "passport_country":{"type":"string","description":"Country that issued the passport"},
              "destination_country":{"type":"string"}},
             "required":["passport_country","destination_country"]}
            """,
            GetVisaRequirements),
    ];

    public static (string ResultJson, bool Ok) Execute(string toolName, string argumentsJson)
    {
        var tool = All.FirstOrDefault(t => t.Name == toolName);
        if (tool is null) return (Error($"Unknown tool '{toolName}'"), false);
        try
        {
            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var result = tool.Handler(args.RootElement);
            var json = JsonSerializer.Serialize(result);
            return (json, !json.Contains("\"error\""));
        }
        catch (Exception ex)
        {
            return (Error(ex.Message), false);
        }
    }

    // ---------------------------------------------------------------- weather
    private static readonly Dictionary<string, (int High, int Low, string Conditions, int Rain)> Weather = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Johannesburg"] = (27, 15, "Afternoon thunderstorms", 60),
        ["Cape Town"] = (24, 14, "Sunny and windy", 5),
        ["Durban"] = (28, 21, "Humid and partly cloudy", 30),
        ["Nairobi"] = (25, 13, "Mostly sunny", 20),
        ["Lagos"] = (31, 24, "Scattered showers", 55),
        ["London"] = (11, 6, "Overcast with light rain", 70),
    };

    private static object GetWeather(JsonElement args)
    {
        var city = Cities.Normalize(Str(args, "city"));
        var date = Str(args, "date");
        if (!Weather.TryGetValue(city, out var w)) return new { error = $"No forecast data for '{city}'" };
        return new { city, date, high_c = w.High, low_c = w.Low, conditions = w.Conditions, chance_of_rain_pct = w.Rain };
    }

    // ---------------------------------------------------------------- flights
    private sealed record Flight(string flight, string airline, string departs, string arrives, int stops, int fare_zar);

    private static readonly Dictionary<(string, string), Flight[]> Flights = new()
    {
        [("Cape Town", "Johannesburg")] =
        [
            new("HV245", "Highveld Air", "06:30", "08:35", 0, 1450),
            new("PA312", "Protea Airways", "09:00", "11:05", 0, 2180),
            new("BX822", "Baobab Express", "17:15", "19:20", 0, 1690),
        ],
        [("Lagos", "Johannesburg")] =
        [
            new("PA061", "Protea Airways", "10:05", "17:40", 0, 9870),
            new("BX533", "Baobab Express", "07:10", "18:55", 1, 8420),
        ],
        [("Nairobi", "Johannesburg")] =
        [
            new("BX762", "Baobab Express", "07:25", "10:45", 0, 5300),
            new("PA185", "Protea Airways", "13:40", "17:00", 0, 6150),
        ],
        [("London", "Johannesburg")] =
        [
            new("PA055", "Protea Airways", "19:45", "07:55+1", 0, 14900),
            new("HV449", "Highveld Air", "21:10", "09:30+1", 0, 13750),
        ],
    };

    private static object SearchFlights(JsonElement args)
    {
        var origin = Cities.Normalize(Str(args, "origin"));
        var destination = Cities.Normalize(Str(args, "destination"));
        var date = Str(args, "date");
        if (!Flights.TryGetValue((origin, destination), out var flights))
            return new { error = $"No flights found from {origin} to {destination}" };
        return new { origin, destination, date, currency = "ZAR", flights };
    }

    // ---------------------------------------------------------------- currency
    private static readonly Dictionary<string, decimal> PerUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1m, ["ZAR"] = 18.30m, ["EUR"] = 0.92m, ["GBP"] = 0.79m, ["KES"] = 129.00m, ["NGN"] = 1550.00m,
    };

    private static object ConvertCurrency(JsonElement args)
    {
        var amount = args.GetProperty("amount").GetDecimal();
        var from = Str(args, "from_currency").ToUpperInvariant();
        var to = Str(args, "to_currency").ToUpperInvariant();
        if (!PerUsd.TryGetValue(from, out var fromRate) || !PerUsd.TryGetValue(to, out var toRate))
            return new { error = $"Unsupported currency pair {from}->{to}" };
        var rate = Math.Round(toRate / fromRate, 6);
        var converted = Math.Round(amount / fromRate * toRate, 2);
        return new { amount, from_currency = from, to_currency = to, rate, converted_amount = converted };
    }

    // ---------------------------------------------------------------- visas
    private static readonly Dictionary<(string, string), object> Visas = new()
    {
        [("Kenya", "South Africa")] = new { visa_required = false, max_stay_days = 90, notes = "Visa-free for tourism and conferences. Passport must be valid 30 days beyond departure." },
        [("United Kingdom", "South Africa")] = new { visa_required = false, max_stay_days = 90, notes = "Visa-free on arrival." },
        [("Nigeria", "South Africa")] = new { visa_required = true, max_stay_days = 90, notes = "Apply for a visitor visa through the VFS Global centre at least 4 weeks before travel. Include the conference invitation letter." },
        [("India", "South Africa")] = new { visa_required = true, max_stay_days = 90, notes = "Apply online for an eVisa at least 10 working days before travel." },
        [("South Africa", "Kenya")] = new { visa_required = false, max_stay_days = 90, notes = "Visa-free; register on the eTA portal before travel." },
    };

    private static object GetVisaRequirements(JsonElement args)
    {
        var passport = Countries.Normalize(Str(args, "passport_country"));
        var destination = Countries.Normalize(Str(args, "destination_country"));
        return Visas.TryGetValue((passport, destination), out var info)
            ? new { passport_country = passport, destination_country = destination, requirements = info }
            : new { error = $"No visa data for {passport} passport holders visiting {destination}" };
    }

    // ---------------------------------------------------------------- helpers
    private static string Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : v.ToString() : "";

    private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();

    private static class Cities
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["JNB"] = "Johannesburg", ["Joburg"] = "Johannesburg", ["Jozi"] = "Johannesburg", ["Johannesburg, South Africa"] = "Johannesburg",
            ["CPT"] = "Cape Town", ["Cape Town, South Africa"] = "Cape Town",
            ["DUR"] = "Durban", ["LOS"] = "Lagos", ["Lagos, Nigeria"] = "Lagos",
            ["NBO"] = "Nairobi", ["Nairobi, Kenya"] = "Nairobi",
            ["LHR"] = "London", ["London, UK"] = "London",
        };

        public static string Normalize(string city) =>
            Aliases.TryGetValue(city, out var canonical) ? canonical : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(city.ToLowerInvariant());
    }

    private static class Countries
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Kenyan"] = "Kenya", ["KE"] = "Kenya",
            ["Nigerian"] = "Nigeria", ["NG"] = "Nigeria",
            ["UK"] = "United Kingdom", ["British"] = "United Kingdom", ["GB"] = "United Kingdom", ["England"] = "United Kingdom",
            ["Indian"] = "India", ["IN"] = "India",
            ["South African"] = "South Africa", ["ZA"] = "South Africa", ["RSA"] = "South Africa",
        };

        public static string Normalize(string country) =>
            Aliases.TryGetValue(country, out var canonical) ? canonical : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(country.ToLowerInvariant());
    }
}
