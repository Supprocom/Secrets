namespace Supprocom.Secrets;

/// <summary>
/// One normalized .NET configuration setting in a detached dotenv document.
/// </summary>
public sealed record SupprocomSecretSetting(string Key, string Value);

/// <summary>
/// Parses and serializes canonical Supprocom dotenv text without file or provider side effects.
/// </summary>
public sealed class SupprocomSecretDocument
{
    private const string DetachedPath = "<detached>";

    private SupprocomSecretDocument(IReadOnlyList<SupprocomSecretSetting> settings)
    {
        Settings = settings;
    }

    /// <summary>
    /// Gets the ordinary settings in source order with normalized .NET configuration keys.
    /// </summary>
    public IReadOnlyList<SupprocomSecretSetting> Settings { get; }

    /// <summary>
    /// Parses canonical dotenv text using the runtime parser and validation rules.
    /// </summary>
    public static SupprocomSecretDocument Parse(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ParsedSecretDocument parsed = SecretDocumentParser.Parse(document, DetachedPath);
        SecretFileRuntime.ValidateDocumentSemantics(parsed);
        RejectReservedDirectives(parsed);

        var settings = parsed.Values
            .Select(item => new SupprocomSecretSetting(item.Key, item.Value))
            .ToArray();
        return new SupprocomSecretDocument(Array.AsReadOnly(settings));
    }

    /// <summary>
    /// Serializes this detached document through the package's canonical dotenv serializer.
    /// </summary>
    public string Serialize() => Serialize(Settings);

    /// <summary>
    /// Serializes normalized .NET configuration settings as canonical dotenv text.
    /// </summary>
    public static string Serialize(IEnumerable<SupprocomSecretSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var values = new List<KeyValuePair<string, string>>();
        foreach (SupprocomSecretSetting setting in settings)
        {
            ArgumentNullException.ThrowIfNull(setting);
            ArgumentNullException.ThrowIfNull(setting.Key);
            ArgumentNullException.ThrowIfNull(setting.Value);

            string normalized = SecretDocumentParser.NormalizeConfigurationKey(setting.Key);
            if (IsReserved(normalized))
            {
                throw new SupprocomSecretsException(
                    "DetachedStructuredEditingUnsupported",
                    "Detached structured editing does not support SUPPROCOM_SECRET_SOURCE or SUPPROCOM_LOCAL_OPTIONS.");
            }

            values.Add(new KeyValuePair<string, string>(normalized, setting.Value));
        }

        string serialized = SecretDocumentSerializer.Serialize(values);
        _ = Parse(serialized);
        return serialized;
    }

    private static void RejectReservedDirectives(ParsedSecretDocument document)
    {
        if (document.SourceDirective is not null || document.HasLocalOptions)
        {
            throw new SupprocomSecretsException(
                "DetachedStructuredEditingUnsupported",
                "Detached structured editing does not support SUPPROCOM_SECRET_SOURCE or SUPPROCOM_LOCAL_OPTIONS.");
        }
    }

    private static bool IsReserved(string key) =>
        key.Equals("SUPPROCOM_SECRET_SOURCE", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SUPPROCOM_LOCAL_OPTIONS", StringComparison.OrdinalIgnoreCase);
}
