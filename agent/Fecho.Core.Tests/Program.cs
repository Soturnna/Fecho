using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fecho.Core;
using Fecho.Server;
using Fecho.Tests;

// Executor mínimo: cada método público estático de Tests que começa com "T_" é um teste.
var tests = typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => m.Name.StartsWith("T_", StringComparison.Ordinal))
    .OrderBy(m => m.MetadataToken)
    .ToList();

var failed = 0;
foreach (var t in tests)
{
    try
    {
        var r = t.Invoke(null, null);
        if (r is Task task) await task;
        Console.WriteLine($"  ok    {t.Name[2..]}");
    }
    catch (Exception ex)
    {
        failed++;
        var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
        Console.WriteLine($"  FALHA {t.Name[2..]}: {inner.Message}");
    }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} testes passaram.");
return failed == 0 ? 0 : 1;

public static class Tests
{
    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    private static AuthResult Auth(Harness h, Envelope e, ConnectionState? c = null) => h.Auth.Authenticate(e, c ?? new ConnectionState());

    // ---------- Assinaturas ----------

    public static void T_comando_assinado_valido_e_aceito()
    {
        var h = new Harness();
        Assert(Auth(h, h.Device.Signed("lock")).Ok, "deveria aceitar");
    }

    public static void T_payload_alterado_e_recusado()
    {
        var h = new Harness();
        var e = h.Device.Signed("lock");
        var bytes = Convert.FromBase64String(e.Payload);
        var tampered = Encoding.UTF8.GetString(bytes).Replace("\"lock\"", "\"sleep\"");
        e.Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered));
        Assert(!Auth(h, e).Ok, "payload alterado passou");
    }

    public static void T_desbloqueio_assinado_com_chave_de_controle_e_recusado()
    {
        var h = new Harness();
        Assert(!Auth(h, h.Device.Signed("unlock", key: h.Device.ControlKey)).Ok, "chave errada passou");
        Assert(!Auth(h, h.Device.Signed("shutdown", key: h.Device.ControlKey)).Ok, "desligar com chave de controle passou");
        Assert(Auth(h, h.Device.Signed("unlock", key: h.Device.UnlockKey)).Ok, "chave certa deveria passar");
    }

    public static void T_comando_de_controle_com_chave_de_desbloqueio_e_recusado()
    {
        var h = new Harness();
        Assert(!Auth(h, h.Device.Signed("lock", key: h.Device.UnlockKey)).Ok, "papéis das chaves misturados");
    }

    public static void T_chave_de_outro_aparelho_e_recusada()
    {
        var h = new Harness();
        var intruso = new TestDevice { Clock = h.Clock };
        var e = h.Device.Signed("lock", key: intruso.ControlKey);
        Assert(!Auth(h, e).Ok, "assinatura de outra chave passou");
    }

    public static void T_horario_fora_da_janela_e_recusado()
    {
        var h = new Harness();
        Assert(!Auth(h, h.Device.Signed("lock", ts: h.Clock.NowMs - 31_000)).Ok, "passado aceito");
        Assert(!Auth(h, h.Device.Signed("lock", ts: h.Clock.NowMs + 31_000)).Ok, "futuro aceito");
        Assert(Auth(h, h.Device.Signed("lock", ts: h.Clock.NowMs - 29_000)).Ok, "dentro da janela recusado");
    }

    public static void T_replay_do_mesmo_comando_e_recusado()
    {
        var h = new Harness();
        var e = h.Device.Signed("lock");
        Assert(Auth(h, e).Ok, "primeira vez deveria passar");
        Assert(!Auth(h, e).Ok, "replay passou");
    }

    public static void T_comando_para_outro_agente_e_recusado()
    {
        var h = new Harness();
        Assert(!Auth(h, h.Device.Signed("lock", agent: "outro-pc")).Ok, "comando de outro PC passou");
    }

    public static void T_comando_desconhecido_e_recusado()
    {
        var h = new Harness();
        Assert(!Auth(h, h.Device.Signed("format.c")).Ok, "comando desconhecido passou");
    }

    public static void T_aparelho_nao_pareado_e_recusado()
    {
        var h = new Harness(paired: false);
        Assert(!Auth(h, h.Device.Signed("lock")).Ok, "aparelho não pareado passou");
    }

    public static void T_nonce_curto_e_recusado()
    {
        var h = new Harness();
        Assert(!Auth(h, h.Device.Signed("lock", nonce: Convert.ToBase64String(new byte[4]))).Ok, "nonce curto passou");
    }

    public static void T_limite_de_comandos_por_segundo()
    {
        var h = new Harness();
        var recusou = false;
        for (var i = 0; i < 40; i++)
        {
            var r = Auth(h, h.Device.Signed("hello"));
            if (r.RateLimited) { recusou = true; break; }
        }
        Assert(recusou, "nenhum limite aplicado");
    }

    // ---------- Sessões (HMAC) ----------

    private static (Harness h, ConnectionState conn, byte[] key) OpenSession()
    {
        var h = new Harness();
        var conn = new ConnectionState();
        var r = Auth(h, h.Device.Signed("session.open"), conn);
        Assert(r.Ok, "session.open recusado");
        h.Dispatcher.Execute(r.Payload!, r.Device!, conn);
        return (h, conn, conn.SessionKey!);
    }

    public static void T_sessao_aceita_evento_rapido()
    {
        var (h, conn, key) = OpenSession();
        Assert(Auth(h, h.Device.Session("volume.set", new { value = 30 }, key, 1), conn).Ok, "evento válido recusado");
        Assert(Auth(h, h.Device.Session("mouse.move", new { dx = 3, dy = 4 }, key, 2), conn).Ok, "segundo evento recusado");
    }

    public static void T_sessao_recusa_seq_repetido()
    {
        var (h, conn, key) = OpenSession();
        Assert(Auth(h, h.Device.Session("volume.set", new { value = 30 }, key, 5), conn).Ok, "primeiro recusado");
        Assert(!Auth(h, h.Device.Session("volume.set", new { value = 30 }, key, 5), conn).Ok, "seq repetido passou");
        Assert(!Auth(h, h.Device.Session("volume.set", new { value = 30 }, key, 4), conn).Ok, "seq menor passou");
    }

    public static void T_sessao_nao_autoriza_desbloquear_desligar_ou_bloquear()
    {
        var (h, conn, key) = OpenSession();
        Assert(!Auth(h, h.Device.Session("unlock", new { }, key, 1), conn).Ok, "desbloqueio por sessão passou");
        Assert(!Auth(h, h.Device.Session("shutdown", new { }, key, 2), conn).Ok, "desligar por sessão passou");
        Assert(!Auth(h, h.Device.Session("clipboard.get", new { }, key, 3), conn).Ok, "área de transferência por sessão passou");
    }

    public static void T_sessao_com_chave_errada_e_recusada()
    {
        var (h, conn, _) = OpenSession();
        Assert(!Auth(h, h.Device.Session("volume.set", new { value = 30 }, RandomNumberGenerator.GetBytes(32), 1), conn).Ok, "chave falsa passou");
    }

    public static void T_sessao_de_outra_conexao_e_recusada()
    {
        var (h, _, key) = OpenSession();
        Assert(!Auth(h, h.Device.Session("volume.set", new { value = 30 }, key, 1), new ConnectionState()).Ok, "sessão vazou para outra conexão");
    }

    // ---------- Pareamento ----------

    private static Envelope PairEnvelope(Harness h, TestDevice d, string token, ECDsa? signer = null, bool sameKeys = false)
    {
        var unlockPub = sameKeys ? TestDevice.PublicX963(d.ControlKey) : TestDevice.PublicX963(d.UnlockKey);
        return d.Signed(CommandPolicy.Pair, new
        {
            token,
            name = "iPhone de Marcos",
            controlKey = Convert.ToBase64String(TestDevice.PublicX963(d.ControlKey)),
            unlockKey = Convert.ToBase64String(unlockPub),
        }, key: signer);
    }

    public static void T_pareamento_valido_registra_o_aparelho()
    {
        var h = new Harness(paired: false);
        var offer = h.Pairing.Begin(TimeSpan.FromMinutes(2));
        Assert(Auth(h, PairEnvelope(h, h.Device, offer.Token)).Ok, "pareamento válido recusado");
        Assert(h.Registry.Get(h.Device.Id) is not null, "aparelho não registrado");
        Assert(Auth(h, h.Device.Signed("lock")).Ok, "comando após parear recusado");
    }

    public static void T_token_de_pareamento_so_vale_uma_vez()
    {
        var h = new Harness(paired: false);
        var offer = h.Pairing.Begin(TimeSpan.FromMinutes(2));
        Assert(Auth(h, PairEnvelope(h, h.Device, offer.Token)).Ok, "primeiro uso recusado");
        var outro = new TestDevice { Clock = h.Clock };
        Assert(!Auth(h, PairEnvelope(h, outro, offer.Token)).Ok, "token reutilizado");
    }

    public static void T_token_expirado_e_recusado()
    {
        var h = new Harness(paired: false);
        var offer = h.Pairing.Begin(TimeSpan.FromMinutes(2));
        h.Clock.NowMs += 121_000;
        Assert(!Auth(h, PairEnvelope(h, h.Device, offer.Token)).Ok, "token expirado aceito");
    }

    public static void T_cinco_tokens_errados_cancelam_o_pareamento()
    {
        var h = new Harness(paired: false);
        var offer = h.Pairing.Begin(TimeSpan.FromMinutes(2));
        for (var i = 0; i < PairingManager.MaxFailedAttempts; i++)
        {
            h.Clock.NowMs += 1_100; // respeita o limite de 1 tentativa por segundo
            Assert(!Auth(h, PairEnvelope(h, new TestDevice { Clock = h.Clock }, Crypto.Base64Url(RandomNumberGenerator.GetBytes(32)))).Ok, "token falso aceito");
        }
        h.Clock.NowMs += 1_100;
        Assert(!Auth(h, PairEnvelope(h, h.Device, offer.Token)).Ok, "token certo aceito após ataque");
    }

    public static void T_pareamento_sem_posse_da_chave_e_recusado()
    {
        var h = new Harness(paired: false);
        var offer = h.Pairing.Begin(TimeSpan.FromMinutes(2));
        var intruso = new TestDevice { Clock = h.Clock };
        Assert(!Auth(h, PairEnvelope(h, h.Device, offer.Token, signer: intruso.ControlKey)).Ok, "pareou sem a chave");
        Assert(h.Pairing.IsActive, "assinatura inválida não deveria gastar o token");
    }

    public static void T_pareamento_com_chaves_iguais_e_recusado()
    {
        var h = new Harness(paired: false);
        var offer = h.Pairing.Begin(TimeSpan.FromMinutes(2));
        Assert(!Auth(h, PairEnvelope(h, h.Device, offer.Token, sameKeys: true)).Ok, "chaves iguais aceitas");
    }

    public static void T_pareamento_sem_janela_aberta_e_recusado()
    {
        var h = new Harness(paired: false);
        Assert(!Auth(h, PairEnvelope(h, h.Device, Crypto.Base64Url(RandomNumberGenerator.GetBytes(32)))).Ok, "pareou sem QR");
    }

    // ---------- Revogação ----------

    public static void T_aparelho_removido_perde_acesso_e_sessao()
    {
        var (h, conn, key) = OpenSession();
        h.Registry.Remove(h.Device.Id);
        Assert(!Auth(h, h.Device.Signed("lock")).Ok, "aparelho removido ainda manda");
        Assert(!Auth(h, h.Device.Session("volume.set", new { value = 10 }, key, 1), conn).Ok, "sessão sobreviveu à remoção");
    }

    // ---------- Validação de argumentos ----------

    private static void ExpectInvalid(Harness h, string cmd, object args)
    {
        var r = Auth(h, h.Device.Signed(cmd, args));
        Assert(r.Ok, $"{cmd}: autenticação falhou");
        try
        {
            h.Dispatcher.Execute(r.Payload!, r.Device!, new ConnectionState());
            throw new Exception($"{cmd}: argumento inválido aceito");
        }
        catch (CommandException ex) when (ex.Code == ErrorCodes.Invalid) { }
    }

    public static void T_argumentos_invalidos_sao_recusados()
    {
        var h = new Harness();
        ExpectInvalid(h, "volume.set", new { value = 101 });
        ExpectInvalid(h, "volume.set", new { value = "50" });
        ExpectInvalid(h, "key", new { key = "ctrl+alt+del" });
        ExpectInvalid(h, "key", new { key = "c", mods = new[] { "ctrl", "ctrl" } });
        ExpectInvalid(h, "text", new { text = "a\u0007b" });
        ExpectInvalid(h, "text", new { text = new string('a', 1001) });
        ExpectInvalid(h, "media", new { action = "eject" });
        ExpectInvalid(h, "mouse.move", new { dx = 99999, dy = 0 });
        Assert(h.Actions.Calls.Count == 0, "alguma ação inválida foi executada");
    }

    public static void T_atalho_permitido_chega_ao_windows()
    {
        var h = new Harness();
        var r = Auth(h, h.Device.Signed("key", new { key = "c", mods = new[] { "ctrl" } }));
        h.Dispatcher.Execute(r.Payload!, r.Device!, new ConnectionState());
        Assert(h.Actions.Calls.Contains("key:ctrl+c"), "atalho não executado");
    }

    public static void T_desbloqueio_sem_componente_instalado_responde_indisponivel()
    {
        var h = new Harness();
        var r = Auth(h, h.Device.Signed("unlock", key: h.Device.UnlockKey));
        try
        {
            h.Dispatcher.Execute(r.Payload!, r.Device!, new ConnectionState());
            throw new Exception("deveria falhar");
        }
        catch (CommandException ex) when (ex.Code == ErrorCodes.Unavailable) { }
    }

    // ---------- Rede ----------

    public static void T_somente_enderecos_da_rede_local()
    {
        Assert(NetworkInfo.IsLocalAddress(IPAddress.Parse("192.168.0.10")), "192.168 recusado");
        Assert(NetworkInfo.IsLocalAddress(IPAddress.Parse("10.1.2.3")), "10.x recusado");
        Assert(NetworkInfo.IsLocalAddress(IPAddress.Parse("172.20.0.1")), "172.20 recusado");
        Assert(NetworkInfo.IsLocalAddress(IPAddress.Parse("::ffff:192.168.1.5")), "IPv4 mapeado recusado");
        Assert(!NetworkInfo.IsLocalAddress(IPAddress.Parse("172.32.0.1")), "172.32 aceito");
        Assert(!NetworkInfo.IsLocalAddress(IPAddress.Parse("8.8.8.8")), "internet aceita");
        Assert(!NetworkInfo.IsLocalAddress(IPAddress.Parse("2001:4860::8888")), "IPv6 público aceito");
    }

    // ---------- Ponta a ponta: TLS real + WebSocket ----------

    public static async Task T_ponta_a_ponta_tls_pareamento_e_comando()
    {
        var clock = new SystemClock();
        var registry = new DeviceRegistry(new InMemoryDeviceStorage());
        var pairing = new PairingManager(clock);
        var audit = new AuditLog(clock);
        var actions = new FakeActions();
        var cert = AgentCertificate.Create();
        var fingerprint = AgentCertificate.Fingerprint(cert);
        var port = 47000 + Random.Shared.Next(1000);
        await using var server = new AgentServer(
            new AgentServerOptions { Port = port, Certificate = cert, AgentId = "agente-teste" },
            new Authenticator("agente-teste", registry, pairing, clock),
            new CommandDispatcher(actions, audit), registry, audit);
        await server.StartAsync();

        var device = new TestDevice { Clock = clock };
        var offer = pairing.Begin(TimeSpan.FromMinutes(2));

        using var ws = await Connect(port, fingerprint);
        var pair = await Send(ws, d => PairEnvelope(new Harness(), d, offer.Token), device);
        Assert(pair.GetProperty("ok").GetBoolean(), "pareamento por TLS falhou");

        var hello = await Send(ws, d => d.Signed("hello"), device);
        Assert(hello.GetProperty("ok").GetBoolean(), "hello falhou");
        Assert(hello.GetProperty("result").GetProperty("version").GetString() == "teste", "status errado");

        var lockReply = await Send(ws, d => d.Signed("lock"), device);
        Assert(lockReply.GetProperty("ok").GetBoolean() && actions.Calls.Contains("lock"), "bloquear não executou");

        var bad = await Send(ws, d => d.Signed("shutdown"), device);
        Assert(!bad.GetProperty("ok").GetBoolean() && bad.GetProperty("error").GetString() == ErrorCodes.Refused,
            "desligar com chave de controle deveria ser recusado com erro genérico");
        Assert(!actions.Calls.Contains("shutdown"), "desligou sem Face ID");
    }

    public static async Task T_ponta_a_ponta_certificado_diferente_derruba_a_conexao()
    {
        var clock = new SystemClock();
        var registry = new DeviceRegistry(new InMemoryDeviceStorage());
        var audit = new AuditLog(clock);
        var cert = AgentCertificate.Create();
        var port = 48000 + Random.Shared.Next(1000);
        await using var server = new AgentServer(
            new AgentServerOptions { Port = port, Certificate = cert, AgentId = "a" },
            new Authenticator("a", registry, new PairingManager(clock), clock),
            new CommandDispatcher(new FakeActions(), audit), registry, audit);
        await server.StartAsync();

        var falso = AgentCertificate.Fingerprint(AgentCertificate.Create());
        try
        {
            using var ws = await Connect(port, falso);
            throw new Exception("conectou com impressão digital errada");
        }
        catch (WebSocketException) { }
    }

    private static async Task<ClientWebSocket> Connect(int port, string expectedFingerprint)
    {
        var ws = new ClientWebSocket();
        // Mesma regra do app: aceita somente o certificado cuja impressão digital veio no QR.
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant() == expectedFingerprint;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/ws"), CancellationToken.None);
        return ws;
    }

    private static async Task<JsonElement> Send(ClientWebSocket ws, Func<TestDevice, Envelope> make, TestDevice device)
    {
        var bytes = TestDevice.ToBytes(make(device));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64 * 1024];
        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
        return JsonDocument.Parse(buffer.AsMemory(0, r.Count)).RootElement.Clone();
    }
}
