using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Supprocom.Secrets;

internal static class SecretDocumentSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string Serialize(ParsedSecretDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Serialize(
            document.Values,
            document.SourceDirective,
            document.HasLocalOptions ? document.LocalOptionsElement : null,
            document.LocalOptions);
    }

    public static string Serialize(
        IReadOnlyDictionary<string, string> values,
        string? sourceDirective = null,
        JsonElement? localOptions = null,
        IReadOnlyDictionary<string, string>? localOptionValues = null)
    {
        var builder = new StringBuilder();

        foreach (KeyValuePair<string, string> item in values.OrderBy(
                     item => item.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            string key = item.Key.Replace(":", "__", StringComparison.Ordinal);
            builder.Append(key);
            builder.Append('=');
            builder.Append(JsonSerializer.Serialize(item.Value));
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(sourceDirective))
        {
            builder.Append("SUPPROCOM_SECRET_SOURCE=");
            builder.Append(JsonSerializer.Serialize(sourceDirective));
            builder.AppendLine();
        }

        if (localOptions is not null || localOptionValues is { Count: > 0 })
        {
            JsonNode local = localOptions is not null
                ? JsonNode.Parse(
                      localOptions.Value.GetRawText(),
                      documentOptions: new JsonDocumentOptions
                      {
                          AllowTrailingCommas = true,
                          CommentHandling = JsonCommentHandling.Skip
                      })
                    ?? new JsonObject()
                : BuildLocalObject(localOptionValues!);

            builder.Append("SUPPROCOM_LOCAL_OPTIONS=");
            builder.AppendLine(local.ToJsonString(JsonOptions));
        }

        return builder.ToString();
    }

    private static JsonObject BuildLocalObject(IReadOnlyDictionary<string, string> values)
    {
        var root = new JsonObject();
        foreach (KeyValuePair<string, string> item in values.OrderBy(
                     item => item.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            string[] segments = item.Key.Split(':', StringSplitOptions.None);
            JsonObject current = root;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                if (current[segments[index]] is not JsonObject child)
                {
                    child = new JsonObject();
                    current[segments[index]] = child;
                }

                current = child;
            }

            current[segments[^1]] = JsonValue.Create(item.Value);
        }

        return root;
    }
}
