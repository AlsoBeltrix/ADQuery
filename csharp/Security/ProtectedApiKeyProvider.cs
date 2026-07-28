using System.Security.Cryptography;

namespace AdQuery.Orchestrator.Security;

/// <summary>
/// F03 Slice 1: reads the Claude API key from a DPAPI-encrypted file that lives
/// <b>outside</b> the web root (default <c>C:\ProgramData\ADQuery\claude-apikey.dat</c>),
/// so a deploy that wipes the application directory can never erase the secret.
///
/// The blob is protected with <see cref="DataProtectionScope.LocalMachine"/> so any
/// process on the server — including the IIS app-pool identity — can decrypt it,
/// regardless of which account wrote it. A missing file or any decrypt failure
/// yields <c>null</c> rather than throwing: the existing missing-key UX
/// (ClaudeService) already handles a blank key gracefully, and startup must not
/// crash just because the store has not been provisioned yet.
///
/// The plaintext key is never logged.
/// </summary>
public static class ProtectedApiKeyProvider
{
    /// <summary>
    /// Default store path, outside <c>D:\inetpub\adquery</c> so no deploy touches it.
    /// Overridable via the <c>Claude:ApiKeyFile</c> configuration knob.
    /// </summary>
    public const string DefaultApiKeyFilePath = @"C:\ProgramData\ADQuery\claude-apikey.dat";

    /// <summary>
    /// Attempts to read and DPAPI-decrypt the API key from <paramref name="filePath"/>.
    /// Returns the plaintext key, or <c>null</c> if the file is absent, empty, or
    /// cannot be decrypted on this machine.
    /// </summary>
    public static string? TryReadApiKey(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(filePath);
            if (protectedBytes.Length == 0)
            {
                return null;
            }

            var plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                scope: DataProtectionScope.LocalMachine);

            var key = System.Text.Encoding.UTF8.GetString(plaintextBytes).Trim();
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Corrupt blob, wrong machine, or an unreadable file: treat as "no key
            // configured" so startup degrades to the existing missing-key UX.
            return null;
        }
    }

    /// <summary>
    /// DPAPI machine-scope encrypts <paramref name="apiKey"/> and writes it to
    /// <paramref name="filePath"/>, creating the containing directory if needed.
    /// Used by the operator provisioning script and by tests.
    /// </summary>
    public static void WriteApiKey(string filePath, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var protectedBytes = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(apiKey),
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        File.WriteAllBytes(filePath, protectedBytes);
    }
}
