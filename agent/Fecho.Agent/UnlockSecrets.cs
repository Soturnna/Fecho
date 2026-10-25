using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fecho.Agent;

/// <summary>
/// Guarda a senha do Windows cifrada com DPAPI (só esta conta, neste PC).
/// O iPhone nunca recebe nem envia a senha; o agente só a usa, protegida,
/// na hora de repassá-la ao componente de desbloqueio.
/// </summary>
public static class UnlockSecrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fecho.unlock.v1");
    private static string Path => System.IO.Path.Combine(AgentPaths.Root, "unlock.dat");

    private sealed class Stored
    {
        public string Domain { get; set; } = "";
        public string User { get; set; } = "";
        public string PasswordProtected { get; set; } = "";
    }

    public static bool HasPassword() => File.Exists(Path);

    public static (string Domain, string User)? CurrentAccount()
    {
        var s = Read();
        return s is null ? null : (s.Domain, s.User);
    }

    /// <summary>Confere a senha com o Windows e, se bater, guarda cifrada.</summary>
    public static bool SetPassword(string domain, string user, string password, bool verify = true)
    {
        if (verify && !VerifyWindowsPassword(domain, user, password)) return false;

        AgentPaths.Ensure();
        var protectedPwd = ProtectedData.Protect(Encoding.Unicode.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        var stored = new Stored
        {
            Domain = domain,
            User = user,
            PasswordProtected = Convert.ToBase64String(protectedPwd),
        };
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stored));
        File.Move(tmp, Path, overwrite: true);
        return true;
    }

    public static void Clear()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }

    /// <summary>Decifra a senha para uso imediato. Lembre de limpar depois.</summary>
    public static (string Domain, string User, string Password)? Load()
    {
        var s = Read();
        if (s is null) return null;
        try
        {
            var pwd = ProtectedData.Unprotect(Convert.FromBase64String(s.PasswordProtected), Entropy, DataProtectionScope.CurrentUser);
            return (s.Domain, s.User, Encoding.Unicode.GetString(pwd));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static Stored? Read()
    {
        if (!File.Exists(Path)) return null;
        try { return JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path)); }
        catch (Exception) { return null; }
    }

    public static bool VerifyWindowsPassword(string domain, string user, string password)
    {
        const int LOGON32_LOGON_INTERACTIVE = 2;
        const int LOGON32_PROVIDER_DEFAULT = 0;
        if (LogonUser(user, string.IsNullOrEmpty(domain) ? "." : domain, password,
                LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out var token))
        {
            CloseHandle(token);
            return true;
        }
        return false;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password,
        int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
