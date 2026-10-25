using System.Text.Json.Serialization;

namespace Fecho.Core;

public sealed class StatusInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("unlockAvailable")] public bool UnlockAvailable { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class AppVolume
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("volume")] public int Volume { get; set; }
    [JsonPropertyName("muted")] public bool Muted { get; set; }
}

public sealed class VolumeState
{
    [JsonPropertyName("volume")] public int Volume { get; set; }
    [JsonPropertyName("muted")] public bool Muted { get; set; }
    [JsonPropertyName("apps")] public List<AppVolume> Apps { get; set; } = new();
}

public enum MediaAction { PlayPause, Next, Previous }

/// <summary>Um quadro da tela do PC, já em JPEG.</summary>
public sealed record ScreenFrame(byte[] Jpeg, int Width, int Height);

/// <summary>
/// Captura a tela do PC. A implementação real é GDI no Windows; devolve null
/// quando não consegue (PC bloqueado / tela segura).
/// </summary>
public interface IScreenSource
{
    ScreenFrame? Capture(int maxWidth, int quality);
}
public enum MouseButton { Left, Right }
public enum UnlockOutcome { Done, NotInstalled, NotLocked, Failed }

/// <summary>
/// Tudo que o agente sabe fazer no sistema operacional. A implementação real
/// fica no projeto do Windows; os testes usam uma versão falsa.
/// </summary>
public interface IPlatformActions
{
    StatusInfo GetStatus();
    void Lock();
    void Sleep();
    void Restart();
    void Shutdown();
    UnlockOutcome Unlock();

    VolumeState GetVolume();
    void SetMasterVolume(int percent);
    void SetMute(bool muted);
    void SetAppVolume(string id, int percent);
    void Media(MediaAction action);

    void MouseMove(int dx, int dy);
    void MouseClick(MouseButton button);
    void MouseScroll(int amount);
    /// <summary>Move o cursor para uma posição absoluta (x,y de 0 a 1 na tela toda).</summary>
    void MouseMoveTo(double x, double y);
    void MouseClickAt(double x, double y, MouseButton button);
    void MouseButtonDown(double x, double y, MouseButton button);
    void MouseButtonUp(double x, double y, MouseButton button);
    void Key(string key, IReadOnlyList<string> modifiers);
    void Text(string text);

    string? GetClipboardText();
    void SetClipboardText(string text);
}
