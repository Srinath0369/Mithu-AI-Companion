using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Ai;

/// <summary>
/// API keys encrypted with Windows DPAPI for the current user (<c>secrets.dat</c> in the data folder): another
/// Windows account, or a copy of the file on another PC, can't read them. Each value is encrypted separately with
/// Mithu-specific entropy. A damaged file is set aside (keys must be entered again). Never logs a value.
/// </summary>
public sealed class DpapiSecretStore(string path, ILogger<DpapiSecretStore> logger) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mithu.Secrets.v1");
    private readonly Lock _gate = new();

    public string? Get(string name)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(name, out var protectedValue))
            {
                return null;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                logger.LogWarning("Stored secret {Name} could not be decrypted (another user or PC?)", name);
                return null;
            }
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            var all = Load();
            if (string.IsNullOrEmpty(value))
            {
                all.Remove(name);
            }
            else
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                all[name] = Convert.ToBase64String(bytes);
            }

            JsonFile.WriteAtomic(path, JsonSerializer.Serialize(all));
            logger.LogInformation("Secret {Name} {Action}", name, string.IsNullOrEmpty(value) ? "removed" : "saved");
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            JsonFile.MoveAside(path);
            logger.LogWarning("Secrets file was damaged and has been set aside");
            return [];
        }
    }
}
