using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fecho.Core;

/// <summary>
/// Mensagem que trafega no WebSocket. O conteúdo (Payload) vai em Base64 para
/// que a assinatura cubra exatamente os bytes enviados, sem depender de como
/// cada linguagem formata o JSON.
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";
    /// <summary>Assinatura ECDSA P-256 (DER, Base64) feita no Secure Enclave.</summary>
    [JsonPropertyName("sig")] public string? Sig { get; set; }
    /// <summary>HMAC-SHA256 (Base64) com a chave da sessão, para eventos rápidos.</summary>
    [JsonPropertyName("mac")] public string? Mac { get; set; }
}

/// <summary>Conteúdo assinado de um comando.</summary>
public sealed class CommandPayload
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("args")] public JsonElement Args { get; set; }
    /// <summary>Horário do iPhone em milissegundos desde 1970 (UTC).</summary>
    [JsonPropertyName("ts")] public long Ts { get; set; }
    /// <summary>16 bytes aleatórios em Base64; nunca podem se repetir.</summary>
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    /// <summary>Contador da sessão; só usado com MAC.</summary>
    [JsonPropertyName("seq")] public long? Seq { get; set; }
}

/// <summary>Resposta do agente.</summary>
public sealed class Reply
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>Códigos de erro que o app recebe. São genéricos de propósito.</summary>
public static class ErrorCodes
{
    /// <summary>Falhou alguma verificação de segurança. Não dizemos qual.</summary>
    public const string Refused = "recusado";
    /// <summary>Argumentos fora do formato esperado.</summary>
    public const string Invalid = "invalido";
    /// <summary>Recurso ainda não disponível neste PC.</summary>
    public const string Unavailable = "indisponivel";
    /// <summary>O comando foi aceito mas a ação falhou no Windows.</summary>
    public const string Failed = "falhou";
    /// <summary>Muitos comandos em pouco tempo.</summary>
    public const string RateLimited = "devagar";
    /// <summary>Precisa confirmar com Face ID antes de abrir as pastas do PC.</summary>
    public const string NeedsGrant = "permissao";
}

public enum AuthLevel
{
    /// <summary>Chave de controle: iPhone desbloqueado basta.</summary>
    Control,
    /// <summary>Chave de desbloqueio: exige Face ID a cada uso.</summary>
    Unlock,
}

/// <param name="Level">Qual chave precisa ter assinado.</param>
/// <param name="AllowSession">Se pode chegar pela sessão (HMAC) em vez de assinatura.</param>
public sealed record CommandRule(AuthLevel Level, bool AllowSession);

/// <summary>
/// Tabela única de permissões. Um comando fora desta tabela é recusado.
/// </summary>
public static class CommandPolicy
{
    public const string Pair = "pair";

    private static readonly Dictionary<string, CommandRule> Rules = new(StringComparer.Ordinal)
    {
        ["hello"] = new(AuthLevel.Control, false),
        ["session.open"] = new(AuthLevel.Control, false),
        ["activity"] = new(AuthLevel.Control, false),
        ["lock"] = new(AuthLevel.Control, false),
        ["sleep"] = new(AuthLevel.Control, false),
        ["clipboard.get"] = new(AuthLevel.Control, false),
        ["clipboard.set"] = new(AuthLevel.Control, false),

        ["unlock"] = new(AuthLevel.Unlock, false),
        ["shutdown"] = new(AuthLevel.Unlock, false),
        ["restart"] = new(AuthLevel.Unlock, false),

        ["volume.get"] = new(AuthLevel.Control, true),
        ["volume.set"] = new(AuthLevel.Control, true),
        ["volume.mute"] = new(AuthLevel.Control, true),
        ["volume.app"] = new(AuthLevel.Control, true),
        ["media"] = new(AuthLevel.Control, true),
        ["mouse.move"] = new(AuthLevel.Control, true),
        ["mouse.click"] = new(AuthLevel.Control, true),
        ["mouse.scroll"] = new(AuthLevel.Control, true),
        ["key"] = new(AuthLevel.Control, true),
        ["text"] = new(AuthLevel.Control, true),

        // Arquivos (docs/SEGURANCA.md, seção 9)
        ["files.grant"] = new(AuthLevel.Unlock, false),
        ["files.roots"] = new(AuthLevel.Control, true),
        ["files.list"] = new(AuthLevel.Control, true),
        ["files.read"] = new(AuthLevel.Control, true),
        ["files.upload.begin"] = new(AuthLevel.Control, false),
        ["files.upload.chunk"] = new(AuthLevel.Control, true),
        ["files.upload.end"] = new(AuthLevel.Control, true),
    };

    public static CommandRule? Get(string cmd) => Rules.TryGetValue(cmd, out var r) ? r : null;

    public static IReadOnlyCollection<string> Commands => Rules.Keys;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
