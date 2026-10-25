namespace Fecho.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Uma única cópia do agente por usuário.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Fecho.Agent", out var isFirst);
        ApplicationConfiguration.Initialize();
        if (!isFirst)
        {
            MessageBox.Show(
                "O Fecho já está rodando (ícone de escudo na bandeja, perto do relógio).\n\n" +
                "Para trocar de versão, clique com o botão direito no escudo, escolha \"Sair do Fecho\" " +
                "e abra o arquivo novo.",
                "Fecho", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayContext());
    }
}
