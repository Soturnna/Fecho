using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fecho.Core;
using Fecho.Server;

namespace Fecho.Agent;

public static class AgentPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fecho");

    public static string Config => Path.Combine(Root, "config.json");
    public static string Devices => Path.Combine(Root, "devices.dat");
    public static string Audit => Path.Combine(Root, "audit.log");

    public static void Ensure() => Directory.CreateDirectory(Root);
}

/// <summary>Configuração não secreta do agente.</summary>
public sealed class AgentConfig
{
    public string AgentId { get; set; } = Guid.NewGuid().ToString("N");
    public int Port { get; set; } = 47321;
    public string? CertThumbprint { get; set; }

    public static AgentConfig LoadOrCreate()
    {
        if (File.Exists(AgentPaths.Config))
        {
            var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(AgentPaths.Config));
            if (cfg is not null) return cfg;
        }
        var fresh = new AgentConfig();
        fresh.Save();
        return fresh;
    }

    public void Save() =>
        File.WriteAllText(AgentPaths.Config, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// Grava a lista de aparelhos cifrada com DPAPI: só a mesma conta do Windows,
/// neste mesmo PC, consegue ler o arquivo.
/// </summary>
public sealed class DpapiDeviceStorage : IDeviceStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fecho.devices.v1");

    public string? Read()
    {
        if (!File.Exists(AgentPaths.Devices)) return null;
        var cipher = File.ReadAllBytes(AgentPaths.Devices);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public void Write(string content)
    {
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(content), Entropy, DataProtectionScope.CurrentUser);
        var tmp = AgentPaths.Devices + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, AgentPaths.Devices, overwrite: true);
    }
}

/// <summary>Grava a auditoria em JSON por linha, com rotação simples em 1 MB.</summary>
public sealed class FileAuditSink : IAuditSink
{
    private readonly object _gate = new();

    public void Append(AuditEntry entry)
    {
        lock (_gate)
        {
            var info = new FileInfo(AgentPaths.Audit);
            if (info.Exists && info.Length > 1_000_000)
                File.Move(AgentPaths.Audit, AgentPaths.Audit + ".1", overwrite: true);
            File.AppendAllText(AgentPaths.Audit, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }
}

public static class CertificateStore
{
    /// <summary>
    /// Carrega o certificado do repositório pessoal do Windows (CurrentUser\My).
    /// A chave privada fica protegida pelo próprio Windows. Na primeira
    /// execução, cria o certificado e guarda a impressão digital na config.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(AgentConfig cfg)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        if (!string.IsNullOrEmpty(cfg.CertThumbprint))
        {
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, cfg.CertThumbprint, validOnly: false);
            if (found.Count > 0 && found[0].HasPrivateKey) return found[0];
        }

        using var fresh = AgentCertificate.Create();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = fresh.Export(X509ContentType.Pfx, password);
        var persisted = new X509Certificate2(pfx, password,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        persisted.FriendlyName = "Fecho Agent";
        store.Add(persisted);
        cfg.CertThumbprint = persisted.Thumbprint;
        cfg.Save();
        return persisted;
    }
}
