namespace Supprocom.Secrets;

public enum SecretFileImport
{
    None,
    JsonWithCommentsOnce
}

public enum SecretFileComposition
{
    Replace,
    Overlay
}

public enum SecretFileRecovery
{
    None,
    QuarantineAndRestoreTemplate
}

public enum SecretFileProtection
{
    None,
    InstallationBoundAesGcm
}

public sealed class SupprocomSecretsOptions
{
    public SupprocomSecretFileOptions File { get; } = new();

    public string? EnvironmentName { get; set; }

    public bool FileOverridesProcessEnvironment { get; set; }

    public string? ProviderManifestPath { get; set; }

    public TimeSpan ProviderLoadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public CancellationToken StartupCancellationToken { get; set; }

    public IList<ISecretStoreProvider> ProviderAdapters { get; } = new List<ISecretStoreProvider>();
}

public sealed class SupprocomSecretFileOptions
{
    public string? Directory { get; set; }

    public string ActiveName { get; set; } = ".env";

    public string? DevelopmentName { get; set; }

    public string TemplateName { get; set; } = ".env.template";

    public string DevelopmentTemplateName { get; set; } = ".dev.env.template";

    public string DevelopmentReplacementTemplateName { get; set; } = ".env.development.template";

    public SecretFileImport Import { get; set; } = SecretFileImport.None;

    public SecretFileComposition DevelopmentComposition { get; set; } = SecretFileComposition.Overlay;

    public SecretFileRecovery Recovery { get; set; } = SecretFileRecovery.None;

    public SecretFileProtection Protection { get; set; } = SecretFileProtection.None;

    public string? InstallationKeyPath { get; set; }

    public IInstallationKeyStore? InstallationKeyStore { get; set; }
}
