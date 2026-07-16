using Microsoft.Extensions.DependencyInjection;
using Supprocom.Secrets;

namespace Microsoft.Extensions.DependencyInjection;

public static class SupprocomSecretsFileProtectionServiceCollectionExtensions
{
    public static IServiceCollection AddSupprocomSecretsFileProtectionManagement(
        this IServiceCollection services,
        SupprocomSecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<SupprocomSecretFileStore>(_ => new SupprocomSecretFileStore(options));
        services.AddSingleton<ISecretDocumentStore>(provider =>
            provider.GetRequiredService<SupprocomSecretFileStore>());
        services.AddSingleton<ISecretDocumentUpdater>(provider =>
            provider.GetRequiredService<SupprocomSecretFileStore>());
        services.AddSingleton<ISecretFileProtectionManager>(provider =>
            provider.GetRequiredService<SupprocomSecretFileStore>());
        return services;
    }
}
