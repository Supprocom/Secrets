using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using NUnit.Framework;

namespace Supprocom.Secrets.Tests;

[TestFixture]
public sealed class PackageAcceptanceTests
{
    private const string PackageVersion = "0.1.10";

    [Test]
    [Explicit("Requires a freshly packed source-mapped Supprocom.Secrets package.")]
    public void PackedArtifactIsSourceMappedAndContainsOnlyPortableBuildAssets()
    {
        string repository = FindRepositoryRoot();
        string packagePath = GetPackagePath(repository);
        Assert.That(File.Exists(packagePath), Is.True, "Pack the current commit before running package acceptance.");

        using FileStream stream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        string[] entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.That(entryNames, Does.Contain("lib/net10.0/Supprocom.Secrets.dll"));
        Assert.That(entryNames, Does.Contain("buildTransitive/Supprocom.Secrets.props"));
        Assert.That(entryNames, Does.Contain("buildTransitive/Supprocom.Secrets.targets"));
        Assert.That(
            entryNames.Any(name => Path.GetFileName(name).Equals(".env", StringComparison.OrdinalIgnoreCase)),
            Is.False);
        Assert.That(
            entryNames.Any(name => Path.GetFileName(name).Equals(".dev.env", StringComparison.OrdinalIgnoreCase)),
            Is.False);
        Assert.That(
            entryNames.Any(name => Path.GetFileName(name).Equals(".env.development", StringComparison.OrdinalIgnoreCase)),
            Is.False);

        ZipArchiveEntry nuspec = archive.Entries.Single(
            entry => entry.FullName.Equals("Supprocom.Secrets.nuspec", StringComparison.OrdinalIgnoreCase));
        XDocument metadata;
        using (Stream nuspecStream = nuspec.Open())
            metadata = XDocument.Load(nuspecStream);

        string expectedCommit = RunProcess("git", "rev-parse HEAD", repository).StandardOutput.Trim();
        string? packageCommit = metadata
            .Descendants()
            .Single(element => element.Name.LocalName.Equals("repository", StringComparison.OrdinalIgnoreCase))
            .Attribute("commit")?
            .Value;
        Assert.That(packageCommit, Is.EqualTo(expectedCommit));
    }

    [Test]
    [Explicit("Requires a freshly packed source-mapped Supprocom.Secrets package.")]
    public void FreshPackageReferenceConsumerBuildsPublishesAndCreatesTemplates()
    {
        string repository = FindRepositoryRoot();
        string packagePath = GetPackagePath(repository);
        Assert.That(File.Exists(packagePath), Is.True, "Pack the current commit before running package acceptance.");

        using var root = new TemporaryDirectory();
        string consumer = Path.Combine(root.Path, "overlay-consumer");
        CreateConsumer(consumer, packagePath);
        Write(Path.Combine(consumer, "Environment", ".env.template"), "Smoke__Base=base\n");
        Write(Path.Combine(consumer, "Environment", ".dev.env.template"), "Smoke__Overlay=overlay\n");

        RestoreAndBuild(consumer);
        string debugEnvironment = Path.Combine(consumer, "bin", "Debug", "net10.0", "Environment");
        Assert.That(File.ReadAllText(Path.Combine(debugEnvironment, ".env.template")), Is.EqualTo("Smoke__Base=base\n"));
        Assert.That(File.ReadAllText(Path.Combine(debugEnvironment, ".dev.env.template")), Is.EqualTo("Smoke__Overlay=overlay\n"));
        Assert.That(File.Exists(Path.Combine(debugEnvironment, ".env")), Is.False);

        RunProcess(
            "dotnet",
            "publish Consumer.csproj -c Release --no-restore --nologo",
            consumer,
            expectedExitCode: 0,
            environment: NuGetEnvironment(consumer));
        string publishEnvironment = Path.Combine(consumer, "bin", "Release", "net10.0", "publish", "Environment");
        Assert.That(File.Exists(Path.Combine(publishEnvironment, ".env.template")), Is.True);
        Assert.That(File.Exists(Path.Combine(publishEnvironment, ".dev.env.template")), Is.True);
        Assert.That(File.Exists(Path.Combine(publishEnvironment, ".env")), Is.False);

        ProcessResult run = RunProcess(
            "dotnet",
            "Consumer.dll",
            Path.Combine(consumer, "bin", "Release", "net10.0", "publish"),
            environment: new Dictionary<string, string> { ["DOTNET_ENVIRONMENT"] = "Development" });
        Assert.That(run.StandardOutput, Does.Contain("base|overlay"));
        Assert.That(File.Exists(Path.Combine(publishEnvironment, ".env")), Is.True);
        Assert.That(File.Exists(Path.Combine(publishEnvironment, ".dev.env")), Is.True);

        string activeBeforeTemplateUpdate = File.ReadAllText(Path.Combine(publishEnvironment, ".env"));
        string baseTemplate = Path.Combine(consumer, "Environment", ".env.template");
        Write(baseTemplate, "Smoke__Base=changed\n");
        File.SetLastWriteTimeUtc(baseTemplate, DateTime.UtcNow.AddSeconds(2));
        RunProcess(
            "dotnet",
            "publish Consumer.csproj -c Release --no-restore --nologo",
            consumer,
            expectedExitCode: 0,
            environment: NuGetEnvironment(consumer));
        Assert.That(File.ReadAllText(Path.Combine(publishEnvironment, ".env.template")), Is.EqualTo("Smoke__Base=changed\n"));
        Assert.That(File.ReadAllText(Path.Combine(publishEnvironment, ".env")), Is.EqualTo(activeBeforeTemplateUpdate));

        string replacement = Path.Combine(root.Path, "replacement-consumer");
        CreateConsumer(replacement, packagePath);
        Write(Path.Combine(replacement, "Environment", ".env.development.template"), "Smoke__Mode=replacement\n");
        RestoreAndBuild(replacement);
        RunProcess(
            "dotnet",
            "publish Consumer.csproj -c Release --no-restore --nologo",
            replacement,
            expectedExitCode: 0,
            environment: NuGetEnvironment(replacement));
        string replacementPublish = Path.Combine(replacement, "bin", "Release", "net10.0", "publish");
        ProcessResult replacementRun = RunProcess(
            "dotnet",
            "Consumer.dll",
            replacementPublish,
            environment: new Dictionary<string, string> { ["DOTNET_ENVIRONMENT"] = "Development" });
        Assert.That(replacementRun.StandardOutput, Does.Contain("replacement"));
        Assert.That(File.Exists(Path.Combine(replacementPublish, "Environment", ".env.development")), Is.True);
        Assert.That(File.Exists(Path.Combine(replacementPublish, "Environment", ".dev.env.template")), Is.False);

        string ambiguous = Path.Combine(root.Path, "ambiguous-consumer");
        CreateConsumer(ambiguous, packagePath);
        Write(Path.Combine(ambiguous, "Environment", ".dev.env.template"), "Smoke__Overlay=overlay\n");
        Write(Path.Combine(ambiguous, "Environment", ".env.development.template"), "Smoke__Mode=replacement\n");
        RunProcess(
            "dotnet",
            "restore Consumer.csproj --configfile NuGet.config --nologo",
            ambiguous,
            expectedExitCode: 0,
            environment: NuGetEnvironment(ambiguous));
        ProcessResult failedBuild = RunProcess(
            "dotnet",
            "build Consumer.csproj --no-restore --nologo",
            ambiguous,
            environment: NuGetEnvironment(ambiguous));
        Assert.That(failedBuild.ExitCode, Is.Not.EqualTo(0));
        Assert.That(failedBuild.CombinedOutput, Does.Contain(".dev.env.template"));
        Assert.That(failedBuild.CombinedOutput, Does.Contain(".env.development.template"));
    }

    private static void CreateConsumer(string directory, string packagePath)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "Environment"));
        Write(
            Path.Combine(directory, "Consumer.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Supprocom.Secrets" Version="{{PackageVersion}}" />
              </ItemGroup>
            </Project>
            """,
            encoding: new UTF8Encoding(false));
        Write(
            Path.Combine(directory, "NuGet.config"),
            $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="verification" value="{{Path.GetDirectoryName(packagePath)!}}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """,
            encoding: new UTF8Encoding(false));
        Write(
            Path.Combine(directory, "Program.cs"),
            """
            using Microsoft.Extensions.Configuration;

            IConfiguration configuration = new ConfigurationBuilder()
                .AddSupprocomSecrets()
                .Build();
            Console.WriteLine($"{configuration["Smoke:Base"]}|{configuration["Smoke:Overlay"]}|{configuration["Smoke:Mode"]}");
            """,
            encoding: new UTF8Encoding(false));
    }

    private static void RestoreAndBuild(string directory)
    {
        IReadOnlyDictionary<string, string> environment = NuGetEnvironment(directory);
        RunProcess(
            "dotnet",
            "restore Consumer.csproj --configfile NuGet.config --nologo",
            directory,
            expectedExitCode: 0,
            environment: environment);
        RunProcess(
            "dotnet",
            "build Consumer.csproj --no-restore --nologo",
            directory,
            expectedExitCode: 0,
            environment: environment);
    }

    private static IReadOnlyDictionary<string, string> NuGetEnvironment(string directory) =>
        new Dictionary<string, string>
        {
            ["NUGET_PACKAGES"] = Path.Combine(directory, ".packages")
        };

    private static string GetPackagePath(string repository) =>
        Path.Combine(repository, "artifacts", "verification", $"Supprocom.Secrets.{PackageVersion}.nupkg");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Supprocom.Secrets.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        Assert.Fail("Unable to locate the Supprocom.Secrets repository root.");
        return string.Empty;
    }

    private static ProcessResult RunProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        int? expectedExitCode = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> item in environment)
                startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.That(process.Start(), Is.True, $"Unable to start {fileName}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 55000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Fail($"Process '{fileName} {arguments}' exceeded the bounded 55-second timeout.");
        }

        Task.WaitAll(output, error);
        var result = new ProcessResult(process.ExitCode, output.Result, error.Result);
        if (expectedExitCode.HasValue)
            Assert.That(result.ExitCode, Is.EqualTo(expectedExitCode.Value), result.CombinedOutput);
        return result;
    }

    private static void Write(string path, string contents, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, encoding ?? new UTF8Encoding(false));
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Supprocom.Secrets.PackageTests",
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
