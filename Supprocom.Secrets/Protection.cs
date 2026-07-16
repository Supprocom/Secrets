using System.Security.Cryptography;
using System.Text;

namespace Supprocom.Secrets;

public sealed class FileInstallationKeyStore : IInstallationKeyStore
{
    private readonly string _path;

    public FileInstallationKeyStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A key path is required.", nameof(path));

        _path = Path.GetFullPath(path);
    }

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(_path))
            return await ReadKeyAsync(cancellationToken).ConfigureAwait(false);

        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await using FileStream stream = new(
                _path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(key, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            TryRestrictUnixPermissions(_path);
            return key;
        }
        catch (IOException) when (File.Exists(_path))
        {
            CryptographicOperations.ZeroMemory(key);
            return await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadKeyAsync(CancellationToken cancellationToken)
    {
        byte[] key = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new SupprocomSecretsException(
                "InvalidInstallationKey",
                $"Installation key file '{_path}' must contain a 32-byte key.");
        }

        return key;
    }

    private static void TryRestrictUnixPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
