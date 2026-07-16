using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Supprocom.Secrets;

namespace Supprocom.Secrets.Tests;

[TestFixture]
public sealed class SupprocomSecretsTests
{
    [Test]
    public async Task DotenvAndLocalOptionsProjectAtTheConfigurationRoot()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", """
            # ordinary values
            Api__Url=https://example.test/a=b#fragment
            Password = "  secret = # value  "
            SUPPROCOM_LOCAL_OPTIONS={
              // readable installation-local settings
              "Auth": {
                "LocalApiKey": "local-key",
              },
              "Brace": "} // # inside a string",
            }
            """);

        var fileOptions = new SupprocomSecretFileOptions
        {
            Directory = directory.Path
        };
        var store = new SupprocomSecretFileStore(fileOptions);

        IReadOnlyDictionary<string, string> values = await store.LoadAsync();

        Assert.That(values["Api:Url"], Is.EqualTo("https://example.test/a=b#fragment"));
        Assert.That(values["Password"], Is.EqualTo("  secret = # value  "));
        Assert.That(values["Auth:LocalApiKey"], Is.EqualTo("local-key"));
        Assert.That(values["Brace"], Is.EqualTo("} // # inside a string"));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddSupprocomSecrets(options => options.File.Directory = directory.Path)
            .Build();

        Assert.That(configuration["Auth:LocalApiKey"], Is.EqualTo("local-key"));
        Assert.That(configuration["Api:Url"], Is.EqualTo("https://example.test/a=b#fragment"));
    }

    [Test]
    public async Task MissingActiveFileIsCreatedFromThePortableTemplate()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env.template", "Template__Value=from-template\n");

        var options = new SupprocomSecretFileOptions { Directory = directory.Path };
        IReadOnlyDictionary<string, string> values =
            await new SupprocomSecretFileStore(options).LoadAsync();

        Assert.That(values["Template:Value"], Is.EqualTo("from-template"));
        Assert.That(File.Exists(Path.Combine(directory.Path, ".env")), Is.True);
        Assert.That(
            File.ReadAllText(Path.Combine(directory.Path, ".env")),
            Is.EqualTo("Template__Value=from-template\n"));
    }

    [Test]
    public void PointerModeRejectsPlaintextAssignmentsWithoutFallback()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", """
            SUPPROCOM_SECRET_SOURCE=fixture://vault/application
            Database__Password=must-not-fallback
            """);

        var exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await new SupprocomSecretFileStore(
                    new SupprocomSecretFileOptions { Directory = directory.Path })
                .LoadAsync());

        Assert.That(exception!.Code, Is.EqualTo("PointerFileContainsConfiguration"));
        Assert.That(exception.Message, Does.Not.Contain("must-not-fallback"));
    }

    [Test]
    public void ProcessEnvironmentOverridesTheFileByDefault()
    {
        using var directory = new TemporaryDirectory();
        const string environmentKey = "SUPPROCOM_TEST_PROCESS__Value";
        Write(directory.Path, ".env", $"{environmentKey}=file\n");
        string? previous = Environment.GetEnvironmentVariable(environmentKey);
        try
        {
            Environment.SetEnvironmentVariable(environmentKey, "process");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddSupprocomSecrets(options => options.File.Directory = directory.Path)
                .Build();
            Assert.That(configuration["SUPPROCOM_TEST_PROCESS:Value"], Is.EqualTo("process"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, previous);
        }
    }

    [Test]
    public async Task DevelopmentOverlayAndReplacementAreDeterministic()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "Base=base\nShared=base\n");
        Write(directory.Path, ".dev.env", "Shared=overlay\nDevOnly=overlay\n");

        var overlayOptions = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            DevelopmentName = ".dev.env",
            DevelopmentComposition = SecretFileComposition.Overlay
        };
        IReadOnlyDictionary<string, string> overlay = await new SupprocomSecretFileStore(
                overlayOptions,
                environmentName: "Development")
            .LoadAsync();
        Assert.That(overlay["Base"], Is.EqualTo("base"));
        Assert.That(overlay["Shared"], Is.EqualTo("overlay"));

        File.Delete(Path.Combine(directory.Path, ".dev.env"));
        Write(directory.Path, ".env.development", "Shared=replacement\nReplacementOnly=yes\n");
        var replacementOptions = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            DevelopmentName = ".env.development",
            DevelopmentComposition = SecretFileComposition.Replace
        };
        IReadOnlyDictionary<string, string> replacement = await new SupprocomSecretFileStore(
                replacementOptions,
                environmentName: "Development")
            .LoadAsync();
        Assert.That(replacement.ContainsKey("Base"), Is.False);
        Assert.That(replacement["Shared"], Is.EqualTo("replacement"));
    }

    [Test]
    public async Task DevelopmentTemplateConventionsAreDiscoveredWithoutOptions()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env.template", "Base=base\n");
        Write(directory.Path, ".dev.env.template", "Dev=overlay\n");

        var fileOptions = new SupprocomSecretFileOptions { Directory = directory.Path };
        IReadOnlyDictionary<string, string> values = await new SupprocomSecretFileStore(
                fileOptions,
                environmentName: "Development")
            .LoadAsync();

        Assert.That(values["Base"], Is.EqualTo("base"));
        Assert.That(values["Dev"], Is.EqualTo("overlay"));
        Assert.That(File.Exists(Path.Combine(directory.Path, ".dev.env")), Is.True);
    }

    [Test]
    public void FileCanExplicitlyOverrideAnEarlierEnvironmentSource()
    {
        using var directory = new TemporaryDirectory();
        const string key = "SUPPROCOM_TEST_FILE_WINS";
        Write(directory.Path, ".env", $"{key}=file\n");
        string? previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "process");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddSupprocomSecrets(options =>
                {
                    options.File.Directory = directory.Path;
                    options.FileOverridesProcessEnvironment = true;
                })
                .Build();
            Assert.That(configuration[key], Is.EqualTo("file"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Test]
    public async Task LocalOptionsOverlayAcrossDevelopmentFiles()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", """
            Ordinary=base
            SUPPROCOM_LOCAL_OPTIONS={
              "Auth": { "LocalApiKey": "base", "Tenant": "base" }
            }
            """);
        Write(directory.Path, ".dev.env", """
            SUPPROCOM_LOCAL_OPTIONS={
              "Auth": { "LocalApiKey": "development" }
            }
            """);

        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            DevelopmentName = ".dev.env",
            DevelopmentComposition = SecretFileComposition.Overlay
        };
        IReadOnlyDictionary<string, string> values = await new SupprocomSecretFileStore(
                options,
                environmentName: "Development")
            .LoadAsync();

        Assert.That(values["Auth:LocalApiKey"], Is.EqualTo("development"));
        Assert.That(values["Auth:Tenant"], Is.EqualTo("base"));
    }

    [Test]
    public void LocalOptionsMustBeFinalAndMalformedJsonIsValueFree()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", """
            SUPPROCOM_LOCAL_OPTIONS={
              "Secret": "do-not-echo"
            }
            Later=value
            """);

        var exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await new SupprocomSecretFileStore(
                    new SupprocomSecretFileOptions { Directory = directory.Path })
                .LoadAsync());
        Assert.That(exception!.Code, Is.EqualTo("LocalOptionsMustBeFinal"));
        Assert.That(exception.Message, Does.Not.Contain("do-not-echo"));

        Write(directory.Path, ".env", "SUPPROCOM_LOCAL_OPTIONS={\n  \"Secret\": \"do-not-echo\",\n");
        exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await new SupprocomSecretFileStore(
                    new SupprocomSecretFileOptions { Directory = directory.Path })
                .LoadAsync());
        Assert.That(exception!.Code, Is.EqualTo("InvalidLocalOptions"));
        Assert.That(exception.Message, Does.Not.Contain("do-not-echo"));
    }

    [Test]
    public async Task JsonWithCommentsImportIsOneTimeAndPreservesOriginalBytes()
    {
        using var directory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes("""
            {
              // existing SharpClaw document
              "Api": {
                "Url": "https://example.test",
                "Retries": 3,
                "Enabled": true,
                "Missing": null,
              },
              "Items": ["one", false, 2.50,],
              "SUPPROCOM_LOCAL_OPTIONS": {
                "CertificateAlias": "local",
              },
            }
            """);
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), original);

        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Import = SecretFileImport.JsonWithCommentsOnce
        };
        var store = new SupprocomSecretFileStore(options);
        IReadOnlyDictionary<string, string> values = await store.LoadAsync();

        Assert.That(values["Api:Url"], Is.EqualTo("https://example.test"));
        Assert.That(values["Api:Retries"], Is.EqualTo("3"));
        Assert.That(values["Api:Enabled"], Is.EqualTo("true"));
        Assert.That(values["Api:Missing"], Is.EqualTo(string.Empty));
        Assert.That(values["Items:0"], Is.EqualTo("one"));
        Assert.That(values["Items:1"], Is.EqualTo("false"));
        Assert.That(values["Items:2"], Is.EqualTo("2.50"));
        Assert.That(values["CertificateAlias"], Is.EqualTo("local"));

        string[] preserved = Directory.GetFiles(directory.Path, ".pre-supprocom-import-*");
        Assert.That(preserved, Has.Length.EqualTo(1));
        Assert.That(File.ReadAllBytes(preserved[0]), Is.EqualTo(original));
        Assert.That(File.ReadAllText(Path.Combine(directory.Path, ".env")), Does.Not.StartWith("{"));

        await store.LoadAsync();
        Assert.That(Directory.GetFiles(directory.Path, ".pre-supprocom-import-*"), Has.Length.EqualTo(1));
    }

    [Test]
    public void LocalOptionsBindThroughOrdinaryOptionsWithoutPerKeyRegistration()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", """
            Auth__LocalApiKey=portable
            SUPPROCOM_LOCAL_OPTIONS={
              "Auth": {
                "LocalApiKey": "installation-local",
                "Tenant": "development"
              }
            }
            """);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddSupprocomSecrets(options => options.File.Directory = directory.Path)
            .Build();
        using ServiceProvider services = new ServiceCollection()
            .AddOptions<AuthOptions>()
            .Bind(configuration.GetSection("Auth"))
            .Services
            .BuildServiceProvider();

        AuthOptions bound = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        Assert.That(bound.LocalApiKey, Is.EqualTo("installation-local"));
        Assert.That(bound.Tenant, Is.EqualTo("development"));
    }

    [Test]
    public async Task ProtectedReadsAndMutationsUseCiphertextAndAtomicReplacement()
    {
        using var directory = new TemporaryDirectory();
        string keyPath = Path.Combine(directory.Path, "installation.key");
        Write(directory.Path, ".env", "ApiKey=initial\n");
        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Protection = SecretFileProtection.InstallationBoundAesGcm,
            InstallationKeyPath = keyPath
        };
        var store = new SupprocomSecretFileStore(options);

        Assert.That((await store.LoadAsync())["ApiKey"], Is.EqualTo("initial"));
        byte[] protectedBytes = File.ReadAllBytes(Path.Combine(directory.Path, ".env"));
        Assert.That(protectedBytes[0], Is.EqualTo(1));
        Assert.That(Encoding.UTF8.GetString(protectedBytes), Does.Not.Contain("initial"));

        await store.SetAsync("ApiKey", "updated");
        await store.SetAsync("Another", "value");
        await store.DeleteAsync("Another");
        IReadOnlyDictionary<string, string> values = await store.LoadAsync();
        Assert.That(values["ApiKey"], Is.EqualTo("updated"));
        Assert.That(values.ContainsKey("Another"), Is.False);
    }

    [Test]
    public async Task RecoveryQuarantinesUnreadableActiveAndRestoresTemplate()
    {
        using var directory = new TemporaryDirectory();
        byte[] unreadable = Encoding.UTF8.GetBytes("not-an-assignment\n");
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), unreadable);
        Write(directory.Path, ".env.template", "Recovered=yes\n");
        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Recovery = SecretFileRecovery.QuarantineAndRestoreTemplate
        };

        IReadOnlyDictionary<string, string> values = await new SupprocomSecretFileStore(options).LoadAsync();

        Assert.That(values["Recovered"], Is.EqualTo("yes"));
        string[] quarantined = Directory.GetFiles(directory.Path, ".unreadable-*");
        Assert.That(quarantined, Has.Length.EqualTo(1));
        Assert.That(File.ReadAllBytes(quarantined[0]), Is.EqualTo(unreadable));
        Assert.That(File.ReadAllText(Path.Combine(directory.Path, ".env.template")), Is.EqualTo("Recovered=yes\n"));
    }

    [Test]
    public async Task ManifestDiscoveryUsesOnlyTheDeclaredProviderType()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "SUPPROCOM_SECRET_SOURCE=manifest://vault/application\n");
        string manifest = Path.Combine(directory.Path, "supprocom-secrets.providers.json");
        string assembly = typeof(ManifestProvider).Assembly.GetName().Name!;
        File.WriteAllText(
            manifest,
            $$"""
            {
              "version": 1,
              "providers": [
                {
                  "scheme": "manifest",
                  "assembly": "{{assembly}}",
                  "providerType": "{{typeof(ManifestProvider).FullName}}"
                }
              ]
            }
            """,
            new UTF8Encoding(false));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddSupprocomSecrets(options =>
            {
                options.File.Directory = directory.Path;
                options.ProviderManifestPath = manifest;
            })
            .Build();

        Assert.That(configuration["Manifest:Value"], Is.EqualTo("from-manifest"));
    }

    [Test]
    public void ExternalProviderFailureDoesNotRecoverFromAFileTemplateOrLeakValues()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "SUPPROCOM_SECRET_SOURCE=failure://vault/application\n");
        Write(directory.Path, ".env.template", "Fallback=must-not-load\n");

        var exception = Assert.Throws<SupprocomSecretsException>(() => new ConfigurationBuilder()
            .AddSupprocomSecretsProvider(new FailingProvider())
            .AddSupprocomSecrets(options => options.File.Directory = directory.Path)
            .Build());

        Assert.That(exception!.Code, Is.EqualTo("ProviderLoadFailed"));
        Assert.That(exception.ToString(), Does.Not.Contain("secret-from-provider"));
        Assert.That(Directory.GetFiles(directory.Path, ".unreadable-*") , Has.Length.EqualTo(0));
        Assert.That(File.ReadAllText(Path.Combine(directory.Path, ".env")), Does.Contain("SUPPROCOM_SECRET_SOURCE"));
    }

    [Test]
    public void ProviderTimeoutIsBoundedAndValueFree()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "SUPPROCOM_SECRET_SOURCE=slow://vault/application\n");
        var exception = Assert.Throws<SupprocomSecretsException>(() => new ConfigurationBuilder()
            .AddSupprocomSecretsProvider(new SlowProvider())
            .AddSupprocomSecrets(options =>
            {
                options.File.Directory = directory.Path;
                options.ProviderLoadTimeout = TimeSpan.FromMilliseconds(20);
            })
            .Build());

        Assert.That(exception!.Code, Is.EqualTo("ProviderTimeout"));
        Assert.That(exception.ToString(), Does.Not.Contain("secret-from-provider"));
    }

    [Test]
    public async Task CompatibleEncryptedJsonImportUsesTheConfiguredKey()
    {
        using var directory = new TemporaryDirectory();
        string keyPath = Path.Combine(directory.Path, "sharpclaw.key");
        byte[] key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        File.WriteAllBytes(keyPath, key);
        string json = """
            {
              "Api": { "Endpoint": "https://example.test", },
              "SUPPROCOM_LOCAL_OPTIONS": { "Local": "value", },
            }
            """;
        byte[] original = SecretFileProtectionCodec.Encrypt(json, key);
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), original);

        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Import = SecretFileImport.JsonWithCommentsOnce,
            Protection = SecretFileProtection.InstallationBoundAesGcm,
            InstallationKeyPath = keyPath
        };
        IReadOnlyDictionary<string, string> values = await new SupprocomSecretFileStore(options).LoadAsync();

        Assert.That(values["Api:Endpoint"], Is.EqualTo("https://example.test"));
        Assert.That(values["Local"], Is.EqualTo("value"));
        Assert.That(File.ReadAllBytes(Path.Combine(directory.Path, ".env"))[0], Is.EqualTo(1));
        string preserved = Directory.GetFiles(directory.Path, ".pre-supprocom-import-*").Single();
        Assert.That(File.ReadAllBytes(preserved), Is.EqualTo(original));
    }

    [Test]
    public async Task ManualProviderRegistrationKeepsConsumerCodeUnchanged()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", """
            SUPPROCOM_SECRET_SOURCE=fixture://vault/application
            SUPPROCOM_LOCAL_OPTIONS={
              "Installation": "local"
            }
            """);

        var provider = new FixtureProvider();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddSupprocomSecretsProvider(provider)
            .AddSupprocomSecrets(options => options.File.Directory = directory.Path)
            .Build();

        Assert.That(configuration["Database:Password"], Is.EqualTo("provider-value"));
        Assert.That(configuration["Installation"], Is.EqualTo("local"));
        Assert.That(provider.Opened, Is.True);
    }

    private static void Write(string directory, string name, string contents) =>
        File.WriteAllText(Path.Combine(directory, name), contents, new UTF8Encoding(false));

    public sealed class FixtureProvider : ISecretStoreProvider
    {
        public string Scheme => "fixture";

        public bool Opened { get; private set; }

        public Task<ISecretStore> OpenAsync(Uri source, CancellationToken cancellationToken = default)
        {
            Opened = true;
            return Task.FromResult<ISecretStore>(new FixtureStore());
        }
    }

    public sealed class ManifestProvider : ISecretStoreProvider
    {
        public string Scheme => "manifest";

        public Task<ISecretStore> OpenAsync(Uri source, CancellationToken cancellationToken = default) =>
            Task.FromResult<ISecretStore>(new ManifestStore());
    }

    public sealed class FailingProvider : ISecretStoreProvider
    {
        public string Scheme => "failure";

        public Task<ISecretStore> OpenAsync(Uri source, CancellationToken cancellationToken = default) =>
            Task.FromResult<ISecretStore>(new FailingStore());
    }

    public sealed class SlowProvider : ISecretStoreProvider
    {
        public string Scheme => "slow";

        public Task<ISecretStore> OpenAsync(Uri source, CancellationToken cancellationToken = default) =>
            Task.FromResult<ISecretStore>(new SlowStore());
    }

    private sealed class FixtureStore : ISecretStore
    {
        public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
                {
                    ["Database:Password"] = "provider-value"
                }));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ManifestStore : ISecretStore
    {
        public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["Manifest:Value"] = "from-manifest" });

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingStore : ISecretStore
    {
        public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-from-provider must not appear in diagnostics");

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SlowStore : ISecretStore
    {
        public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new Dictionary<string, string>();
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AuthOptions
    {
        public string? LocalApiKey { get; set; }

        public string? Tenant { get; set; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Supprocom.Secrets.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
