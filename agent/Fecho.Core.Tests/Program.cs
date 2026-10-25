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
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Console.WriteLine($"::error title=Teste {t.Name[2..]}::{inner.GetType().Name}: {inner.Message.Replace('\n', ' ')}");
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

    // ---------- Arquivos ----------

    private sealed class FileHarness : IDisposable
    {
        public string Base { get; } = Path.Combine(Path.GetTempPath(), "fecho-teste-" + Guid.NewGuid().ToString("N"));
        public string Docs => Path.Combine(Base, "Documentos");
        public string Secret => Path.Combine(Base, "Segredo");
        public string Uploads => Path.Combine(Base, "Downloads", "Fecho");
        public Harness H { get; }
        public FileService Files { get; }
        public CommandDispatcher Dispatcher { get; }
        public ConnectionState Conn { get; } = new();

        public FileHarness()
        {
            Directory.CreateDirectory(Path.Combine(Docs, "Projetos"));
            Directory.CreateDirectory(Secret);
            File.WriteAllText(Path.Combine(Docs, "nota.txt"), "olá do PC");
            File.WriteAllText(Path.Combine(Docs, ".escondido"), "x");
            File.WriteAllText(Path.Combine(Secret, "senhas.txt"), "não pode sair");
            H = new Harness();
            Files = new FileService(new Dictionary<string, (string, string)> { ["documents"] = ("Documentos", Docs) }, Uploads);
            Dispatcher = new CommandDispatcher(H.Actions, H.Audit, Files, H.Clock);
        }

        public object? Run(string cmd, object args, ECDsa? key = null)
        {
            var r = H.Auth.Authenticate(H.Device.Signed(cmd, args, key: key), Conn);
            if (!r.Ok) throw new Exception($"{cmd}: autenticação recusada ({r.Reason})");
            return Dispatcher.Execute(r.Payload!, r.Device!, Conn);
        }

        public string ErrorOf(string cmd, object args, ECDsa? key = null)
        {
            try { Run(cmd, args, key); return "nenhum"; }
            catch (CommandException ex) { return ex.Code; }
        }

        public void Grant()
        {
            H.Clock.NowMs += 1_000;
            Run("files.grant", new { }, H.Device.UnlockKey);
        }

        public void Dispose()
        {
            Conn.Release();
            try { Directory.Delete(Base, recursive: true); } catch (IOException) { }
        }
    }

    public static void T_pastas_do_pc_exigem_face_id()
    {
        using var f = new FileHarness();
        Assert(f.ErrorOf("files.list", new { root = "documents", path = "" }) == ErrorCodes.NeedsGrant, "listou sem Face ID");
        var r = f.H.Auth.Authenticate(f.H.Device.Signed("files.grant", new { }, key: f.H.Device.ControlKey), f.Conn);
        Assert(!r.Ok, "permissão concedida com a chave de controle");
        f.Grant();
        var list = (List<FileEntry>)f.Run("files.list", new { root = "documents", path = "" })!;
        Assert(list.Any(e => e.Name == "nota.txt") && list.Any(e => e.Name == "Projetos" && e.Dir), "listagem incompleta");
        Assert(list.All(e => !e.Name.StartsWith('.')), "arquivo oculto apareceu");
    }

    public static void T_permissao_de_arquivos_expira_em_10_minutos()
    {
        using var f = new FileHarness();
        f.Grant();
        f.H.Clock.NowMs += (long)CommandDispatcher.FilesGrantDuration.TotalMilliseconds + 1_000;
        Assert(f.ErrorOf("files.list", new { root = "documents", path = "" }) == ErrorCodes.NeedsGrant, "permissão não expirou");
    }

    public static void T_permissao_de_arquivos_nao_passa_para_outra_conexao()
    {
        using var f = new FileHarness();
        f.Grant();
        var outra = new ConnectionState();
        var r = f.H.Auth.Authenticate(f.H.Device.Signed("files.list", new { root = "documents", path = "" }), outra);
        try
        {
            f.Dispatcher.Execute(r.Payload!, r.Device!, outra);
            throw new Exception("outra conexão herdou a permissão");
        }
        catch (CommandException ex) when (ex.Code == ErrorCodes.NeedsGrant) { }
    }

    public static void T_caminhos_que_tentam_sair_da_pasta_sao_recusados()
    {
        using var f = new FileHarness();
        f.Grant();
        foreach (var bad in new[] { "..", "../Segredo", "Projetos/../../Segredo/senhas.txt", "/etc/passwd",
                                    "Projetos//nota.txt", "..\\Segredo", "C:\\Windows", ".escondido", "nota.txt ", "Projetos/." })
        {
            Assert(f.ErrorOf("files.read", new { root = "documents", path = bad, offset = 0, length = 100 }) == ErrorCodes.Invalid,
                $"caminho aceito: {bad}");
        }
        Assert(f.ErrorOf("files.list", new { root = "sistema", path = "" }) == ErrorCodes.Invalid, "raiz inventada aceita");
    }

    public static void T_atalho_para_fora_da_pasta_e_recusado()
    {
        using var f = new FileHarness();
        var link = Path.Combine(f.Docs, "atalho");
        try { Directory.CreateSymbolicLink(link, f.Secret); }
        catch (Exception) { return; } // sem permissão para criar link neste sistema: teste não se aplica
        f.Grant();
        Assert(f.ErrorOf("files.read", new { root = "documents", path = "atalho/senhas.txt", offset = 0, length = 100 }) == ErrorCodes.Invalid,
            "atalho deixou sair da pasta");
        var list = (List<FileEntry>)f.Run("files.list", new { root = "documents", path = "" })!;
        Assert(list.All(e => e.Name != "atalho"), "atalho apareceu na listagem");
    }

    public static void T_leitura_de_arquivo_em_blocos()
    {
        using var f = new FileHarness();
        f.Grant();
        var json = JsonSerializer.Serialize(f.Run("files.read", new { root = "documents", path = "nota.txt", offset = 0, length = 4 }));
        var doc = JsonDocument.Parse(json).RootElement;
        Assert(Convert.FromBase64String(doc.GetProperty("data").GetString()!).Length == 4, "bloco de tamanho errado");
        Assert(!doc.GetProperty("eof").GetBoolean(), "fim de arquivo cedo demais");
    }

    private static string Upload(FileHarness f, string name, byte[] content, byte[]? hashOverride = null)
    {
        var begin = JsonDocument.Parse(JsonSerializer.Serialize(f.Run("files.upload.begin", new { name, size = content.Length }))).RootElement;
        var id = begin.GetProperty("id").GetString()!;
        for (var off = 0; off < content.Length; off += 10)
        {
            var part = content.AsSpan(off, Math.Min(10, content.Length - off)).ToArray();
            f.Run("files.upload.chunk", new { id, offset = off, data = Convert.ToBase64String(part) });
        }
        var hash = hashOverride ?? SHA256.HashData(content);
        var end = JsonDocument.Parse(JsonSerializer.Serialize(f.Run("files.upload.end", new { id, sha256 = Convert.ToBase64String(hash) }))).RootElement;
        return end.GetProperty("name").GetString()!;
    }

    public static void T_envio_de_arquivo_chega_inteiro_na_pasta_do_fecho()
    {
        using var f = new FileHarness();
        var content = Encoding.UTF8.GetBytes("conteúdo enviado pelo iPhone, em vários blocos");
        var name = Upload(f, "foto.jpg", content);
        Assert(name == "foto.jpg", "nome mudou: " + name);
        Assert(File.ReadAllBytes(Path.Combine(f.Uploads, name)).SequenceEqual(content), "conteúdo diferente");
        Assert(Upload(f, "foto.jpg", content) == "foto (2).jpg", "não criou nome único");
        Assert(f.H.Audit.RecentForApp(5).Any(e => e.Cmd == "file.received"), "envio não entrou na atividade");
    }

    public static void T_envio_nao_escapa_da_pasta_e_limpa_nomes()
    {
        using var f = new FileHarness();
        var content = new byte[] { 1, 2, 3 };
        Assert(Upload(f, "../../Segredo/virus.exe", content) == "virus.exe", "nome com caminho aceito");
        Assert(File.Exists(Path.Combine(f.Uploads, "virus.exe")), "arquivo fora do lugar");
        Assert(!File.Exists(Path.Combine(f.Secret, "virus.exe")), "escapou para outra pasta");
        Assert(Upload(f, "CON.txt", content) == "_CON.txt", "nome reservado do Windows aceito");
        Assert(Upload(f, "a<b>c:d.txt", content) == "abcd.txt", "caracteres proibidos mantidos");
        Assert(Upload(f, "...", content) == "arquivo", "nome vazio não tratado");
    }

    public static void T_envio_com_hash_errado_e_descartado()
    {
        using var f = new FileHarness();
        try
        {
            Upload(f, "adulterado.txt", new byte[] { 9, 9, 9 }, hashOverride: new byte[32]);
            throw new Exception("aceitou hash errado");
        }
        catch (CommandException ex) when (ex.Code == ErrorCodes.Invalid) { }
        Assert(!File.Exists(Path.Combine(f.Uploads, "adulterado.txt")), "arquivo adulterado ficou");
        Assert(Directory.GetFiles(Path.Combine(f.Uploads, ".parcial")).Length == 0, "parcial não foi apagado");
    }

    public static void T_envio_fora_de_ordem_ou_maior_que_o_declarado_e_recusado()
    {
        using var f = new FileHarness();
        var begin = JsonDocument.Parse(JsonSerializer.Serialize(f.Run("files.upload.begin", new { name = "x.bin", size = 5 }))).RootElement;
        var id = begin.GetProperty("id").GetString()!;
        Assert(f.ErrorOf("files.upload.chunk", new { id, offset = 3, data = Convert.ToBase64String(new byte[2]) }) == ErrorCodes.Invalid, "fora de ordem aceito");
        var b2 = JsonDocument.Parse(JsonSerializer.Serialize(f.Run("files.upload.begin", new { name = "y.bin", size = 2 }))).RootElement;
        var id2 = b2.GetProperty("id").GetString()!;
        Assert(f.ErrorOf("files.upload.chunk", new { id = id2, offset = 0, data = Convert.ToBase64String(new byte[5]) }) == ErrorCodes.Invalid, "maior que o declarado aceito");
        Assert(f.ErrorOf("files.upload.begin", new { name = "grande.iso", size = FileService.MaxUploadBytes + 1 }) == ErrorCodes.Invalid, "acima do limite aceito");
    }

    public static void T_conexao_fechada_apaga_envios_pela_metade()
    {
        using var f = new FileHarness();
        var begin = JsonDocument.Parse(JsonSerializer.Serialize(f.Run("files.upload.begin", new { name = "meio.bin", size = 100 }))).RootElement;
        var id = begin.GetProperty("id").GetString()!;
        f.Run("files.upload.chunk", new { id, offset = 0, data = Convert.ToBase64String(new byte[10]) });
        f.Conn.Release();
        Assert(Directory.GetFiles(Path.Combine(f.Uploads, ".parcial")).Length == 0, "parcial sobrou após desconectar");
        Assert(!File.Exists(Path.Combine(f.Uploads, "meio.bin")), "arquivo incompleto publicado");
    }

    // ---------- Tela e mouse absoluto ----------

    public static void T_mouse_absoluto_mapeia_coordenadas()
    {
        var h = new Harness();
        var r = Auth(h, h.Device.Signed("mouse.moveto", new { x = 0.5, y = 0.25 }));
        h.Dispatcher.Execute(r.Payload!, r.Device!, new ConnectionState());
        Assert(h.Actions.Calls.Contains("moveto:0.5,0.25"), "moveto não mapeou: " + string.Join(";", h.Actions.Calls));
        var r2 = Auth(h, h.Device.Signed("mouse.clickat", new { x = 1, y = 0, button = "right" }));
        h.Dispatcher.Execute(r2.Payload!, r2.Device!, new ConnectionState());
        Assert(h.Actions.Calls.Any(c => c.StartsWith("clickat:Right:")), "clique direito por posição não registrou");
    }

    public static void T_coordenada_fora_do_intervalo_e_presa()
    {
        var h = new Harness();
        var r = Auth(h, h.Device.Signed("mouse.moveto", new { x = 5.0, y = -2.0 }));
        h.Dispatcher.Execute(r.Payload!, r.Device!, new ConnectionState());
        Assert(h.Actions.Calls.Contains("moveto:1,0"), "coordenada não foi presa a 0..1");
    }

    public static void T_screen_start_e_stop_controlam_o_streaming()
    {
        var h = new Harness();
        var conn = new ConnectionState();
        Assert(!conn.WantsScreen, "começou querendo a tela");
        var r = Auth(h, h.Device.Signed("screen.start", new { maxWidth = 800, quality = 40 }), conn);
        h.Dispatcher.Execute(r.Payload!, r.Device!, conn);
        Assert(conn.WantsScreen && conn.ScreenMaxWidth == 800 && conn.ScreenQuality == 40, "screen.start não ligou");
        var r2 = Auth(h, h.Device.Signed("screen.stop"), conn);
        h.Dispatcher.Execute(r2.Payload!, r2.Device!, conn);
        Assert(!conn.WantsScreen, "screen.stop não desligou");
    }

    public static void T_streaming_para_quando_a_conexao_fecha()
    {
        var conn = new ConnectionState { WantsScreen = true };
        conn.Release();
        Assert(!conn.WantsScreen, "streaming sobreviveu ao fechamento da conexão");
    }

    public static async Task T_ponta_a_ponta_streaming_da_tela()
    {
        var clock = new SystemClock();
        var registry = new DeviceRegistry(new InMemoryDeviceStorage());
        var pairing = new PairingManager(clock);
        var audit = new AuditLog(clock);
        var cert = AgentCertificate.CreateForServer();
        var fp = AgentCertificate.Fingerprint(cert);
        var screen = new FakeScreenSource();
        var port = 49000 + Random.Shared.Next(900);
        await using var server = new AgentServer(
            new AgentServerOptions { Port = port, Certificate = cert, AgentId = "agente-teste" },
            new Authenticator("agente-teste", registry, pairing, clock),
            new CommandDispatcher(new FakeActions(), audit), registry, audit, screen);
        await server.StartAsync();

        var device = new TestDevice { Clock = clock };
        var offer = pairing.Begin(TimeSpan.FromMinutes(2));
        using var ws = await Connect(port, fp);
        var pair = await Send(ws, d => PairEnvelope(new Harness(), d, offer.Token), device);
        Assert(pair.GetProperty("ok").GetBoolean(), "pareamento falhou");

        await ws.SendAsync(TestDevice.ToBytes(device.Signed("screen.start", new { maxWidth = 640, quality = 40 })),
            WebSocketMessageType.Text, true, CancellationToken.None);
        var frame = await WaitForBinary(ws, TimeSpan.FromSeconds(4));
        Assert(frame is { Length: 4 }, "nenhum quadro binário chegou pela conexão");
        Assert(screen.Captures > 0, "a tela não foi capturada");
    }

    private static async Task<byte[]?> WaitForBinary(ClientWebSocket ws, TimeSpan timeout)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Binary) return buffer[..r.Count];
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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
        var cert = AgentCertificate.CreateForServer();
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
        var cert = AgentCertificate.CreateForServer();
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
