using System.Text.Json;

namespace Jozi.TravelAgent;

/// <summary>
/// Everything that changes agent behaviour without changing code: prompt, model, tool descriptions.
/// These are exactly the edits that cause silent regressions, so they live in a file a PR can touch.
/// </summary>
public sealed class AgentConfig
{
    public string Name { get; init; } = "JoziTravelAgent";
    public string Version { get; init; } = "1.0.0";
    public string ModelId { get; init; } = "us.anthropic.claude-sonnet-4-6";
    public int MaxTokens { get; init; } = 1024;
    public float? Temperature { get; init; }
    public int MaxSteps { get; init; } = 8;
    public string SystemPrompt { get; init; } = "";

    /// <summary>Tool name -> description shown to the model. Missing entries fall back to the built-in description.</summary>
    public Dictionary<string, string> ToolDescriptions { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AgentConfig Load(string path) =>
        JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"Could not read agent config from {path}");
}
