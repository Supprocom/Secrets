using System.Text.Json;

namespace Supprocom.Secrets;

internal static class JsonWithCommentsImporter
{
    public static ParsedSecretDocument Import(string text, string path)
    {
        JsonElement root = SecretDocumentParser.ParseJsonObjectElement(
            text,
            path,
            "JsonWithCommentsOnce import");
        var result = new ParsedSecretDocument(path, text);

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.Equals("SUPPROCOM_SECRET_SOURCE", StringComparison.OrdinalIgnoreCase))
            {
                if (result.SourceDirective is not null)
                {
                    throw new SupprocomSecretsException(
                        "DuplicateSecretSource",
                        $"JSON import in '{path}' contains more than one SUPPROCOM_SECRET_SOURCE property.");
                }

                if (property.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    throw new SupprocomSecretsException(
                        "InvalidSecretSource",
                        $"SUPPROCOM_SECRET_SOURCE in JSON import '{path}' must be a non-empty string.");
                }

                result.SourceDirective = property.Value.GetString();
                continue;
            }

            if (property.Name.Equals("SUPPROCOM_LOCAL_OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                if (result.HasLocalOptions)
                {
                    throw new SupprocomSecretsException(
                        "DuplicateLocalOptions",
                        $"JSON import in '{path}' contains more than one SUPPROCOM_LOCAL_OPTIONS property.");
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new SupprocomSecretsException(
                        "InvalidLocalOptions",
                        $"SUPPROCOM_LOCAL_OPTIONS in JSON import '{path}' must be an object.");
                }

                result.LocalOptionsElement = property.Value.Clone();
                foreach (KeyValuePair<string, string> item in SecretDocumentParser.FlattenJsonElement(
                             property.Value,
                             path,
                             "SUPPROCOM_LOCAL_OPTIONS"))
                {
                    result.LocalOptions.Add(item.Key, item.Value);
                }

                result.HasLocalOptions = true;
                continue;
            }

            AddFlattenedProperty(result, property, path);
        }

        return result;
    }

    private static void AddFlattenedProperty(
        ParsedSecretDocument result,
        JsonProperty property,
        string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var wrapper = JsonDocument.Parse(
            $"{{{JsonSerializer.Serialize(property.Name)}:{property.Value.GetRawText()}}}",
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        values = SecretDocumentParser.FlattenJsonElement(wrapper.RootElement, path, "JSON import");
        foreach (KeyValuePair<string, string> item in values)
        {
            if (!result.Values.TryAdd(item.Key, item.Value))
            {
                throw new SupprocomSecretsException(
                    "FlatteningCollision",
                    $"JSON import in '{path}' contains duplicate configuration path '{item.Key}'.");
            }
        }
    }
}
