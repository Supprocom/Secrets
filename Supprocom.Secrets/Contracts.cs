namespace Supprocom.Secrets;

public interface ISecretStore
{
    Task<IReadOnlyDictionary<string, string>> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);
}

public interface ISecretDocumentStore
{
    Task<string> ReadDocumentAsync(
        CancellationToken cancellationToken = default);

    Task ReplaceDocumentAsync(
        string document,
        CancellationToken cancellationToken = default);
}

public enum SecretFileProtectionState
{
    Missing,
    Plaintext,
    Protected
}

public interface ISecretFileProtectionManager
{
    Task<SecretFileProtectionState> GetStateAsync(
        CancellationToken cancellationToken = default);

    Task UnprotectAsync(
        CancellationToken cancellationToken = default);
}

public interface ISecretStoreProvider
{
    string Scheme { get; }

    Task<ISecretStore> OpenAsync(
        Uri source,
        CancellationToken cancellationToken = default);
}

public interface IInstallationKeyStore
{
    Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default);

    Task<byte[]> ReadExistingKeyAsync(CancellationToken cancellationToken = default) =>
        throw new SupprocomSecretsException(
            "InstallationKeyUnavailable",
            "The configured installation key store cannot read an existing key without creating one.");
}
