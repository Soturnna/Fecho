using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Fecho.Core;

public sealed class FileEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dir")] public bool Dir { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("modified")] public long Modified { get; set; }
}

/// <summary>Um envio em andamento, preso a uma conexão.</summary>
public sealed class UploadSession : IDisposable
{
    public string Id { get; } = Crypto.Base64Url(Crypto.RandomBytes(12));
    public required string FinalName { get; init; }
    public required string PartialPath { get; init; }
    public required long Size { get; init; }
    public required FileStream Stream { get; init; }
    public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long Written { get; set; }
    public bool Finished { get; set; }

    /// <summary>Fecha e apaga o arquivo parcial (envio abandonado ou com erro).</summary>
    public void Dispose()
    {
        Stream.Dispose();
        Hash.Dispose();
        if (!Finished)
        {
            try { File.Delete(PartialPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// Acesso a arquivos com regras rígidas (docs/SEGURANCA.md, seção 9):
/// só as pastas pessoais listadas, sem "..", sem atalhos/junções, sem
/// arquivos ocultos ou de sistema. Envios caem sempre na mesma pasta.
/// </summary>
public sealed class FileService
{
    public const long MaxUploadBytes = 500L * 1024 * 1024;
    public const int MaxChunkBytes = 48 * 1024;
    public const int MaxListEntries = 500;
    public const int MaxUploadsPerConnection = 3;

    private static readonly char[] ForbiddenNameChars =
        "<>:\"/\\|?*".ToCharArray().Concat(Path.GetInvalidFileNameChars()).Distinct().ToArray();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly Dictionary<string, (string Label, string Path)> _roots;
    private readonly string _uploadDir;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Disparado quando um arquivo termina de chegar (nome final).</summary>
    public event Action<string>? Received;

    /// <param name="roots">chave → (rótulo, caminho absoluto). Ex.: "documents" → ("Documentos", "C:\Users\...").</param>
    public FileService(IReadOnlyDictionary<string, (string Label, string Path)> roots, string uploadDir)
    {
        _roots = roots
            .Where(r => Directory.Exists(r.Value.Path))
            .ToDictionary(r => r.Key, r => (r.Value.Label, Path.GetFullPath(r.Value.Path)), StringComparer.Ordinal);
        _uploadDir = Path.GetFullPath(uploadDir);
    }

    public string UploadDirectory => _uploadDir;

    public IReadOnlyList<object> Roots() =>
        _roots.Select(r => (object)new { id = r.Key, name = r.Value.Label }).ToList();

    // ---------- Navegação ----------

    public List<FileEntry> List(string root, string relative)
    {
        var dir = Resolve(root, relative);
        if (!Directory.Exists(dir)) throw new CommandException(ErrorCodes.Invalid, "pasta inexistente");

        var info = new DirectoryInfo(dir);
        var entries = new List<FileEntry>();
        foreach (var item in info.EnumerateFileSystemInfos())
        {
            if (IsBlocked(item.Attributes) || item.Name.StartsWith('.')) continue;
            entries.Add(new FileEntry
            {
                Name = item.Name,
                Dir = item is DirectoryInfo,
                Size = item is FileInfo f ? f.Length : 0,
                Modified = new DateTimeOffset(item.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            });
            if (entries.Count >= MaxListEntries) break;
        }
        return entries
            .OrderByDescending(e => e.Dir)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public (byte[] Data, long Size, bool Eof) Read(string root, string relative, long offset, int length)
    {
        if (length <= 0 || length > MaxChunkBytes || offset < 0) throw new CommandException(ErrorCodes.Invalid, "faixa");
        var path = Resolve(root, relative);
        if (!File.Exists(path)) throw new CommandException(ErrorCodes.Invalid, "arquivo inexistente");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > fs.Length) throw new CommandException(ErrorCodes.Invalid, "faixa");
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(length, fs.Length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (read != buffer.Length) Array.Resize(ref buffer, read);
        return (buffer, fs.Length, offset + read >= fs.Length);
    }

    /// <summary>
    /// Converte (pasta raiz, caminho relativo) em caminho absoluto e garante
    /// que ele continua dentro da raiz. Recusa "..", ":", "\", segmentos
    /// vazios e qualquer atalho/junção no caminho.
    /// </summary>
    public string Resolve(string root, string relative)
    {
        if (!_roots.TryGetValue(root, out var r)) throw new CommandException(ErrorCodes.Invalid, "raiz");
        if (relative.Length > 1024) throw new CommandException(ErrorCodes.Invalid, "caminho longo");

        var current = r.Path;
        if (relative.Length == 0) return current;

        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(ForbiddenNameChars) >= 0
                || segment.Any(char.IsControl) || segment.StartsWith('.') || segment != segment.Trim()
                || segment.EndsWith('.'))
                throw new CommandException(ErrorCodes.Invalid, "segmento");

            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attrs = File.GetAttributes(current);
                if (IsBlocked(attrs)) throw new CommandException(ErrorCodes.Invalid, "bloqueado");
            }
        }

        var full = Path.GetFullPath(current);
        var prefix = r.Path.EndsWith(Path.DirectorySeparatorChar) ? r.Path : r.Path + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison)) throw new CommandException(ErrorCodes.Invalid, "fora da raiz");
        return full;
    }

    private static bool IsBlocked(FileAttributes a) =>
        (a & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Device)) != 0;

    // ---------- Envio do iPhone para o PC ----------

    public UploadSession BeginUpload(string name, long size, int activeUploads)
    {
        if (size < 0 || size > MaxUploadBytes) throw new CommandException(ErrorCodes.Invalid, "tamanho");
        if (activeUploads >= MaxUploadsPerConnection) throw new CommandException(ErrorCodes.RateLimited, "envios demais");

        Directory.CreateDirectory(_uploadDir);
        var partialDir = Path.Combine(_uploadDir, ".parcial");
        var created = Directory.CreateDirectory(partialDir);
        if (OperatingSystem.IsWindows()) created.Attributes |= FileAttributes.Hidden;

        var finalName = SanitizeName(name);
        var partial = Path.Combine(partialDir, Crypto.Base64Url(Crypto.RandomBytes(12)) + ".part");
        var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return new UploadSession { FinalName = finalName, PartialPath = partial, Size = size, Stream = stream };
    }

    public void WriteChunk(UploadSession s, long offset, byte[] data)
    {
        if (s.Finished) throw new CommandException(ErrorCodes.Invalid, "encerrado");
        if (data.Length == 0 || data.Length > MaxChunkBytes) throw new CommandException(ErrorCodes.Invalid, "bloco");
        if (offset != s.Written) throw new CommandException(ErrorCodes.Invalid, "ordem");
        if (s.Written + data.Length > s.Size) throw new CommandException(ErrorCodes.Invalid, "excede tamanho");
        s.Stream.Write(data);
        s.Hash.AppendData(data);
        s.Written += data.Length;
    }

    /// <summary>Confere tamanho e SHA-256, e só então move para a pasta final.</summary>
    public string FinishUpload(UploadSession s, byte[] expectedSha256)
    {
        if (s.Written != s.Size) throw new CommandException(ErrorCodes.Invalid, "incompleto");
        var actual = s.Hash.GetHashAndReset();
        if (expectedSha256.Length != 32 || !Crypto.FixedTimeEquals(actual, expectedSha256))
            throw new CommandException(ErrorCodes.Invalid, "hash");

        s.Stream.Flush(true);
        s.Stream.Dispose();
        var target = UniquePath(Path.Combine(_uploadDir, s.FinalName));
        File.Move(s.PartialPath, target);
        s.Finished = true;
        MarkFromInternet(target);
        var name = Path.GetFileName(target);
        Received?.Invoke(name);
        return name;
    }

    /// <summary>
    /// Marca o arquivo como vindo de fora (Mark of the Web). Assim o Windows
    /// avisa antes de abrir um executável recebido pelo celular.
    /// </summary>
    private static void MarkFromInternet(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string SanitizeName(string? name)
    {
        var raw = (name ?? "").Replace('\\', '/');
        raw = raw[(raw.LastIndexOf('/') + 1)..];
        var clean = new string(raw.Where(c => !char.IsControl(c) && Array.IndexOf(ForbiddenNameChars, c) < 0).ToArray())
            .Trim().TrimEnd('.', ' ').TrimStart('.');
        if (clean.Length == 0) clean = "arquivo";

        var ext = Path.GetExtension(clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        if (ext.Length > 16) { stem = clean; ext = ""; }
        if (ReservedNames.Contains(stem)) stem = "_" + stem;
        if (stem.Length > 100) stem = stem[..100];
        return stem + ext;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new CommandException(ErrorCodes.Failed, "nome");
    }
}
