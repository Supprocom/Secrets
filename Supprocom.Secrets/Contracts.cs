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

/// <summary>
/// Atomically transforms the active canonical local document while coordinating with the package file writer.
/// </summary>
public interface ISecretDocumentUpdater
{
    /// <summary>
    /// Invokes a synchronous, non-reentrant transform exactly once with an immutable normalized snapshot while
    /// the package writer gate is held. Pointer and local-options documents are rejected, and the transformed
    /// output is parsed, validated, serialized canonically, and installed atomically. Callback, output,
    /// cancellation, or write failure restores the prior active state; if restoration fails, the package reports
    /// <c>DocumentUpdateRollbackFailed</c> with the original failure retained as evidence.
    /// </summary>
    /// <param name="update">
    /// A synchronous transform that must not call this store or re-enter the update operation. Throwing aborts the
    /// update before the new document is committed.
    /// </param>
    /// <param name="cancellationToken">The cancellation token for the update and its atomic commit.</param>
    Task UpdateDocumentAsync(
        Func<IReadOnlyList<SupprocomSecretSetting>, IReadOnlyList<SupprocomSecretSetting>> update,
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
