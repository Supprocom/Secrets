using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Supprocom.Secrets;

internal sealed class ParsedSecretDocument
{
    public ParsedSecretDocument(string path, string rawText)
    {
        Path = path;
        RawText = rawText;
    }

    public string Path { get; }

    public string RawText { get; }

    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> LocalOptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasLocalOptions { get; set; }

    public JsonElement? LocalOptionsElement { get; set; }

    public string? SourceDirective { get; set; }
}

internal static class SecretDocumentParser
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 128
    };

    private static readonly JsonReaderOptions JsonReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 128
    };

    public static ParsedSecretDocument Parse(string text, string path)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);

        string normalized = NormalizeNewlines(text);
        string[] lines = normalized.Split('\n');
        var result = new ParsedSecretDocument(path, text);

        for (int index = 0; index < lines.Length; index++)
        {
            string line = index == 0 ? lines[index].TrimStart('\uFEFF') : lines[index];
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            int equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex <= 0)
                throw Error(
                    "InvalidDotenvAssignment",
                    $"Invalid assignment in '{path}' at line {index + 1}. Expected KEY=value.");

            string key = trimmed[..equalsIndex].Trim();
            ValidateKey(key, path, index + 1);
            string value = trimmed[(equalsIndex + 1)..];

            if (key.Equals("SUPPROCOM_LOCAL_OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                if (result.HasLocalOptions)
                    throw Error(
                        "DuplicateLocalOptions",
                        $"Duplicate SUPPROCOM_LOCAL_OPTIONS assignment in '{path}' at line {index + 1}.");

                LocalOptionsRead local = ReadLocalOptions(value, lines, index, path);
                result.LocalOptions.Clear();
                foreach (KeyValuePair<string, string> item in FlattenJsonObject(
                             local.Json,
                             path,
                             "SUPPROCOM_LOCAL_OPTIONS"))
                {
                    result.LocalOptions.Add(item.Key, item.Value);
                }

                result.LocalOptionsElement = local.Element;
                result.HasLocalOptions = true;
                index = local.FinalLine;
                continue;
            }

            if (key.Equals("SUPPROCOM_SECRET_SOURCE", StringComparison.OrdinalIgnoreCase))
            {
                if (result.SourceDirective is not null)
                    throw Error(
                        "DuplicateSecretSource",
                        $"Duplicate SUPPROCOM_SECRET_SOURCE assignment in '{path}' at line {index + 1}.");

                string source = ParseScalarValue(value, path, index + 1, key);
                if (source.Length == 0)
                    throw Error(
                        "EmptySecretSource",
                        $"SUPPROCOM_SECRET_SOURCE is empty in '{path}' at line {index + 1}.");

                result.SourceDirective = source;
                continue;
            }

            string configurationKey = NormalizeConfigurationKey(key);
            if (!result.Values.TryAdd(
                    configurationKey,
                    ParseScalarValue(value, path, index + 1, key)))
            {
                throw Error(
                    "DuplicateDotenvKey",
                    $"Duplicate configuration key '{key}' in '{path}' at line {index + 1}.");
            }
        }

        return result;
    }

    public static Dictionary<string, string> FlattenJsonObject(
        string json,
        string path,
        string context)
    {
        using JsonDocument document = ParseJsonDocument(json, path, context);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw Error("InvalidJsonObject", $"{context} in '{path}' must be a JSON object.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FlattenObject(document.RootElement, prefix: null, values, path, context);
        return values;
    }

    public static Dictionary<string, string> FlattenJsonElement(
        JsonElement element,
        string path,
        string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Error("InvalidJsonObject", $"{context} in '{path}' must be a JSON object.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FlattenObject(element, prefix: null, values, path, context);
        return values;
    }

    public static bool TryParseJsonObject(
        string text,
        string path,
        out JsonElement element,
        out string error)
    {
        try
        {
            using JsonDocument document = ParseJsonDocument(text, path, "Environment document");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                element = default;
                error = "The JSON root must be an object.";
                return false;
            }

            element = document.RootElement.Clone();
            error = string.Empty;
            return true;
        }
        catch (SupprocomSecretsException exception)
        {
            element = default;
            error = exception.Message;
            return false;
        }
    }

    public static JsonElement ParseJsonObjectElement(string json, string path, string context)
    {
        using JsonDocument document = ParseJsonDocument(json, path, context);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw Error("InvalidJsonObject", $"{context} in '{path}' must be a JSON object.");

        return document.RootElement.Clone();
    }

    private static LocalOptionsRead ReadLocalOptions(
        string firstValue,
        string[] lines,
        int startLine,
        string path)
    {
        string first = firstValue.TrimStart();
        if (!first.StartsWith('{'))
        {
            throw Error(
                "InvalidLocalOptions",
                $"SUPPROCOM_LOCAL_OPTIONS in '{path}' at line {startLine + 1} must start with a JSON object.");
        }

        var builder = new StringBuilder(first);
        for (int line = startLine + 1; line < lines.Length; line++)
        {
            builder.Append('\n');
            builder.Append(lines[line]);
        }

        string tail = builder.ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(tail);
        var reader = new Utf8JsonReader(
            bytes,
            isFinalBlock: true,
            new JsonReaderState(JsonReaderOptions));

        bool rootStarted = false;
        int depth = 0;
        long consumed = -1;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    if (!rootStarted)
                    {
                        rootStarted = true;
                        if (depth != 0)
                            throw Error("InvalidLocalOptions", "The local-options JSON root is not an object.");
                    }

                    depth++;
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    if (!rootStarted)
                        throw Error("InvalidLocalOptions", "The local-options JSON root is not an object.");

                    depth++;
                }
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    depth--;
                    if (depth < 0)
                        throw Error("InvalidLocalOptions", "The local-options JSON object is malformed.");

                    if (rootStarted && depth == 0)
                    {
                        if (reader.TokenType != JsonTokenType.EndObject)
                            throw Error("InvalidLocalOptions", "SUPPROCOM_LOCAL_OPTIONS must contain a JSON object.");

                        consumed = reader.BytesConsumed;
                        break;
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            throw JsonError("InvalidLocalOptions", path, startLine, exception);
        }

        if (!rootStarted || consumed < 0)
        {
            throw Error(
                "InvalidLocalOptions",
                $"SUPPROCOM_LOCAL_OPTIONS in '{path}' at line {startLine + 1} has no complete JSON object.");
        }

        string json = Encoding.UTF8.GetString(bytes.AsSpan(0, checked((int)consumed)));
        string suffix = Encoding.UTF8.GetString(bytes.AsSpan(checked((int)consumed)));
        ValidateLocalOptionsSuffix(suffix, path, startLine);

        JsonElement element;
        try
        {
            element = ParseJsonObjectElement(json, path, "SUPPROCOM_LOCAL_OPTIONS");
        }
        catch (SupprocomSecretsException exception)
        {
            throw WithLine(exception, startLine + 1);
        }

        return new LocalOptionsRead(
            json,
            element,
            startLine + CountNewlines(json));
    }

    private static void ValidateLocalOptionsSuffix(string suffix, string path, int startLine)
    {
        if (suffix.Length == 0)
            return;

        string normalized = NormalizeNewlines(suffix);
        int newline = normalized.IndexOf('\n');
        string firstLine = newline < 0 ? normalized : normalized[..newline];
        if (firstLine.Trim().Length != 0)
        {
            throw Error(
                "LocalOptionsMustBeFinal",
                $"SUPPROCOM_LOCAL_OPTIONS in '{path}' at line {startLine + 1} must be the final assignment.");
        }

        if (newline < 0)
            return;

        foreach (string line in normalized[(newline + 1)..].Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            int equals = trimmed.IndexOf('=');
            string key = equals > 0 ? trimmed[..equals].Trim() : string.Empty;
            if (key.Equals("SUPPROCOM_LOCAL_OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                throw Error(
                    "DuplicateLocalOptions",
                    $"Duplicate SUPPROCOM_LOCAL_OPTIONS assignment after its first block in '{path}'.");
            }

            if (trimmed.Length != 0)
            {
                throw Error(
                    "LocalOptionsMustBeFinal",
                    $"SUPPROCOM_LOCAL_OPTIONS in '{path}' must be the final assignment.");
            }
        }
    }

    private static string ParseScalarValue(string value, string path, int line, string key)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith('"'))
            return trimmed;

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.String)
                throw new JsonException();

            return document.RootElement.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            throw Error(
                "InvalidQuotedValue",
                $"Quoted value for '{key}' in '{path}' at line {line} is not a valid JSON string.");
        }
    }

    private static void ValidateKey(string key, string path, int line)
    {
        if (key.Length == 0)
            throw Error("InvalidDotenvKey", $"An assignment key in '{path}' at line {line} is empty.");

        foreach (char character in key)
        {
            bool valid = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_'
                or '.'
                or '-';

            if (!valid)
            {
                throw Error(
                    "InvalidDotenvKey",
                    $"Assignment key '{key}' in '{path}' at line {line} contains an unsupported character.");
            }
        }
    }

    private static void FlattenObject(
        JsonElement element,
        string? prefix,
        IDictionary<string, string> values,
        string path,
        string context)
    {
        foreach (JsonProperty property in element.EnumerateObject())
            Flatten(Join(prefix, property.Name), property.Value, values, path, context);
    }

    private static void Flatten(
        string prefix,
        JsonElement element,
        IDictionary<string, string> values,
        string path,
        string context)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                FlattenObject(element, prefix, values, path, context);
                return;
            case JsonValueKind.Array:
            {
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                    Flatten($"{prefix}:{index++}", item, values, path, context);

                return;
            }
            case JsonValueKind.String:
                AddFlattened(values, prefix, element.GetString() ?? string.Empty, path, context);
                return;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                AddFlattened(values, prefix, element.GetRawText(), path, context);
                return;
            case JsonValueKind.Null:
                AddFlattened(values, prefix, string.Empty, path, context);
                return;
            default:
                throw Error("InvalidJsonValue", $"{context} in '{path}' contains an unsupported JSON value.");
        }
    }

    private static void AddFlattened(
        IDictionary<string, string> values,
        string key,
        string value,
        string path,
        string context)
    {
        string normalized = NormalizeConfigurationKey(key);
        if (!values.TryAdd(normalized, value))
        {
            throw Error(
                "FlatteningCollision",
                $"{context} in '{path}' contains duplicate configuration path '{normalized}'.");
        }
    }

    private static JsonDocument ParseJsonDocument(string text, string path, string context)
    {
        try
        {
            return JsonDocument.Parse(text, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw JsonError("InvalidJson", path, 0, exception, context);
        }
    }

    private static SupprocomSecretsException JsonError(
        string code,
        string path,
        int startLine,
        JsonException exception,
        string context = "JSON")
    {
        long line = startLine + (exception.LineNumber ?? 0) + 1;
        long column = (exception.BytePositionInLine ?? 0) + 1;
        return Error(code, $"{context} in '{path}' is invalid at line {line}, column {column}.");
    }

    private static SupprocomSecretsException WithLine(SupprocomSecretsException exception, int line)
    {
        if (!exception.Message.Contains("line", StringComparison.OrdinalIgnoreCase))
            return Error(exception.Code, $"{exception.Message} At line {line}.");

        return exception;
    }

    private static int CountNewlines(string value) => value.Count(character => character == '\n');

    private static int CountNewlines(string value, int start, int length) =>
        value.AsSpan(start, length).Count('\n');

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Join(string? prefix, string key) =>
        string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

    internal static string NormalizeConfigurationKey(string key) =>
        key.Replace("__", ":", StringComparison.Ordinal);

    private static SupprocomSecretsException Error(string code, string message) =>
        new(code, message);

    private readonly record struct LocalOptionsRead(
        string Json,
        JsonElement Element,
        int FinalLine);
}
