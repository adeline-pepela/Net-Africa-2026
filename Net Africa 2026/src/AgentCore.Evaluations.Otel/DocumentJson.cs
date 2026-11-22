using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime.Documents;

namespace AgentCore.Evaluations.Otel;

/// <summary>Converts between System.Text.Json and the AWS SDK's <see cref="Document"/> type.</summary>
public static class DocumentJson
{
    public static Document FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    public static Document FromNode(JsonNode? node) => node is null ? new Document() : FromJson(node.ToJsonString());

    public static Document FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new Document(element.EnumerateObject().ToDictionary(p => p.Name, p => FromElement(p.Value))),
        JsonValueKind.Array => new Document(element.EnumerateArray().Select(FromElement).ToList()),
        JsonValueKind.String => new Document(element.GetString()!),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? new Document(l) : new Document(element.GetDouble()),
        JsonValueKind.True => new Document(true),
        JsonValueKind.False => new Document(false),
        _ => new Document(),
    };

    public static JsonNode? ToNode(Document document)
    {
        if (document.IsNull()) return null;
        if (document.IsDictionary())
        {
            var obj = new JsonObject();
            foreach (var (key, value) in document.AsDictionary()) obj[key] = ToNode(value);
            return obj;
        }
        if (document.IsList()) return new JsonArray(document.AsList().Select(ToNode).ToArray());
        if (document.IsString()) return JsonValue.Create(document.AsString());
        if (document.IsBool()) return JsonValue.Create(document.AsBool());
        if (document.IsInt()) return JsonValue.Create(document.AsInt());
        if (document.IsLong()) return JsonValue.Create(document.AsLong());
        if (document.IsDouble()) return JsonValue.Create(document.AsDouble());
        return null;
    }

    public static string ToJson(Document document) => ToNode(document)?.ToJsonString() ?? "null";
}
