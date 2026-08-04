namespace AgenteBiometricoPresencial.Configuration;

public sealed record AgentOptions(
    int Port,
    string? CertificatePath,
    string? CertificatePassword)
{
    public bool UseTls => !string.IsNullOrWhiteSpace(CertificatePath);

    public static AgentOptions FromEnvironment()
    {
        var portText = Environment.GetEnvironmentVariable("BIOMETRIC_AGENT_PORT");
        var port = int.TryParse(portText, out var configuredPort) ? configuredPort : 8443;

        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("BIOMETRIC_AGENT_PORT debe estar entre 1 y 65535.");
        }

        return new AgentOptions(
            port,
            Environment.GetEnvironmentVariable("BIOMETRIC_AGENT_CERT_PATH"),
            Environment.GetEnvironmentVariable("BIOMETRIC_AGENT_CERT_PASSWORD"));
    }
}
