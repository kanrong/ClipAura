using System.Security.Cryptography;
using System.Text;
using PopClip.Core.Logging;

namespace PopClip.App.Config;

public sealed class ProtectedSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClipAura.AI.ApiKey.v1");
    private readonly ILog? _log;

    public ProtectedSecretStore(ILog? log = null) => _log = log;

    public string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        try
        {
            var plain = Encoding.UTF8.GetBytes(secret);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch (Exception ex)
        {
            _log?.Warn("api key protect failed", ("err", ex.Message));
            return "";
        }
    }

    public string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret)) return "";
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedSecret);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            _log?.Warn("api key unprotect failed", ("err", ex.Message));
            return "";
        }
    }
}
