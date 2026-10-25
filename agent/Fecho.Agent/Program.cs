namespace Fecho.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Modos elevados (instalar/remover o componente de desbloqueio).
        // O ícone da bandeja dispara estes com "Executar como administrador".
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--install-credprovider": return CredProviderInstaller.InstallElevated();
                case "--uninstall-credprovider": return CredProviderInstaller.UninstallElevated();
            }
        }

        // Uma única cópia do agente por usuário.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Fecho.Agent", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "O Fecho já está rodando (ícone de escudo na bandeja, perto do relógio).\n\n" +
                "Para trocar de versão, clique com o botão direito no escudo, escolha \"Sair do Fecho\" " +
                "e abra o arquivo novo.",
                "Fecho", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new TrayContext());
        return 0;
    }
}
