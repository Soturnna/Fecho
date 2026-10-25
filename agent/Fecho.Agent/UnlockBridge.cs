using System.IO.Pipes;
using System.Text;

namespace Fecho.Agent;

public enum BridgeResult { Sent, NotRunning, Refused }

/// <summary>
/// Entrega a senha ao componente de desbloqueio (a DLL que roda na tela de
/// login) por um named pipe local. O pipe só existe enquanto o PC está
/// bloqueado e a DLL está carregada.
/// </summary>
public static class UnlockBridge
{
    private const string PipeName = "Fecho.Unlock";
    private static string SecretFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fecho", "unlock.secret");

    public static BridgeResult Send(string domain, string user, string password)
    {
        string secret;
        try { secret = File.ReadAllText(SecretFile).Trim(); }
        catch (IOException) { return BridgeResult.NotRunning; }
        catch (UnauthorizedAccessException) { return BridgeResult.NotRunning; }
        if (secret.Length == 0) return BridgeResult.NotRunning;

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            pipe.ReadMode = PipeTransmissionMode.Message;

            var msg = $"FECHO1\n{secret}\n{domain}\n{user}\n{password}\n";
            var bytes = Encoding.UTF8.GetBytes(msg);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            Array.Clear(bytes);

            var buffer = new byte[16];
            var read = pipe.Read(buffer, 0, buffer.Length);
            var reply = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return reply.StartsWith("OK", StringComparison.Ordinal) ? BridgeResult.Sent : BridgeResult.Refused;
        }
        catch (TimeoutException) { return BridgeResult.NotRunning; }
        catch (IOException) { return BridgeResult.NotRunning; }
    }
}
