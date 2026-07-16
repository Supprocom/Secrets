using System.Collections;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
              "EmptyObject": {},
              "EmptyArray": [],
              "SUPPROCOM_LOCAL_OPTIONS": {
                "CertificateAlias": "local",
              },
            }
            """);
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), original);

        IConfiguration actual = new ConfigurationBuilder()
            .AddSupprocomSecrets(configurationOptions =>
            {
                configurationOptions.File.Directory = directory.Path;
                configurationOptions.File.Import = SecretFileImport.JsonWithCommentsOnce;
                configurationOptions.FileOverridesProcessEnvironment = true;
            })
            .Build();
        IReadOnlyDictionary<string, string> expected = CanonicalImportProjection(original);
        AssertConfigurationProjection(actual, expected);

        IReadOnlyDictionary<string, string> restartedValues = await new SupprocomSecretFileStore(
                new SupprocomSecretFileOptions { Directory = directory.Path })
            .LoadAsync();
        Assert.That(restartedValues.Keys, Is.EquivalentTo(expected.Keys));
        foreach (KeyValuePair<string, string> item in expected)
            Assert.That(restartedValues[item.Key], Is.EqualTo(item.Value), item.Key);

        IConfiguration restarted = new ConfigurationBuilder()
            .AddSupprocomSecrets(configurationOptions =>
            {
                configurationOptions.File.Directory = directory.Path;
                configurationOptions.FileOverridesProcessEnvironment = true;
            })
            .Build();
        AssertConfigurationProjection(restarted, expected);

        string[] preserved = Directory.GetFiles(directory.Path, ".pre-supprocom-import-*");
        Assert.That(preserved, Has.Length.EqualTo(1));
        Assert.That(File.ReadAllBytes(preserved[0]), Is.EqualTo(original));
        Assert.That(File.ReadAllText(Path.Combine(directory.Path, ".env")), Does.Not.StartWith("{"));

        Assert.That(Directory.GetFiles(directory.Path, ".pre-supprocom-import-*"), Has.Length.EqualTo(1));
    }

    [TestCase("{\"A\":1,\"a\":null}")]
    [TestCase("{\"A\":null,\"a\":1}")]
    [TestCase("{\"A\":1,\"a\":{}}")]
    [TestCase("{\"A\":{},\"a\":1}")]
    [TestCase("{\"Parent\":{\"Value\":1},\"parent\":{\"value\":null}}")]
    [TestCase("{\"Parent\":{\"Value\":null},\"PARENT\":{\"VALUE\":1}}")]
    public void JsonImportRejectsCaseInsensitiveValueAndEmptyPathCollisions(string json)
    {
        using var directory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes(json);
        string activePath = Path.Combine(directory.Path, ".env");
        File.WriteAllBytes(activePath, original);

        SupprocomSecretsException exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await new SupprocomSecretFileStore(
                    new SupprocomSecretFileOptions
                    {
                        Directory = directory.Path,
                        Import = SecretFileImport.JsonWithCommentsOnce
                    })
                .LoadAsync())!;

        Assert.That(exception.Code, Is.EqualTo("FlatteningCollision"));
        Assert.That(File.ReadAllBytes(activePath), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(directory.Path, ".pre-supprocom-import-*"), Is.Empty);
    }

    [Test]
    public async Task ReadAndReplaceDocumentUseTheCompleteDocumentContract()
    {
        using var directory = new TemporaryDirectory();
        string original = "Api__Url=https://before.test\nSUPPROCOM_LOCAL_OPTIONS={\n  \"Tenant\": \"before\"\n}\n";
        string replacement = "Api__Url=https://after.test\nSUPPROCOM_LOCAL_OPTIONS={\n  \"Tenant\": \"after\"\n}\n";
        Write(directory.Path, ".env", original);
        var store = new SupprocomSecretFileStore(new SupprocomSecretFileOptions { Directory = directory.Path });

        Assert.That(await store.ReadDocumentAsync(), Is.EqualTo(original));
        await store.ReplaceDocumentAsync(replacement);
        Assert.That(await store.ReadDocumentAsync(), Is.EqualTo(replacement));

        IReadOnlyDictionary<string, string> values = await store.LoadAsync();
        Assert.That(values["Api:Url"], Is.EqualTo("https://after.test"));
        Assert.That(values["Tenant"], Is.EqualTo("after"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedImportPreservationLeavesActiveBytesAndNoPartialSibling(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes("{\"Value\":\"preserve-me\"}\n");
        string activePath = Path.Combine(directory.Path, ".env");
        File.WriteAllBytes(activePath, original);
        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Import = SecretFileImport.JsonWithCommentsOnce
        };

        SecretFileRuntime.ImportPreservationBeforeInstallHook = cancel
            ? static token => Task.FromException(
                new OperationCanceledException("injected preservation cancellation", token))
            : static _ => Task.FromException(new IOException("injected preservation failure"));
        try
        {
            if (cancel)
            {
                Assert.ThrowsAsync<OperationCanceledException>(
                    async () => await new SupprocomSecretFileStore(options).LoadAsync());
            }
            else
            {
                SupprocomSecretsException exception = Assert.ThrowsAsync<SupprocomSecretsException>(
                    async () => await new SupprocomSecretFileStore(options).LoadAsync())!;
                Assert.That(exception.Code, Is.EqualTo("ImportPreservationFailed"));
            }
        }
        finally
        {
            SecretFileRuntime.ImportPreservationBeforeInstallHook = null;
        }

        Assert.That(File.ReadAllBytes(activePath), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(directory.Path, ".pre-supprocom-import-*"), Is.Empty);
    }

    [Test]
    public void EveryStoreOperationHonorsCallerCancellation()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "A=value\n");
        var store = new SupprocomSecretFileStore(new SupprocomSecretFileOptions { Directory = directory.Path });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.LoadAsync(cancellation.Token));
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.SetAsync("A", "new", cancellation.Token));
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.DeleteAsync("A", cancellation.Token));
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.ReadDocumentAsync(cancellation.Token));
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.ReplaceDocumentAsync("A=replaced\n", cancellation.Token));
    }

    [Test]
    public async Task MalformedDocumentsAndFailedReadsDoNotChangeTheActiveFile()
    {
        using var directory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes("not-a-valid-assignment\n");
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), original);
        var store = new SupprocomSecretFileStore(new SupprocomSecretFileOptions { Directory = directory.Path });

        SupprocomSecretsException loadException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await store.LoadAsync())!;
        Assert.That(loadException.Code, Is.EqualTo("InvalidDotenvAssignment"));
        SupprocomSecretsException readException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await store.ReadDocumentAsync())!;
        Assert.That(readException.Code, Is.EqualTo("InvalidDotenvAssignment"));
        SupprocomSecretsException setException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await store.SetAsync("A", "new"))!;
        Assert.That(setException.Code, Is.EqualTo("InvalidDotenvAssignment"));
        SupprocomSecretsException deleteException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await store.DeleteAsync("A"))!;
        Assert.That(deleteException.Code, Is.EqualTo("InvalidDotenvAssignment"));
        Assert.That(File.ReadAllBytes(Path.Combine(directory.Path, ".env")), Is.EqualTo(original));

        SupprocomSecretsException replaceException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await store.ReplaceDocumentAsync("not-a-valid-assignment\n"))!;
        Assert.That(replaceException.Code, Is.EqualTo("InvalidDotenvAssignment"));
        replaceException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await store.ReplaceDocumentAsync("SUPPROCOM_SECRET_SOURCE=env://\n"))!;
        Assert.That(replaceException.Code, Is.EqualTo("UnnecessaryEnvSource"));
        Assert.That(File.ReadAllBytes(Path.Combine(directory.Path, ".env")), Is.EqualTo(original));

        using var protectedDirectory = new TemporaryDirectory();
        byte[] protectedOriginal = Encoding.UTF8.GetBytes("A=protected\n");
        File.WriteAllBytes(Path.Combine(protectedDirectory.Path, ".env"), protectedOriginal);
        var protectedStore = new SupprocomSecretFileStore(new SupprocomSecretFileOptions
        {
            Directory = protectedDirectory.Path,
            Protection = SecretFileProtection.InstallationBoundAesGcm,
            InstallationKeyStore = new FailingKeyStore()
        });
        SupprocomSecretsException failedLoad = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await protectedStore.LoadAsync())!;
        Assert.That(failedLoad.Code, Is.EqualTo("InstallationKeyUnavailable"));
        SupprocomSecretsException failedRead = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await protectedStore.ReadDocumentAsync())!;
        Assert.That(failedRead.Code, Is.EqualTo("InstallationKeyUnavailable"));
        Assert.That(
            File.ReadAllBytes(Path.Combine(protectedDirectory.Path, ".env")),
            Is.EqualTo(protectedOriginal));
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
    public async Task LocalOptionMutationPreservesUntouchedJsonTypesAndShape()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", """
            Ordinary=value
            SUPPROCOM_LOCAL_OPTIONS={
              "Enabled": true,
              "Retries": 3,
              "Missing": null,
              "Nested": {
                "Value": "before",
                "Other": false,
              },
              "Hosts": ["a", "b"],
            }
            """);

        var store = new SupprocomSecretFileStore(new SupprocomSecretFileOptions { Directory = directory.Path });
        await store.SetAsync("nested:value", "after");
        AssertLocalJsonTypes(directory.Path, expectedNestedValue: "after");

        await store.DeleteAsync("NESTED:VALUE");
        AssertLocalJsonTypes(directory.Path, expectedNestedValue: null);
    }

    [Test]
    public async Task ConcurrentMutationsDoNotLoseACommit()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "A=zero\n");
        var first = new SupprocomSecretFileStore(new SupprocomSecretFileOptions { Directory = directory.Path });
        var second = new SupprocomSecretFileStore(new SupprocomSecretFileOptions { Directory = directory.Path });

        await Task.WhenAll(first.SetAsync("A", "one"), second.SetAsync("B", "two"));

        IReadOnlyDictionary<string, string> values = await first.LoadAsync();
        Assert.That(values["A"], Is.EqualTo("one"));
        Assert.That(values["B"], Is.EqualTo("two"));
    }

    [Test]
    public async Task ReplaceIsSerializedWithSetDeleteAndProtection()
    {
        await AssertReplacementWinsAsync(delete: false);
        await AssertReplacementWinsAsync(delete: true);

        static async Task AssertReplacementWinsAsync(bool delete)
        {
            using var directory = new TemporaryDirectory();
            Write(directory.Path, ".env", "A=old\n");
            var keyStore = new BlockingInstallationKeyStore();
            var options = new SupprocomSecretFileOptions
            {
                Directory = directory.Path,
                Protection = SecretFileProtection.InstallationBoundAesGcm,
                InstallationKeyStore = keyStore
            };
            var mutator = new SupprocomSecretFileStore(options);
            var replacer = new SupprocomSecretFileStore(options);

            Task mutation = delete
                ? mutator.DeleteAsync("A")
                : mutator.SetAsync("B", "from-mutation");
            await keyStore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task replacement = replacer.ReplaceDocumentAsync("A=from-replace\n");
            try
            {
                await Task.Delay(50);
                Assert.That(replacement.IsCompleted, Is.False);
            }
            finally
            {
                keyStore.Release.TrySetResult(null);
            }

            await Task.WhenAll(mutation, replacement);
            IReadOnlyDictionary<string, string> values = await new SupprocomSecretFileStore(options).LoadAsync();
            Assert.That(values["A"], Is.EqualTo("from-replace"));
            Assert.That(values.ContainsKey("B"), Is.False);
        }
    }

    [Test]
    public async Task MutationIsSerializedWithOneTimeImport()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "{\"Imported\":\"value\"}\n");
        var keyStore = new BlockingInstallationKeyStore();
        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Import = SecretFileImport.JsonWithCommentsOnce,
            Protection = SecretFileProtection.InstallationBoundAesGcm,
            InstallationKeyStore = keyStore
        };
        var importer = new SupprocomSecretFileStore(options);
        var mutator = new SupprocomSecretFileStore(options);

        Task import = importer.LoadAsync();
        await keyStore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task mutation = mutator.SetAsync("After", "mutation");
        try
        {
            await Task.Delay(50);
            Assert.That(mutation.IsCompleted, Is.False);
        }
        finally
        {
            keyStore.Release.TrySetResult(null);
        }

        await Task.WhenAll(import, mutation);
        IReadOnlyDictionary<string, string> values = await new SupprocomSecretFileStore(options).LoadAsync();
        Assert.That(values["Imported"], Is.EqualTo("value"));
        Assert.That(values["After"], Is.EqualTo("mutation"));
    }

    [Test]
    public async Task MutationIsSerializedWithQuarantineRecovery()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "not-a-valid-assignment\n");
        Write(directory.Path, ".env.template", "Recovered=value\n");
        var keyStore = new BlockingInstallationKeyStore();
        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Recovery = SecretFileRecovery.QuarantineAndRestoreTemplate,
            Protection = SecretFileProtection.InstallationBoundAesGcm,
            InstallationKeyStore = keyStore
        };
        var recovery = new SupprocomSecretFileStore(options);
        var mutator = new SupprocomSecretFileStore(options);

        Task recoveryTask = recovery.LoadAsync();
        await keyStore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task mutation = mutator.SetAsync("After", "mutation");
        try
        {
            await Task.Delay(50);
            Assert.That(mutation.IsCompleted, Is.False);
        }
        finally
        {
            keyStore.Release.TrySetResult(null);
        }

        await Task.WhenAll(recoveryTask, mutation);
        IReadOnlyDictionary<string, string> values = await new SupprocomSecretFileStore(options).LoadAsync();
        Assert.That(values["Recovered"], Is.EqualTo("value"));
        Assert.That(values["After"], Is.EqualTo("mutation"));
        Assert.That(Directory.GetFiles(directory.Path, ".unreadable-*"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task InvalidSourceReplacementLeavesActiveBytesUntouched()
    {
        using var directory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes("Existing=unchanged\n");
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), original);
        var store = new SupprocomSecretFileStore(new SupprocomSecretFileOptions { Directory = directory.Path });

        SupprocomSecretsException exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await store.ReplaceDocumentAsync("SUPPROCOM_SECRET_SOURCE=env://\n"))!;

        Assert.That(exception.Code, Is.EqualTo("UnnecessaryEnvSource"));
        Assert.That(File.ReadAllBytes(Path.Combine(directory.Path, ".env")), Is.EqualTo(original));
    }

    [Test]
    public async Task InvalidTemplateAndImportLeaveActiveStateUntouched()
    {
        using var templateDirectory = new TemporaryDirectory();
        Write(templateDirectory.Path, ".env.template", "SUPPROCOM_SECRET_SOURCE=env://\n");
        var templateStore = new SupprocomSecretFileStore(
            new SupprocomSecretFileOptions { Directory = templateDirectory.Path });

        SupprocomSecretsException templateException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await templateStore.LoadAsync())!;
        Assert.That(templateException.Code, Is.EqualTo("UnnecessaryEnvSource"));
        Assert.That(File.Exists(Path.Combine(templateDirectory.Path, ".env")), Is.False);

        using var importDirectory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes("{\"SUPPROCOM_SECRET_SOURCE\":\"env://\"}");
        File.WriteAllBytes(Path.Combine(importDirectory.Path, ".env"), original);
        var importStore = new SupprocomSecretFileStore(new SupprocomSecretFileOptions
        {
            Directory = importDirectory.Path,
            Import = SecretFileImport.JsonWithCommentsOnce
        });

        SupprocomSecretsException importException = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await importStore.LoadAsync())!;
        Assert.That(importException.Code, Is.EqualTo("UnnecessaryEnvSource"));
        Assert.That(File.ReadAllBytes(Path.Combine(importDirectory.Path, ".env")), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(importDirectory.Path, ".pre-supprocom-import-*"), Has.Length.EqualTo(0));
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
    public void RecoveryPreparesBeforeQuarantineWhenReplacementCannotBeProtected()
    {
        using var directory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes("not-an-assignment\n");
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), original);
        Write(directory.Path, ".env.template", "Recovered=yes\n");
        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Recovery = SecretFileRecovery.QuarantineAndRestoreTemplate,
            Protection = SecretFileProtection.InstallationBoundAesGcm,
            InstallationKeyStore = new FailingKeyStore()
        };

        SupprocomSecretsException exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await new SupprocomSecretFileStore(options).LoadAsync())!;

        Assert.That(exception.Code, Is.EqualTo("InstallationKeyUnavailable"));
        Assert.That(File.ReadAllBytes(Path.Combine(directory.Path, ".env")), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(directory.Path, ".unreadable-*"), Has.Length.EqualTo(0));
    }

    [Test]
    public void ConfigurationHonorsExplicitStartupCancellation()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "SUPPROCOM_SECRET_SOURCE=slow://vault/application\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        SupprocomSecretsException exception = Assert.Throws<SupprocomSecretsException>(() => new ConfigurationBuilder()
            .AddSupprocomSecretsProvider(new SlowProvider())
            .AddSupprocomSecrets(options =>
            {
                options.File.Directory = directory.Path;
                options.StartupCancellationToken = cancellation.Token;
            })
            .Build())!;

        Assert.That(exception.Code, Is.EqualTo("ConfigurationLoadCancelled"));
    }

    [Test]
    public void InstallationKeyCreationCancellationLeavesNoFinalOrTemporaryKey()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "installation.key");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new FileInstallationKeyStore(path).GetOrCreateKeyAsync(cancellation.Token));

        Assert.That(File.Exists(path), Is.False);
        Assert.That(Directory.GetFiles(directory.Path, ".supprocom-key-*.tmp"), Has.Length.EqualTo(0));
    }

    [Test]
    public void InstallationKeyDirectoryFailureDoesNotCreateAStrandedKey()
    {
        using var directory = new TemporaryDirectory();
        string parent = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(parent, "occupied", new UTF8Encoding(false));
        string path = Path.Combine(parent, "installation.key");

        SupprocomSecretsException exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await new FileInstallationKeyStore(path).GetOrCreateKeyAsync())!;

        Assert.That(exception.Code, Is.EqualTo("InstallationKeyCreationFailed"));
        Assert.That(Directory.GetFiles(directory.Path, ".supprocom-key-*.tmp"), Has.Length.EqualTo(0));
    }

    [Test]
    public void TruncatedInstallationKeyIsRejected()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "installation.key");
        File.WriteAllBytes(path, new byte[5]);

        SupprocomSecretsException exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await new FileInstallationKeyStore(path).GetOrCreateKeyAsync())!;

        Assert.That(exception.Code, Is.EqualTo("InvalidInstallationKey"));
    }

    #pragma warning disable CA1416
    [Test]
    public async Task ExistingWindowsKeyDaclRemovesExplicitForeignReadAccess()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("The installation-key DACL contract is Windows-specific.");

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "installation.key");
        byte[] expectedKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        File.WriteAllBytes(path, expectedKey);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier currentUser = identity.User
            ?? throw new InvalidOperationException("The test identity has no SID.");
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var initial = new FileSecurity();
        initial.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        initial.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        initial.AddAccessRule(new FileSystemAccessRule(
            everyone,
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(initial);

        Assert.That(await new FileInstallationKeyStore(path).GetOrCreateKeyAsync(), Is.EqualTo(expectedKey));

        FileSecurity after = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var rules = after
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        Assert.That(
            rules.Any(rule => rule.IdentityReference is SecurityIdentifier sid && sid.Equals(everyone)),
            Is.False);
    }
    #pragma warning restore CA1416

    [Test]
    public async Task ConcurrentInstallationKeyCreatorsConvergeOnOneCompleteKey()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "installation.key");
        Task<byte[]> first = new FileInstallationKeyStore(path).GetOrCreateKeyAsync();
        Task<byte[]> second = new FileInstallationKeyStore(path).GetOrCreateKeyAsync();
        byte[][] keys = await Task.WhenAll(first, second);

        Assert.That(keys[0], Is.EqualTo(keys[1]));
        Assert.That(File.ReadAllBytes(path), Has.Length.EqualTo(32));
        Assert.That(Directory.GetFiles(directory.Path, ".supprocom-key-*.tmp"), Has.Length.EqualTo(0));
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

    [TestCase("[]", "InvalidProviderManifest")]
    [TestCase("{\"version\":999999999999,\"providers\":[]}", "InvalidProviderManifest")]
    public void MalformedProviderManifestHasPreciseValueFreeDiagnostics(string manifestText, string code)
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "SUPPROCOM_SECRET_SOURCE=manifest://vault/application\n");
        string manifest = Path.Combine(directory.Path, "supprocom-secrets.providers.json");
        File.WriteAllText(manifest, manifestText, new UTF8Encoding(false));

        SupprocomSecretsException exception = Assert.Throws<SupprocomSecretsException>(() => new ConfigurationBuilder()
            .AddSupprocomSecrets(options =>
            {
                options.File.Directory = directory.Path;
                options.ProviderManifestPath = manifest;
            })
            .Build())!;

        Assert.That(exception.Code, Is.EqualTo(code));
        Assert.That(exception.ToString(), Does.Not.Contain("secret-value"));
    }

    [Test]
    public void ProviderWithoutParameterlessConstructorHasPreciseDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.Path, ".env", "SUPPROCOM_SECRET_SOURCE=ctor://vault/application\n");
        string manifest = Path.Combine(directory.Path, "supprocom-secrets.providers.json");
        string assembly = typeof(NoDefaultConstructorProvider).Assembly.GetName().Name!;
        File.WriteAllText(
            manifest,
            $$"""
            {
              "version": 1,
              "providers": [
                {
                  "scheme": "ctor",
                  "assembly": "{{assembly}}",
                  "providerType": "{{typeof(NoDefaultConstructorProvider).FullName}}"
                }
              ]
            }
            """,
            new UTF8Encoding(false));

        SupprocomSecretsException exception = Assert.Throws<SupprocomSecretsException>(() => new ConfigurationBuilder()
            .AddSupprocomSecrets(options =>
            {
                options.File.Directory = directory.Path;
                options.ProviderManifestPath = manifest;
            })
            .Build())!;

        Assert.That(exception.Code, Is.EqualTo("ProviderInstantiationFailed"));
        Assert.That(exception.ToString(), Does.Not.Contain("secret-value"));
    }

    [Test]
    public async Task FailedProtectedMutationsLeaveTheActiveDocumentUnchanged()
    {
        await AssertProtectedWriteFailure(
            async store => await store.SetAsync("A", "new"));
        await AssertProtectedWriteFailure(
            async store => await store.DeleteAsync("A"));
        await AssertProtectedWriteFailure(
            async store => await store.ReplaceDocumentAsync("A=replaced\n"));
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

    private static IReadOnlyDictionary<string, string> CanonicalImportProjection(byte[] source)
    {
        using JsonDocument document = JsonDocument.Parse(
            source,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("SUPPROCOM_LOCAL_OPTIONS", StringComparison.OrdinalIgnoreCase))
                continue;

            AddCanonicalImportProjection(expected, property.Value, property.Name);
        }

        if (document.RootElement.TryGetProperty("SUPPROCOM_LOCAL_OPTIONS", out JsonElement localOptions) &&
            localOptions.ValueKind == JsonValueKind.Object)
        {
            AddCanonicalImportProjection(expected, localOptions, string.Empty);
        }

        return expected;
    }

    private static void AddCanonicalImportProjection(
        IDictionary<string, string> expected,
        JsonElement element,
        string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (!element.EnumerateObject().Any())
                {
                    if (path.Length != 0)
                        expected[path] = string.Empty;
                    return;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string childPath = path.Length == 0 ? property.Name : $"{path}:{property.Name}";
                    AddCanonicalImportProjection(expected, property.Value, childPath);
                }

                return;
            }

            case JsonValueKind.Array:
            {
                if (element.GetArrayLength() == 0)
                {
                    expected[path] = string.Empty;
                    return;
                }

                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                    AddCanonicalImportProjection(expected, item, $"{path}:{index++}");

                return;
            }

            case JsonValueKind.String:
                expected[path] = element.GetString() ?? string.Empty;
                return;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                expected[path] = element.GetRawText();
                return;
            case JsonValueKind.Null:
                expected[path] = string.Empty;
                return;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }

    private static void AssertConfigurationProjection(
        IConfiguration configuration,
        IReadOnlyDictionary<string, string> expected)
    {
        HashSet<string> actualKeys = configuration.AsEnumerable()
            .Where(item => item.Value is not null || expected.ContainsKey(item.Key))
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That(actualKeys, Is.EquivalentTo(expected.Keys));
        foreach (KeyValuePair<string, string> item in expected)
            Assert.That(configuration[item.Key], Is.EqualTo(item.Value), item.Key);
    }

    private static void AssertLocalJsonTypes(string directory, string? expectedNestedValue)
    {
        string document = File.ReadAllText(Path.Combine(directory, ".env"));
        const string marker = "SUPPROCOM_LOCAL_OPTIONS=";
        int start = document.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        using JsonDocument json = JsonDocument.Parse(document[(start + marker.Length)..]);
        JsonElement root = json.RootElement;

        Assert.That(root.GetProperty("Enabled").ValueKind, Is.EqualTo(JsonValueKind.True));
        Assert.That(root.GetProperty("Retries").GetInt32(), Is.EqualTo(3));
        Assert.That(root.GetProperty("Missing").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(root.GetProperty("Nested").GetProperty("Other").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("Hosts").EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { "a", "b" }));

        bool hasValue = root.GetProperty("Nested").TryGetProperty("Value", out JsonElement value);
        Assert.That(hasValue, Is.EqualTo(expectedNestedValue is not null));
        if (expectedNestedValue is not null)
            Assert.That(value.GetString(), Is.EqualTo(expectedNestedValue));
    }

    private static async Task AssertProtectedWriteFailure(
        Func<SupprocomSecretFileStore, Task> mutation)
    {
        using var directory = new TemporaryDirectory();
        byte[] original = Encoding.UTF8.GetBytes("A=old\n");
        File.WriteAllBytes(Path.Combine(directory.Path, ".env"), original);
        var options = new SupprocomSecretFileOptions
        {
            Directory = directory.Path,
            Protection = SecretFileProtection.InstallationBoundAesGcm,
            InstallationKeyStore = new FailingKeyStore()
        };

        SupprocomSecretsException exception = Assert.ThrowsAsync<SupprocomSecretsException>(
            async () => await mutation(new SupprocomSecretFileStore(options)))!;

        Assert.That(exception.Code, Is.EqualTo("InstallationKeyUnavailable"));
        Assert.That(File.ReadAllBytes(Path.Combine(directory.Path, ".env")), Is.EqualTo(original));
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

    private sealed class FailingKeyStore : IInstallationKeyStore
    {
        public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default) =>
            throw new SupprocomSecretsException(
                "InstallationKeyUnavailable",
                "The test installation key is unavailable.");
    }

    private sealed class BlockingInstallationKeyStore : IInstallationKeyStore
    {
        private readonly byte[] _key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        private int _calls;

        public TaskCompletionSource<object?> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult(null);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return _key.ToArray();
        }
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

public sealed class NoDefaultConstructorProvider : ISecretStoreProvider
{
    public NoDefaultConstructorProvider(string required) => _ = required;

    public string Scheme => "ctor";

    public Task<ISecretStore> OpenAsync(Uri source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
