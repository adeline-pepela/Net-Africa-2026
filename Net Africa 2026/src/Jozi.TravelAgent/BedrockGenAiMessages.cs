using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Evaluations.Otel;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Jozi.TravelAgent;

/// <summary>
/// Maps Bedrock Converse messages to the OpenTelemetry GenAI message schema
/// (role + parts: text | tool_call | tool_call_response) used in gen_ai.input/output.messages.
/// </summary>
public static class BedrockGenAiMessages
{
    public static string ToJson(IEnumerable<Message> messages, string? finishReason = null)
    {
        var array = new JsonArray();
        foreach (var message in messages) array.Add(ToNode(message, finishReason));
        return array.ToJsonString();
    }

    public static string SystemInstructions(string systemPrompt) =>
        new JsonArray(new JsonObject { ["type"] = "text", ["content"] = systemPrompt }).ToJsonString();

    public static string ToolDefinitions(IEnumerable<Tool> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.ToolSpec.Name,
                ["description"] = tool.ToolSpec.Description,
                ["parameters"] = DocumentJson.ToNode(tool.ToolSpec.InputSchema.Json),
            });
        }
        return array.ToJsonString();
    }

    private static JsonObject ToNode(Message message, string? finishReason)
    {
        var content = message.Content ?? [];
        var isToolResultMessage = content.Count > 0 && content.All(c => c.ToolResult is not null);
        var role = isToolResultMessage ? "tool" : message.Role == ConversationRole.Assistant ? "assistant" : "user";

        var parts = new JsonArray();
        foreach (var block in content)
        {
            if (block.Text is not null)
            {
                parts.Add(new JsonObject { ["type"] = "text", ["content"] = block.Text });
            }
            else if (block.ToolUse is not null)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "tool_call",
                    ["id"] = block.ToolUse.ToolUseId,
                    ["name"] = block.ToolUse.Name,
                    ["arguments"] = DocumentJson.ToNode(block.ToolUse.Input),
                });
            }
            else if (block.ToolResult is not null)
            {
                var text = string.Join("\n", (block.ToolResult.Content ?? []).Select(c => c.Text).Where(t => t is not null));
                parts.Add(new JsonObject
                {
                    ["type"] = "tool_call_response",
                    ["id"] = block.ToolResult.ToolUseId,
                    ["response"] = TryParse(text),
                });
            }
        }

        var node = new JsonObject { ["role"] = role, ["parts"] = parts };
        if (finishReason is not null && role == "assistant") node["finish_reason"] = finishReason;
        return node;
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }
}
