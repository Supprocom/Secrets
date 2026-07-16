namespace Supprocom.Secrets;

public sealed class SupprocomSecretsException : InvalidOperationException
{
    public SupprocomSecretsException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public SupprocomSecretsException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
