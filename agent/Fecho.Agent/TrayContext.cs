using Fecho.Core;
using Fecho.Server;
using Microsoft.Win32;

namespace Fecho.Agent;

/// <summary>
/// Ícone na bandeja do Windows. Monta todas as peças do agente e as liga.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly AgentConfig _config;
    private readonly DeviceRegistry _registry;
    private readonly PairingManager _pairing;
    private readonly AuditLog _audit;
    private readonly WindowsActions _actions;
    private readonly FileService _files;
    private readonly AgentServer _server;
    private readonly string _fingerprint;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _devicesMenu;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly Control _ui;
    private PairingForm? _pairingForm;

    public TrayContext()
    {
        AgentPaths.Ensure();
        _config = AgentConfig.LoadOrCreate();
        var cert = CertificateStore.LoadOrCreate(_config);
        _fingerprint = AgentCertificate.Fingerprint(cert);

        var clock = new SystemClock();
        _registry = new DeviceRegistry(new DpapiDeviceStorage());
        _pairing = new PairingManager(clock);
        _audit = new AuditLog(clock, new FileAuditSink());
        _actions = new WindowsActions();
        var auth = new Authenticator(_config.AgentId, _registry, _pairing, clock);
        _files = CreateFileService();
        var dispatcher = new CommandDispatcher(_actions, _audit, _files, clock);
        _server = new AgentServer(
            new AgentServerOptions { Port = _config.Port, Certificate = cert, AgentId = _config.AgentId },
            auth, dispatcher, _registry, _audit);

        // Controle invisível só para voltar à thread da interface.
        _ui = new Control();
        _ui.CreateControl();

        _devicesMenu = new ToolStripMenuItem("Aparelhos pareados");
        _devicesMenu.DropDownOpening += (_, _) => RebuildDevicesMenu();
        _devicesMenu.DropDownItems.Add(new ToolStripMenuItem("(carregando)") { Enabled = false });

        _autostartItem = new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = IsAutostartEnabled(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Parear iPhone…", null, (_, _) => ShowPairing()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_devicesMenu);
        menu.Items.Add(new ToolStripMenuItem("Atividade recente…", null, (_, _) => ShowActivity()));
        menu.Items.Add(new ToolStripMenuItem("Abrir arquivos recebidos", null, (_, _) => OpenUploads()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripMenuItem("Sair do Fecho", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Fecho — aguardando o iPhone",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowPairing();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        _files.Received += name => _ui.BeginInvoke(() =>
            _icon.ShowBalloonTip(4000, "Arquivo recebido do iPhone", name, ToolTipIcon.Info));

        try
        {
            _server.StartAsync().GetAwaiter().GetResult();
            _icon.Text = $"Fecho — ativo na porta {_config.Port}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não consegui abrir a porta {_config.Port}.\n\n{ex.Message}", "Fecho",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        if (_registry.List().Count == 0)
        {
            _icon.ShowBalloonTip(5000, "Fecho está rodando",
                "Clique com o botão direito no ícone e escolha Parear iPhone.", ToolTipIcon.Info);
        }
    }

    /// <summary>Só as pastas pessoais do usuário; nada de C:\ ou pastas do sistema.</summary>
    private static FileService CreateFileService()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        var roots = new Dictionary<string, (string, string)>
        {
            ["desktop"] = ("Área de Trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ["documents"] = ("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ["downloads"] = ("Downloads", downloads),
            ["pictures"] = ("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ["videos"] = ("Vídeos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            ["music"] = ("Música", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
        };
        return new FileService(roots, Path.Combine(downloads, "Fecho"));
    }

    private void OpenUploads()
    {
        Directory.CreateDirectory(_files.UploadDirectory);
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_files.UploadDirectory}\"")
        {
            UseShellExecute = false,
        });
    }

    private void ShowPairing()
    {
        if (_pairingForm is { IsDisposed: false })
        {
            _pairingForm.Activate();
            return;
        }

        var address = NetworkInfo.GetLanAddresses().FirstOrDefault();
        if (address is null)
        {
            MessageBox.Show("Este PC não está conectado a uma rede local.", "Fecho",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var offer = _pairing.Begin(TimeSpan.FromMinutes(2));
        var uri = PairingUri.Build(address.ToString(), _config.Port, _fingerprint, offer.Token,
            _config.AgentId, Environment.MachineName);
        _pairingForm = new PairingForm(_pairing, uri, offer.ExpiresAt, $"{address}:{_config.Port}");
        _pairingForm.Show();
    }

    private void RebuildDevicesMenu()
    {
        _devicesMenu.DropDownItems.Clear();
        var devices = _registry.List();
        if (devices.Count == 0)
        {
            _devicesMenu.DropDownItems.Add(new ToolStripMenuItem("Nenhum aparelho") { Enabled = false });
            return;
        }
        foreach (var d in devices)
        {
            var item = new ToolStripMenuItem($"{d.Name} — pareado em {d.PairedAt.LocalDateTime:dd/MM/yyyy}");
            item.DropDownItems.Add(new ToolStripMenuItem("Remover este aparelho", null, (_, _) => RemoveDevice(d)));
            _devicesMenu.DropDownItems.Add(item);
        }
    }

    private void RemoveDevice(DeviceRecord d)
    {
        var answer = MessageBox.Show($"Remover {d.Name}? Ele perde o acesso a este PC na hora.", "Fecho",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        _registry.Remove(d.Id);
        _audit.Record(d.Id, "revoke", true, d.Name);
    }

    private void ShowActivity()
    {
        var names = _registry.List().ToDictionary(d => d.Id, d => d.Name);
        var lines = _audit.All().Take(20).Select(e =>
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(e.Ts).LocalDateTime.ToString("dd/MM HH:mm:ss");
            var who = e.Device is not null && names.TryGetValue(e.Device, out var n) ? n : "desconhecido";
            return $"{when}  {(e.Ok ? "ok    " : "RECUSA")}  {e.Cmd,-14} {who}";
        });
        var text = string.Join(Environment.NewLine, lines);
        MessageBox.Show(text.Length == 0 ? "Nada registrado ainda." : text, "Fecho — atividade recente");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => null,
        };
        if (locked is null) return;
        _actions.IsLocked = locked.Value;
        _ = _server.BroadcastAsync("status", new { locked = locked.Value });
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Fecho") is string;
    }

    private void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostartEnabled())
        {
            key.DeleteValue("Fecho", throwOnMissingValue: false);
        }
        else if (Environment.ProcessPath is { } exe)
        {
            key.SetValue("Fecho", $"\"{exe}\"");
        }
        _autostartItem.Checked = IsAutostartEnabled();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _icon.Visible = false;
        _icon.Dispose();
        try { _server.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { }
        _ui.Dispose();
        base.ExitThreadCore();
    }
}
