using Fecho.Core;
using QRCoder;

namespace Fecho.Agent;

/// <summary>
/// Janela com o QR code de pareamento. Fechar a janela cancela o token.
/// </summary>
public sealed class PairingForm : Form
{
    private readonly PairingManager _pairing;
    private readonly DateTimeOffset _expiresAt;
    private readonly Label _countdown;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _paired;

    public PairingForm(PairingManager pairing, string pairingUri, DateTimeOffset expiresAt, string address)
    {
        _pairing = pairing;
        _expiresAt = expiresAt;

        Text = "Parear iPhone — Fecho";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(420, 560);

        var title = new Label
        {
            Text = "Aponte a câmera do Fecho no iPhone",
            Font = new Font("Segoe UI Semibold", 14f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 56,
        };

        var picture = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(320, 320),
            Location = new Point(50, 64),
            Image = RenderQr(pairingUri),
        };

        var info = new Label
        {
            Text = "O código vale para um único iPhone e expira sozinho.\nNão compartilhe esta tela.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 396),
            Size = new Size(380, 48),
            ForeColor = Color.FromArgb(60, 60, 67),
        };

        _countdown = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 448),
            Size = new Size(380, 28),
            Font = new Font("Segoe UI Semibold", 11f),
        };

        var addressLabel = new Label
        {
            Text = "Endereço do PC: " + address,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 478),
            Size = new Size(380, 24),
            ForeColor = Color.FromArgb(110, 110, 115),
        };

        var cancel = new Button
        {
            Text = "Cancelar",
            Size = new Size(120, 36),
            Location = new Point(150, 512),
            DialogResult = DialogResult.Cancel,
        };
        CancelButton = cancel;

        Controls.AddRange(new Control[] { title, picture, info, _countdown, addressLabel, cancel });

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        UpdateCountdown();

        _pairing.Paired += OnPaired;
    }

    private void OnPaired(DeviceRecord device)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _paired = true;
            _timer.Stop();
            MessageBox.Show(this, $"{device.Name} foi pareado com este PC.", "Fecho",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        });
    }

    private void UpdateCountdown()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _timer.Stop();
            Close();
            return;
        }
        _countdown.Text = $"Expira em {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pairing.Paired -= OnPaired;
        _timer.Dispose();
        if (!_paired) _pairing.Cancel();
        base.OnFormClosed(e);
    }

    private static Image RenderQr(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        using var ms = new MemoryStream(png);
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }
}
