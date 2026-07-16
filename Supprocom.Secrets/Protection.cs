using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Supprocom.Secrets;

public sealed class FileInstallationKeyStore : IInstallationKeyStore
{
    private const int KeySize = 32;
    private const int MaxConcurrentAttempts = 6;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _path;

    internal static Func<CancellationToken, Task>? LegacyKeyMigrationBeforeInstallHook { get; set; }

    public FileInstallationKeyStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A key path is required.", nameof(path));

        _path = Path.GetFullPath(path);
    }

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim keyLock = KeyLocks.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetOrCreateKeyCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            keyLock.Release();
        }
    }

    private async Task<byte[]> GetOrCreateKeyCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? directory = Path.GetDirectoryName(_path);
        try
        {
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SupprocomSecretsException(
                "InstallationKeyCreationFailed",
                $"Unable to create the installation key directory for '{_path}'.",
                exception);
        }

        for (int attempt = 0; attempt < MaxConcurrentAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
                return await ReadKeyAsync(cancellationToken).ConfigureAwait(false);

            byte[] key = RandomNumberGenerator.GetBytes(KeySize);
            string temporary = Path.Combine(
                directory ?? Path.GetTempPath(),
                $".supprocom-key-{Guid.NewGuid():N}.tmp");
            bool installed = false;
            try
            {
                await using (FileStream stream = new(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 4096,
                                 options: FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    RestrictKeyPermissions(temporary);
                    await stream.WriteAsync(key, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (new FileInfo(temporary).Length != KeySize)
                {
                    throw new SupprocomSecretsException(
                        "InvalidInstallationKey",
                        "The temporary installation key did not contain 32 bytes after flushing.");
                }

                try
                {
                    File.Move(temporary, _path);
                    installed = true;
                    return key;
                }
                catch (IOException) when (File.Exists(_path))
                {
                    if (attempt + 1 >= MaxConcurrentAttempts)
                    {
                        throw new SupprocomSecretsException(
                            "InstallationKeyCreationFailed",
                            $"Another process won installation key creation for '{_path}', but its key could not be read in time.");
                    }
                }
            }
            catch (SupprocomSecretsException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException exception) when (!File.Exists(_path))
            {
                throw new SupprocomSecretsException(
                    "InstallationKeyCreationFailed",
                    $"Unable to create installation key '{_path}'.",
                    exception);
            }
            catch (UnauthorizedAccessException exception) when (!File.Exists(_path))
            {
                throw new SupprocomSecretsException(
                    "InstallationKeyCreationFailed",
                    $"Unable to create installation key '{_path}'.",
                    exception);
            }
            finally
            {
                if (!installed)
                    CryptographicOperations.ZeroMemory(key);
                DeleteTemporary(temporary);
            }

            if (attempt + 1 < MaxConcurrentAttempts)
                await RetryConcurrentReadAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        throw new SupprocomSecretsException(
            "InstallationKeyCreationFailed",
            $"Unable to establish installation key '{_path}' after bounded concurrent-creation retries.");
    }

    private async Task<byte[]> ReadKeyAsync(CancellationToken cancellationToken)
    {
        RestrictKeyPermissions(_path);

        byte[] key;
        try
        {
            key = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                PlatformNotSupportedException)
        {
            throw new SupprocomSecretsException(
                "InstallationKeyReadFailed",
                $"Unable to read installation key '{_path}'.",
                exception);
        }

        if (key.Length == KeySize)
            return key;

        byte[] decodedKey;
        try
        {
            decodedKey = DecodeLegacyKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            await MigrateLegacyKeyAsync(decodedKey, cancellationToken).ConfigureAwait(false);
            return decodedKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(decodedKey);
            throw;
        }
    }

    private byte[] DecodeLegacyKey(byte[] content)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidLegacyKey("The installation key file is not valid UTF-8 Base64 text.", exception);
        }

        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace) || trimmed.Any(character => character > 0x7F))
        {
            throw InvalidLegacyKey(
                "The installation key file must contain only one ASCII Base64 value with optional surrounding whitespace.");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(trimmed);
        }
        catch (FormatException exception)
        {
            throw InvalidLegacyKey("The installation key file contains malformed Base64 text.", exception);
        }

        if (decoded.Length != KeySize ||
            !string.Equals(Convert.ToBase64String(decoded), trimmed, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw InvalidLegacyKey("The installation key Base64 value must decode to exactly 32 bytes.");
        }

        return decoded;
    }

    private async Task MigrateLegacyKeyAsync(byte[] key, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_path) ?? Path.GetTempPath();
        string temporary = Path.Combine(directory, $".supprocom-key-{Guid.NewGuid():N}.tmp");
        bool installed = false;
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                RestrictKeyPermissions(temporary);
                await stream.WriteAsync(key, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(temporary).Length != KeySize)
            {
                throw new SupprocomSecretsException(
                    "InstallationKeyMigrationFailed",
                    "The canonical installation key replacement did not contain 32 bytes after flushing.");
            }

            Func<CancellationToken, Task>? hook = LegacyKeyMigrationBeforeInstallHook;
            if (hook is not null)
                await hook(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            installed = true;
            RestrictKeyPermissions(_path);
        }
        catch (SupprocomSecretsException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SupprocomSecretsException(
                "InstallationKeyMigrationFailed",
                $"Unable to replace the legacy installation key file '{_path}' with its canonical raw form.",
                exception);
        }
        finally
        {
            if (!installed)
                DeleteTemporary(temporary);
        }
    }

    private SupprocomSecretsException InvalidLegacyKey(string reason, Exception? inner = null) =>
        inner is null
            ? new SupprocomSecretsException(
                "InvalidInstallationKey",
                $"Installation key file '{_path}' is invalid. {reason}")
            : new SupprocomSecretsException(
                "InvalidInstallationKey",
                $"Installation key file '{_path}' is invalid. {reason}",
                inner);

    private static async Task RetryConcurrentReadAsync(
        int attempt,
        CancellationToken cancellationToken) =>
        await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken)
            .ConfigureAwait(false);

    private static void DeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RestrictKeyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                SecurityIdentifier? user = identity.User;
                if (user is null)
                    throw new InvalidOperationException("The current Windows identity has no security identifier.");

                var file = new FileInfo(path);
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.SetAccessRule(new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                file.SetAccessControl(security);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new SupprocomSecretsException(
                    "InstallationKeyPermissions",
                    $"Unable to restrict installation key permissions for '{path}'.",
                    exception);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                PlatformNotSupportedException)
            {
                throw new SupprocomSecretsException(
                    "InstallationKeyPermissions",
                    $"Unable to restrict installation key permissions for '{path}'.",
                    exception);
            }
        }
    }
}

internal static class SecretFileProtectionCodec
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MinimumEnvelopeSize = 1 + NonceSize + TagSize;

    public static bool IsEnvelope(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= MinimumEnvelopeSize && bytes[0] == Version;

    public static byte[] Encrypt(string plaintext, ReadOnlySpan<byte> key)
    {
        ValidateKey(key);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plainBytes, cipher, tag);

            byte[] envelope = new byte[1 + nonce.Length + cipher.Length + tag.Length];
            envelope[0] = Version;
            nonce.CopyTo(envelope, 1);
            cipher.CopyTo(envelope, 1 + nonce.Length);
            tag.CopyTo(envelope, 1 + nonce.Length + cipher.Length);
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public static string Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, string path)
    {
        ValidateKey(key);
        if (!IsEnvelope(envelope))
        {
            throw new SupprocomSecretsException(
                "InvalidProtectedDocument",
                $"Protected document '{path}' has an unsupported envelope.");
        }

        ReadOnlySpan<byte> nonce = envelope.Slice(1, NonceSize);
        ReadOnlySpan<byte> cipher = envelope.Slice(1 + NonceSize, envelope.Length - MinimumEnvelopeSize);
        ReadOnlySpan<byte> tag = envelope.Slice(envelope.Length - TagSize, TagSize);
        byte[] plaintext = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new SupprocomSecretsException(
                "ProtectedDocumentAuthentication",
                $"Unable to authenticate protected document '{path}'.",
                exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SupprocomSecretsException(
                "ProtectedDocumentEncoding",
                $"Protected document '{path}' is not valid UTF-8.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new SupprocomSecretsException("InvalidInstallationKey", "Installation key must contain 32 bytes.");
    }
}
