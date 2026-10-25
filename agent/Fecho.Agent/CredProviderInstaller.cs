using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace Fecho.Agent;

/// <summary>
/// Instala e remove o componente de desbloqueio (a DLL de Credential Provider).
/// Precisa de administrador: cria um ponto de restauração, copia a DLL para
/// %ProgramData%\Fecho e grava as chaves no Registro. Nunca substitui o login
/// por senha ou PIN — só acrescenta uma opção a mais.
/// </summary>
public static class CredProviderInstaller
{
    // Precisa bater com Guid.h / FECHO_CLSID_STRING.
    private const string Clsid = "{C3D1E2F0-5A6B-4C8D-9E0F-1A2B3C4D5E6F}";
    private const string ProvidersKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";
    private const string DllResource = "FechoCredentialProvider.dll";

    private static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fecho");
    private static string DllPath => Path.Combine(InstallDir, DllResource);

    /// <summary>A DLL está embutida neste .exe? (Fora do CI pode não estar.)</summary>
    public static bool HasEmbeddedDll() =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Any(n => n.EndsWith(DllResource, StringComparison.OrdinalIgnoreCase));

    public static bool IsRegistered()
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"{ProvidersKey}\{Clsid}");
        return k is not null && File.Exists(DllPath);
    }

    /// <summary>Pede ao Windows para rodar este .exe como administrador no modo instalar/remover.</summary>
    public static bool RunElevated(bool install)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var psi = new ProcessStartInfo(exe, install ? "--install-credprovider" : "--uninstall-credprovider")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch (Exception)
        {
            return false; // usuário recusou o UAC, por exemplo
        }
    }

    /// <summary>Executado já com administrador (processo separado, elevado).</summary>
    public static int InstallElevated()
    {
        try
        {
            if (!HasEmbeddedDll())
            {
                Report("Esta versão do Fecho não traz o componente de desbloqueio embutido.", error: true);
                return 2;
            }

            TryCreateRestorePoint();

            Directory.CreateDirectory(InstallDir);
            ExtractDll(DllPath);

            using (var providers = Registry.LocalMachine.CreateSubKey($@"{ProvidersKey}\{Clsid}"))
            {
                providers!.SetValue(null, "Fecho");
            }
            using (var clsid = Registry.ClassesRoot.CreateSubKey($@"CLSID\{Clsid}"))
            {
                clsid!.SetValue(null, "Fecho Credential Provider");
                using var inproc = clsid.CreateSubKey("InprocServer32");
                inproc!.SetValue(null, DllPath);
                inproc.SetValue("ThreadingModel", "Apartment");
            }

            Report("Componente de desbloqueio instalado.\n\nBloqueie o PC (Win+L) e teste o desbloqueio pelo iPhone. " +
                   "A senha e o PIN continuam funcionando normalmente.");
            return 0;
        }
        catch (Exception ex)
        {
            Report("Não foi possível instalar o componente:\n\n" + ex.Message, error: true);
            return 1;
        }
    }

    public static int UninstallElevated()
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree($@"{ProvidersKey}\{Clsid}", throwOnMissingSubKey: false);
            Registry.ClassesRoot.DeleteSubKeyTree($@"CLSID\{Clsid}", throwOnMissingSubKey: false);
            try { if (File.Exists(DllPath)) File.Delete(DllPath); } catch (IOException) { }
            Report("Componente de desbloqueio removido.");
            return 0;
        }
        catch (Exception ex)
        {
            Report("Não foi possível remover o componente:\n\n" + ex.Message, error: true);
            return 1;
        }
    }

    private static void ExtractDll(string target)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(DllResource, StringComparison.OrdinalIgnoreCase));
        using var src = asm.GetManifestResourceStream(name)!;
        var tmp = target + ".new";
        using (var dst = File.Create(tmp)) src.CopyTo(dst);
        File.Move(tmp, target, overwrite: true);
    }

    private static void TryCreateRestorePoint()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"Checkpoint-Computer -Description 'Fecho - desbloqueio' -RestorePointType MODIFY_SETTINGS\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
        }
        catch (Exception)
        {
            // Ponto de restauração é melhor esforço; a Proteção do Sistema pode estar desligada.
        }
    }

    private static void Report(string message, bool error = false)
    {
        MessageBox.Show(message, "Fecho — desbloqueio",
            MessageBoxButtons.OK, error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
    }
}
