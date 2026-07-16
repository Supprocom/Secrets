using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Supprocom.Secrets;

namespace Microsoft.Extensions.Configuration;

public static class SupprocomSecretsConfigurationExtensions
{
    private const string ManualProvidersProperty = "Supprocom.Secrets.ManualProviders";

    public static IConfigurationBuilder AddSupprocomSecrets(
        this IConfigurationBuilder configuration,
        Action<SupprocomSecretsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new SupprocomSecretsOptions();
        configure?.Invoke(options);
        return configuration.Add(new SupprocomSecretsConfigurationSource(options));
    }

    public static IConfigurationBuilder AddSupprocomSecrets(
        this IConfigurationBuilder configuration,
        SupprocomSecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        return configuration.Add(new SupprocomSecretsConfigurationSource(options));
    }

    public static IConfigurationBuilder AddSupprocomSecretsProvider(
        this IConfigurationBuilder configuration,
        ISecretStoreProvider provider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(provider);
        if (!configuration.Properties.TryGetValue(ManualProvidersProperty, out object? value))
        {
            value = new List<ISecretStoreProvider>();
            configuration.Properties[ManualProvidersProperty] = value;
        }

        ((List<ISecretStoreProvider>)value).Add(provider);
        return configuration;
    }

    internal static IReadOnlyList<ISecretStoreProvider> GetManualProviders(IConfigurationBuilder builder)
    {
        if (builder.Properties.TryGetValue(ManualProvidersProperty, out object? value) &&
            value is List<ISecretStoreProvider> providers)
        {
            return providers;
        }

        return Array.Empty<ISecretStoreProvider>();
    }
}

internal sealed class SupprocomSecretsConfigurationSource : IConfigurationSource
{
    public SupprocomSecretsConfigurationSource(SupprocomSecretsOptions options)
    {
        Options = options;
    }

    public SupprocomSecretsOptions Options { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new SupprocomSecretsConfigurationProvider(
            Options,
            SupprocomSecretsConfigurationExtensions.GetManualProviders(builder));
}

internal sealed class SupprocomSecretsConfigurationProvider : ConfigurationProvider
{
    private readonly SupprocomSecretsOptions _options;
    private readonly IReadOnlyList<ISecretStoreProvider> _manualProviders;

    public SupprocomSecretsConfigurationProvider(
        SupprocomSecretsOptions options,
        IReadOnlyList<ISecretStoreProvider> manualProviders)
    {
        _options = options;
        _manualProviders = manualProviders;
    }

    public override void Load()
    {
        try
        {
            Data = LoadDataAsync(_options.StartupCancellationToken).GetAwaiter().GetResult();
        }
        catch (SupprocomSecretsException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new SupprocomSecretsException(
                "ConfigurationLoadCancelled",
                "Supprocom.Secrets configuration loading was cancelled.");
        }
        catch (Exception exception)
        {
            throw new SupprocomSecretsException(
                "ConfigurationLoadFailed",
                $"Supprocom.Secrets configuration loading failed ({exception.GetType().Name}).");
        }
    }

    private async Task<IDictionary<string, string?>> LoadDataAsync(CancellationToken cancellationToken)
    {
        var runtime = new SecretFileRuntime(_options);
        FileLoadResult local = await runtime.LoadAsync(cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (local.SourceDirective is null)
        {
            foreach (KeyValuePair<string, string> item in local.Values)
                data[item.Key] = item.Value;
        }
        else
        {
            Uri source = CreateSourceUri(local.SourceDirective);
            ISecretStoreProvider provider = ProviderResolver.Resolve(
                source,
                _options,
                _manualProviders);
            IReadOnlyDictionary<string, string> snapshot = await LoadProviderAsync(
                    provider,
                    source,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (KeyValuePair<string, string> item in snapshot)
                data[item.Key] = item.Value;

            foreach (KeyValuePair<string, string> item in local.LocalOptions)
                data[item.Key] = item.Value;
        }

        if (!_options.FileOverridesProcessEnvironment)
            AddProcessEnvironment(data);

        return data;
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadProviderAsync(
        ISecretStoreProvider provider,
        Uri source,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan duration = _options.ProviderLoadTimeout;
        if (duration <= TimeSpan.Zero)
            duration = TimeSpan.FromSeconds(30);
        timeout.CancelAfter(duration);

        string label = ProviderResolver.SafeLabel(source);
        ISecretStore store;
        try
        {
            store = await provider.OpenAsync(source, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SupprocomSecretsException(
                "ProviderTimeout",
                $"Provider '{source.Scheme}' timed out while opening '{label}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProviderFailure("ProviderOpenFailed", source, label, exception);
        }

        try
        {
            return await store.LoadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SupprocomSecretsException(
                "ProviderTimeout",
                $"Provider '{source.Scheme}' timed out while loading '{label}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProviderFailure("ProviderLoadFailed", source, label, exception);
        }
    }

    private static Uri CreateSourceUri(string source)
    {
        SecretFileRuntime.ValidateSourceDirective(source);
        return new Uri(source, UriKind.Absolute);
    }

    private static void AddProcessEnvironment(IDictionary<string, string?> data)
    {
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is not string key || item.Value is not string value)
                continue;
            if (key.Equals("SUPPROCOM_SECRET_SOURCE", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SUPPROCOM_LOCAL_OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            data[SecretDocumentParser.NormalizeConfigurationKey(key)] = value;
        }
    }

    private static SupprocomSecretsException ProviderFailure(
        string code,
        Uri source,
        string label,
        Exception exception) =>
        new(
            code,
            $"Provider '{source.Scheme}' failed for '{label}' during collection loading ({exception.GetType().Name}).");
}

internal static class ProviderResolver
{
    public static ISecretStoreProvider Resolve(
        Uri source,
        SupprocomSecretsOptions options,
        IReadOnlyList<ISecretStoreProvider> manualProviders)
    {
        var providers = new List<ISecretStoreProvider>();
        providers.AddRange(options.ProviderAdapters);
        providers.AddRange(manualProviders);

        string manifestPath = options.ProviderManifestPath
            ?? Path.Combine(AppContext.BaseDirectory, "supprocom-secrets.providers.json");
        if (File.Exists(manifestPath))
            providers.AddRange(LoadManifest(manifestPath));

        ISecretStoreProvider? match = providers.FirstOrDefault(
            provider => provider.Scheme.Equals(source.Scheme, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new SupprocomSecretsException(
                "ProviderAdapterMissing",
                $"No installed provider adapter is allowlisted for URI scheme '{source.Scheme}'.");
        }

        return match;
    }

    public static string SafeLabel(Uri source) =>
        string.IsNullOrEmpty(source.AbsolutePath) || source.AbsolutePath == "/"
            ? source.Host
            : $"{source.Host}{source.AbsolutePath}";

    private static IReadOnlyList<ISecretStoreProvider> LoadManifest(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SupprocomSecretsException(
                "ProviderManifestReadFailed",
                $"Unable to read provider manifest '{path}'.",
                exception);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (!document.RootElement.TryGetProperty("version", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetInt32() != 1 ||
                !document.RootElement.TryGetProperty("providers", out JsonElement providerArray) ||
                providerArray.ValueKind != JsonValueKind.Array)
            {
                throw new SupprocomSecretsException(
                    "InvalidProviderManifest",
                    $"Provider manifest '{path}' must declare version 1 and a providers array.");
            }

            var providers = new List<ISecretStoreProvider>();
            var schemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement item in providerArray.EnumerateArray())
            {
                if (!item.TryGetProperty("scheme", out JsonElement schemeElement) ||
                    !item.TryGetProperty("assembly", out JsonElement assemblyElement) ||
                    !item.TryGetProperty("providerType", out JsonElement typeElement) ||
                    schemeElement.ValueKind != JsonValueKind.String ||
                    assemblyElement.ValueKind != JsonValueKind.String ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    throw new SupprocomSecretsException(
                        "InvalidProviderManifest",
                        $"Provider manifest '{path}' contains a provider without an assembly and providerType.");
                }

                string assemblyName = assemblyElement.GetString()!;
                string typeName = typeElement.GetString()!;
                string scheme = schemeElement.GetString()!;
                if (string.IsNullOrWhiteSpace(scheme) || !schemes.Add(scheme))
                {
                    throw new SupprocomSecretsException(
                        "InvalidProviderManifest",
                        $"Provider manifest '{path}' contains a duplicate or empty provider scheme.");
                }

                Type? providerType;
                try
                {
                    providerType = Type.GetType($"{typeName}, {assemblyName}", throwOnError: false);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or TypeLoadException or FileLoadException or
                    FileNotFoundException or BadImageFormatException)
                {
                    throw new SupprocomSecretsException(
                        "ProviderAssemblyLoadFailed",
                        $"Provider manifest '{path}' could not load its declared provider assembly.");
                }
                if (providerType is null)
                {
                    try
                    {
                        providerType = Assembly.Load(new AssemblyName(assemblyName))
                            .GetType(typeName, throwOnError: false, ignoreCase: false);
                    }
                    catch (Exception exception) when (
                        exception is FileLoadException or FileNotFoundException or BadImageFormatException or
                        TypeLoadException)
                    {
                        throw new SupprocomSecretsException(
                            "ProviderAssemblyLoadFailed",
                            $"Provider manifest '{path}' could not load its declared provider assembly.");
                    }
                }

                if (providerType is null || !typeof(ISecretStoreProvider).IsAssignableFrom(providerType))
                {
                    throw new SupprocomSecretsException(
                        "InvalidProviderManifest",
                        $"Provider manifest '{path}' names a type that is not an ISecretStoreProvider.");
                }

                ISecretStoreProvider provider;
                try
                {
                    if (Activator.CreateInstance(providerType) is not ISecretStoreProvider instance)
                    {
                        throw new SupprocomSecretsException(
                            "ProviderInstantiationFailed",
                            $"Provider manifest '{path}' names a provider that cannot be constructed.");
                    }

                    provider = instance;
                }
                catch (SupprocomSecretsException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is MissingMethodException or MemberAccessException or TargetInvocationException or
                    InvalidOperationException or TypeLoadException)
                {
                    throw new SupprocomSecretsException(
                        "ProviderInstantiationFailed",
                        $"Provider manifest '{path}' names a provider that cannot be constructed.");
                }

                if (!provider.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SupprocomSecretsException(
                        "InvalidProviderManifest",
                        $"Provider manifest '{path}' declares scheme '{scheme}' for a provider with a different stable scheme.");
                }

                providers.Add(provider);
            }

            return providers;
        }
        catch (SupprocomSecretsException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new SupprocomSecretsException(
                "InvalidProviderManifest",
                $"Provider manifest '{path}' is not valid JSON.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new SupprocomSecretsException(
                "InvalidProviderManifest",
                $"Provider manifest '{path}' has an invalid JSON shape or version.");
        }
    }

}
