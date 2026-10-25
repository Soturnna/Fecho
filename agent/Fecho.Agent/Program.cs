namespace Fecho.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Uma única cópia do agente por usuário.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Fecho.Agent", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
