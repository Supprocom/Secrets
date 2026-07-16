using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Supprocom.Secrets;

public sealed class SupprocomSecretFileStore : ISecretStore, ISecretDocumentStore
{
    private readonly SecretFileRuntime _runtime;

    public SupprocomSecretFileStore(SupprocomSecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _runtime = new SecretFileRuntime(options);
    }

    public SupprocomSecretFileStore(SupprocomSecretFileOptions options, string? environmentName = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = new SupprocomSecretsOptions
        {
            EnvironmentName = environmentName
        };
        CopyFileOptions(options, root.File);
        _runtime = new SecretFileRuntime(root);
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        FileLoadResult result = await _runtime.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (result.SourceDirective is not null)
        {
            throw new SupprocomSecretsException(
                "ExternalProviderPointer",
                "The local file contains SUPPROCOM_SECRET_SOURCE and is not itself the selected secret store.");
        }

        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(result.Values, StringComparer.OrdinalIgnoreCase));
    }

    public async Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        await _runtime.MutateAsync(key, value, delete: false, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _runtime.MutateAsync(key, value: null, delete: true, cancellationToken);
    }

    public Task<string> ReadDocumentAsync(CancellationToken cancellationToken = default) =>
        _runtime.ReadDocumentAsync(cancellationToken);

    public Task ReplaceDocumentAsync(string document, CancellationToken cancellationToken = default) =>
        _runtime.ReplaceDocumentAsync(document, cancellationToken);

    private static void CopyFileOptions(
        SupprocomSecretFileOptions source,
        SupprocomSecretFileOptions target)
    {
        target.Directory = source.Directory;
        target.ActiveName = source.ActiveName;
        target.DevelopmentName = source.DevelopmentName;
        target.TemplateName = source.TemplateName;
        target.DevelopmentTemplateName = source.DevelopmentTemplateName;
        target.DevelopmentReplacementTemplateName = source.DevelopmentReplacementTemplateName;
        target.Import = source.Import;
        target.DevelopmentComposition = source.DevelopmentComposition;
        target.Recovery = source.Recovery;
        target.Protection = source.Protection;
        target.InstallationKeyPath = source.InstallationKeyPath;
        target.InstallationKeyStore = source.InstallationKeyStore;
    }
}

internal sealed class SecretFileRuntime
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriterLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static Func<CancellationToken, Task>? ImportPreservationBeforeInstallHook { get; set; }

    private readonly SupprocomSecretsOptions _options;
    private readonly SupprocomSecretFileOptions _fileOptions;
    private string? _cachedKeyPath;

    public SecretFileRuntime(SupprocomSecretsOptions options)
    {
        _options = options;
        _fileOptions = options.File;
    }

    public string DirectoryPath => Path.GetFullPath(
        _fileOptions.Directory ?? Path.Combine(AppContext.BaseDirectory, "Environment"));

    public async Task<FileLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFileNames();
        Directory.CreateDirectory(DirectoryPath);
        DevelopmentSelection? development = SelectDevelopment();

        var files = new List<PhysicalFile>();
        if (development.HasValue && development.Value.Composition == SecretFileComposition.Replace)
        {
            files.Add(await LoadPhysicalAsync(
                    development.Value.Name,
                    development.Value.TemplateName,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        else
        {
            PhysicalFile baseFile = await LoadPhysicalAsync(
                    _fileOptions.ActiveName,
                    _fileOptions.TemplateName,
                    cancellationToken)
                .ConfigureAwait(false);
            files.Add(baseFile);
            if (development.HasValue)
            {
                files.Add(await LoadPhysicalAsync(
                        development.Value.Name,
                        development.Value.TemplateName,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        string? source = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhysicalFile file in files)
        {
            if (file.Document.SourceDirective is not null)
            {
                if (source is not null)
                {
                    throw new SupprocomSecretsException(
                        "DuplicateSecretSource",
                        $"SUPPROCOM_SECRET_SOURCE is declared more than once across the composed files.");
                }

                source = file.Document.SourceDirective;
            }

            foreach (KeyValuePair<string, string> item in file.Document.Values)
                values[item.Key] = item.Value;
        }

        if (source is not null && values.Count != 0)
        {
            throw new SupprocomSecretsException(
                "PointerFileContainsConfiguration",
                "When SUPPROCOM_SECRET_SOURCE is selected, the composed dotenv files may contain only that directive, SUPPROCOM_LOCAL_OPTIONS, comments, and blank lines.");
        }

        var localOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhysicalFile file in files)
        {
            foreach (KeyValuePair<string, string> item in file.Document.LocalOptions)
                localOptions[item.Key] = item.Value;
        }

        foreach (KeyValuePair<string, string> item in localOptions)
            values[item.Key] = item.Value;

        if (source is not null)
            ValidateSourceDirective(source);

        return new FileLoadResult(
            values,
            source,
            localOptions,
            files.Count == 0 ? null : files[^1].Document);
    }

    public async Task<string> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFileNames();
        Directory.CreateDirectory(DirectoryPath);
        PhysicalFile file = await LoadPhysicalAsync(
                _fileOptions.ActiveName,
                _fileOptions.TemplateName,
                cancellationToken)
            .ConfigureAwait(false);
        return file.Document.RawText;
    }

    public async Task ReplaceDocumentAsync(string document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFileNames();
        Directory.CreateDirectory(DirectoryPath);
        string activePath = Path.Combine(DirectoryPath, _fileOptions.ActiveName);
        ParsedSecretDocument parsed = SecretDocumentParser.Parse(document, activePath);
        ValidateDocumentSemantics(parsed);
        SemaphoreSlim writerLock = GetWriterLock(activePath);
        await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(activePath, document, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writerLock.Release();
        }
    }

    public async Task MutateAsync(
        string key,
        string? value,
        bool delete,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFileNames();
        string normalizedKey = NormalizeMutationKey(key);
        Directory.CreateDirectory(DirectoryPath);
        string activePath = Path.Combine(DirectoryPath, _fileOptions.ActiveName);
        SemaphoreSlim writerLock = GetWriterLock(activePath);
        await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PhysicalFile physical = await LoadPhysicalCoreAsync(
                    _fileOptions.ActiveName,
                    _fileOptions.TemplateName,
                    cancellationToken)
                .ConfigureAwait(false);
            ParsedSecretDocument document = physical.Document;

            if (document.SourceDirective is not null)
            {
                throw new SupprocomSecretsException(
                    "ExternalProviderPointer",
                    "The pointer file cannot be mutated through the built-in local store.");
            }

            bool isLocal = document.LocalOptions.ContainsKey(normalizedKey);
            if (isLocal)
            {
                JsonNode local = document.LocalOptionsNode
                    ?? throw new SupprocomSecretsException(
                        "InvalidLocalOptions",
                        "The local-options document structure is unavailable for mutation.");
                if (!MutateLocalNode(local, normalizedKey, value, delete))
                {
                    throw new SupprocomSecretsException(
                        "InvalidLocalOptions",
                        "The local-options document structure does not match its flattened configuration paths.");
                }

                document.LocalOptions.Clear();
                foreach (KeyValuePair<string, string> item in SecretDocumentParser.FlattenJsonNode(
                             local,
                             activePath,
                             "SUPPROCOM_LOCAL_OPTIONS"))
                {
                    document.LocalOptions[item.Key] = item.Value;
                }

                document.HasLocalOptions = document.LocalOptions.Count != 0;
            }
            else
            {
                if (delete)
                    document.Values.Remove(normalizedKey);
                else
                    document.Values[normalizedKey] = value!;
            }

            string serialized = SecretDocumentSerializer.Serialize(document);
            ParsedSecretDocument validated = SecretDocumentParser.Parse(serialized, activePath);
            ValidateDocumentSemantics(validated);
            await WriteAtomicAsync(activePath, serialized, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writerLock.Release();
        }
    }

    private async Task<PhysicalFile> LoadPhysicalAsync(
        string name,
        string templateName,
        CancellationToken cancellationToken)
    {
        string activePath = Path.Combine(DirectoryPath, name);
        SemaphoreSlim writerLock = GetWriterLock(activePath);
        await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadPhysicalCoreAsync(name, templateName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writerLock.Release();
        }
    }

    private async Task<PhysicalFile> LoadPhysicalCoreAsync(
        string name,
        string templateName,
        CancellationToken cancellationToken)
    {
        string activePath = Path.Combine(DirectoryPath, name);
        string templatePath = Path.Combine(DirectoryPath, templateName);

        if (!File.Exists(activePath) || new FileInfo(activePath).Length == 0)
        {
            if (!File.Exists(templatePath))
                return new PhysicalFile(
                    new ParsedSecretDocument(activePath, string.Empty),
                    activePath);

            return await CreateFromTemplateAsync(activePath, templatePath, cancellationToken)
                .ConfigureAwait(false);
        }

        byte[] originalBytes = await File.ReadAllBytesAsync(activePath, cancellationToken).ConfigureAwait(false);
        string plaintext;
        try
        {
            plaintext = await ReadPlaintextAsync(originalBytes, activePath, cancellationToken).ConfigureAwait(false);
        }
        catch (SupprocomSecretsException)
        {
            if (_fileOptions.Recovery != SecretFileRecovery.QuarantineAndRestoreTemplate)
                throw;

            return await RecoverAsync(activePath, templatePath, originalBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            ParsedSecretDocument parsed = SecretDocumentParser.Parse(plaintext, activePath);
            ValidateDocumentSemantics(parsed);
            await EnsureProtectedAsync(activePath, originalBytes, plaintext, cancellationToken).ConfigureAwait(false);
            return new PhysicalFile(parsed, activePath);
        }
        catch (SupprocomSecretsException parseException)
        {
            if (_fileOptions.Import != SecretFileImport.JsonWithCommentsOnce)
            {
                if (_fileOptions.Recovery != SecretFileRecovery.QuarantineAndRestoreTemplate)
                    throw;

                return await RecoverAsync(activePath, templatePath, originalBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (SecretDocumentParser.TryParseJsonObject(plaintext, activePath, out _, out _))
            {
                ParsedSecretDocument imported = JsonWithCommentsImporter.Import(plaintext, activePath);
                ValidateDocumentSemantics(imported);
                string canonical = SecretDocumentSerializer.Serialize(imported);
                ParsedSecretDocument reparsed = SecretDocumentParser.Parse(canonical, activePath);
                ValidateDocumentSemantics(reparsed);
                await PreserveOriginalAsync(activePath, originalBytes, cancellationToken).ConfigureAwait(false);
                await WriteAtomicAsync(activePath, canonical, null, cancellationToken).ConfigureAwait(false);
                return new PhysicalFile(reparsed, activePath);
            }

            if (_fileOptions.Recovery != SecretFileRecovery.QuarantineAndRestoreTemplate)
                throw new SupprocomSecretsException(
                    parseException.Code,
                    parseException.Message,
                    parseException);

            return await RecoverAsync(activePath, templatePath, originalBytes, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<PhysicalFile> CreateFromTemplateAsync(
        string activePath,
        string templatePath,
        CancellationToken cancellationToken)
    {
        byte[] templateBytes = await File.ReadAllBytesAsync(templatePath, cancellationToken).ConfigureAwait(false);
        ParsedSecretDocument template = ValidateTemplate(templateBytes, templatePath);
        await WriteAtomicAsync(activePath, template.RawText, templateBytes, cancellationToken).ConfigureAwait(false);
        ParsedSecretDocument active = SecretDocumentParser.Parse(template.RawText, activePath);
        return new PhysicalFile(active, activePath);
    }

    private async Task<PhysicalFile> RecoverAsync(
        string activePath,
        string templatePath,
        byte[] originalBytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(templatePath))
        {
            throw new SupprocomSecretsException(
                "RecoveryTemplateMissing",
                $"Cannot recover '{activePath}' because template '{templatePath}' does not exist.");
        }

        byte[] templateBytes = await File.ReadAllBytesAsync(templatePath, cancellationToken).ConfigureAwait(false);
        ParsedSecretDocument template = ValidateTemplate(templateBytes, templatePath);
        FilePermissionSnapshot? permissions = FilePermissionSnapshot.Capture(activePath);
        string? temporary = await PrepareAtomicFileAsync(
                activePath,
                template.RawText,
                templateBytes,
                cancellationToken,
                permissions)
            .ConfigureAwait(false);

        string quarantinePath = CreateUniqueSibling(activePath, ".unreadable-");
        bool quarantined = false;
        try
        {
            File.Move(activePath, quarantinePath);
            quarantined = true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DeleteTemporaryFile(temporary);
            throw new SupprocomSecretsException(
                "RecoveryQuarantineFailed",
                $"Unable to quarantine unreadable active file '{activePath}'.",
                exception);
        }

        try
        {
            File.Move(temporary!, activePath);
            temporary = null;
            ParsedSecretDocument restored = SecretDocumentParser.Parse(template.RawText, activePath);
            _ = originalBytes;
            return new PhysicalFile(restored, activePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (quarantined && !File.Exists(activePath) && File.Exists(quarantinePath))
            {
                try
                {
                    File.Move(quarantinePath, activePath);
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException or ArgumentException or
                    NotSupportedException)
                {
                    throw new SupprocomSecretsException(
                        "RecoveryRollbackFailed",
                        $"Unable to restore unreadable active file '{activePath}' after recovery failed.",
                        rollbackException);
                }
            }

            throw new SupprocomSecretsException(
                "RecoveryReplacementFailed",
                $"Unable to install the validated recovery file '{activePath}'.",
                exception);
        }
        finally
        {
            DeleteTemporaryFile(temporary);
        }
    }

    private async Task<string> ReadPlaintextAsync(
        byte[] bytes,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SecretFileProtectionCodec.IsEnvelope(bytes))
        {
            if (_fileOptions.Protection != SecretFileProtection.InstallationBoundAesGcm)
            {
                throw new SupprocomSecretsException(
                    "ProtectedFileRequiresProtection",
                    $"Active file '{path}' is protected and requires installation-bound AES-GCM configuration.");
            }

            byte[] key = await GetInstallationKeyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return SecretFileProtectionCodec.Decrypt(bytes, key, path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SupprocomSecretsException(
                "InvalidDocumentEncoding",
                $"Active file '{path}' is not valid UTF-8.",
                exception);
        }
    }

    private async Task EnsureProtectedAsync(
        string path,
        byte[] originalBytes,
        string plaintext,
        CancellationToken cancellationToken)
    {
        if (_fileOptions.Protection != SecretFileProtection.InstallationBoundAesGcm ||
            SecretFileProtectionCodec.IsEnvelope(originalBytes))
        {
            return;
        }

        await WriteAtomicAsync(path, plaintext, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAtomicAsync(
        string path,
        string plaintext,
        byte[]? originalPlaintextBytes,
        CancellationToken cancellationToken)
    {
        FilePermissionSnapshot? permissions = FilePermissionSnapshot.Capture(path);
        string temporary = await PrepareAtomicFileAsync(
                path,
                plaintext,
                originalPlaintextBytes,
                cancellationToken,
                permissions)
            .ConfigureAwait(false);
        try
        {
            File.Move(temporary, path, overwrite: true);
            temporary = string.Empty;
        }
        finally
        {
            DeleteTemporaryFile(temporary);
        }
    }

    private async Task<string> PrepareAtomicFileAsync(
        string path,
        string plaintext,
        byte[]? originalPlaintextBytes,
        CancellationToken cancellationToken,
        FilePermissionSnapshot? permissions)
    {
        byte[] payload;
        if (_fileOptions.Protection == SecretFileProtection.InstallationBoundAesGcm)
        {
            byte[] key = await GetInstallationKeyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                payload = SecretFileProtectionCodec.Encrypt(plaintext, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        else
        {
            payload = originalPlaintextBytes?.ToArray() ?? Encoding.UTF8.GetBytes(plaintext);
        }

        try
        {
            return await PreparePayloadFileAsync(path, payload, permissions, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (_fileOptions.Protection == SecretFileProtection.InstallationBoundAesGcm)
                CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task<string> PreparePayloadFileAsync(
        string path,
        byte[] payload,
        FilePermissionSnapshot? permissions,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Missing file directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".supprocom-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                permissions?.Apply(temporary);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return temporary;
        }
        catch
        {
            DeleteTemporaryFile(temporary);
            throw;
        }
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

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

    private async Task PreserveOriginalAsync(
        string activePath,
        byte[] originalBytes,
        CancellationToken cancellationToken)
    {
        string sibling = CreateUniqueSibling(activePath, ".pre-supprocom-import-");
        string temporary = CreateUniqueSibling(activePath, ".pre-supprocom-import-tmp-");
        FilePermissionSnapshot? permissions = FilePermissionSnapshot.Capture(activePath);
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                permissions?.Apply(temporary);
                await stream.WriteAsync(originalBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length != originalBytes.Length)
                {
                    throw new SupprocomSecretsException(
                        "ImportPreservationFailed",
                        "The preserved import source did not contain the complete original document.");
                }
            }

            if (ImportPreservationBeforeInstallHook is { } hook)
                await hook(cancellationToken).ConfigureAwait(false);

            File.Move(temporary, sibling);
            temporary = string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SupprocomSecretsException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SupprocomSecretsException(
                "ImportPreservationFailed",
                "Unable to preserve the exact original import document.",
                exception);
        }
        finally
        {
            DeleteTemporaryFile(temporary);
        }
    }

    private ParsedSecretDocument ValidateTemplate(byte[] bytes, string templatePath)
    {
        if (bytes.Length == 0)
            throw new SupprocomSecretsException("InvalidTemplate", $"Template '{templatePath}' is empty.");
        if (SecretFileProtectionCodec.IsEnvelope(bytes))
        {
            throw new SupprocomSecretsException(
                "EncryptedTemplate",
                $"Template '{templatePath}' must be plaintext canonical dotenv.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SupprocomSecretsException(
                "InvalidTemplate",
                $"Template '{templatePath}' is not valid UTF-8.",
                exception);
        }

        ParsedSecretDocument document = SecretDocumentParser.Parse(text, templatePath);
        if (document.RawText.Length == 0)
            throw new SupprocomSecretsException("InvalidTemplate", $"Template '{templatePath}' is empty.");
        ValidateDocumentSemantics(document);
        return document;
    }

    private DevelopmentSelection? SelectDevelopment()
    {
        if (!string.Equals(GetEnvironmentName(), "Development", StringComparison.OrdinalIgnoreCase))
            return null;

        string overlayTemplate = Path.Combine(DirectoryPath, _fileOptions.DevelopmentTemplateName);
        string replacementTemplate = Path.Combine(DirectoryPath, _fileOptions.DevelopmentReplacementTemplateName);
        string overlayPath = Path.Combine(DirectoryPath, ".dev.env");
        string replacementPath = Path.Combine(DirectoryPath, ".env.development");

        bool hasOverlay = File.Exists(overlayTemplate) || File.Exists(overlayPath);
        bool hasReplacement = File.Exists(replacementTemplate) || File.Exists(replacementPath);
        if (hasOverlay && hasReplacement)
        {
            throw new SupprocomSecretsException(
                "AmbiguousDevelopmentTemplates",
                $"Development conventions '{overlayTemplate}' and '{replacementTemplate}' are both present.");
        }

        if (_fileOptions.DevelopmentName is not null)
        {
            string name = _fileOptions.DevelopmentName;
            string template = name.Equals(".env.development", StringComparison.OrdinalIgnoreCase)
                ? _fileOptions.DevelopmentReplacementTemplateName
                : _fileOptions.DevelopmentTemplateName;
            return new DevelopmentSelection(name, template, _fileOptions.DevelopmentComposition);
        }

        if (hasReplacement)
            return new DevelopmentSelection(
                ".env.development",
                _fileOptions.DevelopmentReplacementTemplateName,
                SecretFileComposition.Replace);
        if (hasOverlay)
            return new DevelopmentSelection(
                ".dev.env",
                _fileOptions.DevelopmentTemplateName,
                SecretFileComposition.Overlay);

        return null;
    }

    private string? GetEnvironmentName() =>
        _options.EnvironmentName
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

    private void ValidateFileNames()
    {
        ValidateFileName(_fileOptions.ActiveName, nameof(_fileOptions.ActiveName));
        ValidateFileName(_fileOptions.TemplateName, nameof(_fileOptions.TemplateName));
        ValidateFileName(_fileOptions.DevelopmentTemplateName, nameof(_fileOptions.DevelopmentTemplateName));
        ValidateFileName(
            _fileOptions.DevelopmentReplacementTemplateName,
            nameof(_fileOptions.DevelopmentReplacementTemplateName));
        if (_fileOptions.DevelopmentName is not null)
            ValidateFileName(_fileOptions.DevelopmentName, nameof(_fileOptions.DevelopmentName));
    }

    private static void ValidateFileName(string name, string option)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            Path.IsPathRooted(name) ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new SupprocomSecretsException(
                "InvalidFileName",
                $"File option '{option}' must be a single file name.");
        }
    }

    private static void ValidateDocumentSemantics(ParsedSecretDocument document)
    {
        if (document.SourceDirective is not null && document.Values.Count != 0)
        {
            throw new SupprocomSecretsException(
                "PointerFileContainsConfiguration",
                "When SUPPROCOM_SECRET_SOURCE is selected, the dotenv document may contain only that directive, SUPPROCOM_LOCAL_OPTIONS, comments, and blank lines.");
        }

        if (document.SourceDirective is not null)
            ValidateSourceDirective(document.SourceDirective);
    }

    internal static void ValidateSourceDirective(string source)
    {
        if (source.StartsWith('#') ||
            !Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) ||
            string.IsNullOrWhiteSpace(uri.Scheme) ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            throw new SupprocomSecretsException(
                "InvalidSecretSource",
                "SUPPROCOM_SECRET_SOURCE must be a URI-shaped provider pointer without embedded credentials.");
        }

        if (uri.Scheme.Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            throw new SupprocomSecretsException(
                "UnnecessaryEnvSource",
                "The built-in dotenv store is selected by omitting SUPPROCOM_SECRET_SOURCE; env:// is not a provider pointer.");
        }
    }

    private static bool MutateLocalNode(
        JsonNode root,
        string normalizedKey,
        string? value,
        bool delete)
    {
        string[] segments = normalizedKey.Split(':', StringSplitOptions.None);
        JsonNode? current = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (current is JsonObject objectNode)
            {
                if (!TryGetJsonProperty(objectNode, segments[index], out _, out current))
                    return false;
            }
            else if (current is JsonArray arrayNode &&
                     int.TryParse(segments[index], out int arrayIndex) &&
                     arrayIndex >= 0 && arrayIndex < arrayNode.Count)
            {
                current = arrayNode[arrayIndex];
            }
            else
            {
                return false;
            }

            if (current is null)
                return false;
        }

        string leaf = segments[^1];
        switch (current)
        {
            case JsonObject objectNode when TryGetJsonProperty(objectNode, leaf, out string propertyName, out _):
                if (delete)
                    objectNode.Remove(propertyName);
                else
                    objectNode[propertyName] = JsonValue.Create(value);
                return true;
            case JsonArray arrayNode when int.TryParse(leaf, out int arrayIndex) &&
                arrayIndex >= 0 && arrayIndex < arrayNode.Count:
                if (delete)
                    arrayNode.RemoveAt(arrayIndex);
                else
                    arrayNode[arrayIndex] = JsonValue.Create(value);
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetJsonProperty(
        JsonObject objectNode,
        string name,
        out string propertyName,
        out JsonNode? value)
    {
        foreach (KeyValuePair<string, JsonNode?> property in objectNode)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                propertyName = property.Key;
                value = property.Value;
                return true;
            }
        }

        propertyName = string.Empty;
        value = null;
        return false;
    }

    private static string NormalizeMutationKey(string key)
    {
        string normalized = SecretDocumentParser.NormalizeConfigurationKey(key.Trim());
        if (normalized.Length == 0)
            throw new SupprocomSecretsException("InvalidMutationKey", "Secret mutation key cannot be empty.");
        return normalized;
    }

    private static SemaphoreSlim GetWriterLock(string activePath) =>
        WriterLocks.GetOrAdd(Path.GetFullPath(activePath), _ => new SemaphoreSlim(1, 1));

    private async Task<byte[]> GetInstallationKeyAsync(CancellationToken cancellationToken)
    {
        if (_fileOptions.InstallationKeyStore is not null)
            return await _fileOptions.InstallationKeyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);

        string path = _fileOptions.InstallationKeyPath ?? Path.Combine(DirectoryPath, ".installation.key");
        if (!string.Equals(_cachedKeyPath, path, StringComparison.Ordinal))
            _cachedKeyPath = path;
        return await new FileInstallationKeyStore(path).GetOrCreateKeyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CreateUniqueSibling(string activePath, string prefix)
    {
        string directory = Path.GetDirectoryName(activePath) ?? throw new InvalidOperationException("Missing file directory.");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string candidate = Path.Combine(
                directory,
                $"{prefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new SupprocomSecretsException(
            "UniqueSiblingFailed",
            $"Unable to allocate a unique recovery sibling for '{activePath}'.");
    }

    private readonly record struct DevelopmentSelection(
        string Name,
        string TemplateName,
        SecretFileComposition Composition);

    private sealed record PhysicalFile(ParsedSecretDocument Document, string Path);
}

internal sealed record FileLoadResult(
    Dictionary<string, string> Values,
    string? SourceDirective,
    Dictionary<string, string> LocalOptions,
    ParsedSecretDocument? LastDocument);
