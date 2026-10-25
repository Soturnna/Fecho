using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using Fecho.Core;
using static Fecho.Agent.NativeMethods;

namespace Fecho.Agent;

/// <summary>
/// Captura a área de trabalho com GDI (BitBlt), desenha o cursor por cima,
/// reduz para a largura pedida e codifica em JPEG. Devolve null quando a
/// captura falha — é o que acontece na tela de bloqueio (área de trabalho
/// segura), então o streaming simplesmente pausa enquanto o PC está trancado.
/// </summary>
public sealed class GdiScreenSource : IScreenSource
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public ScreenFrame? Capture(int maxWidth, int quality)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return null;

        using var full = new Bitmap(vw, vh, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            try
            {
                g.CopyFromScreen(vx, vy, 0, 0, new Size(vw, vh), CopyPixelOperation.SourceCopy);
            }
            catch (Win32Exception)
            {
                return null; // área de trabalho segura (tela de bloqueio)
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            TryDrawCursor(g, vx, vy);
        }

        Bitmap toEncode = full;
        Bitmap? scaled = null;
        var w = vw;
        var h = vh;
        if (vw > maxWidth)
        {
            w = maxWidth;
            h = Math.Max(1, (int)Math.Round(vh * (double)maxWidth / vw));
            scaled = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using var g2 = Graphics.FromImage(scaled);
            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g2.DrawImage(full, new Rectangle(0, 0, w, h));
            toEncode = scaled;
        }

        try
        {
            using var ms = new MemoryStream();
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 20, 90));
            toEncode.Save(ms, JpegCodec, ep);
            return new ScreenFrame(ms.ToArray(), w, h);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>Desenha o ponteiro do mouse no quadro, para dar para mirar no toque.</summary>
    private static void TryDrawCursor(Graphics g, int vx, int vy)
    {
        var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero) return;

        var icon = CopyIcon(ci.hCursor);
        if (icon == IntPtr.Zero) return;
        try
        {
            var xHot = 0;
            var yHot = 0;
            if (GetIconInfo(icon, out var ii))
            {
                xHot = ii.xHotspot;
                yHot = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            var hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, ci.ptScreenPos.X - vx - xHot, ci.ptScreenPos.Y - vy - yHot, icon,
                    0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        catch (Exception)
        {
            // o cursor é só um extra; se falhar, o quadro vai sem ele
        }
        finally
        {
            DestroyIcon(icon);
        }
    }
}
