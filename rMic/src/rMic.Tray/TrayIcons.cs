using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace rMic.Tray;

public enum MicIconState
{
    /// <summary>Every microphone is live.</summary>
    On,

    /// <summary>Some microphones are disabled and some are still live.</summary>
    Mixed,

    /// <summary>Every microphone is disabled.</summary>
    Off,

    /// <summary>The service could not be reached, so the real state is unknown.</summary>
    Unknown
}

/// <summary>
/// Tray icons are drawn at runtime rather than shipped as .ico resources, so the set stays in
/// sync and scales cleanly. Colour carries the state, which reads correctly on both light and
/// dark taskbars.
/// </summary>
public sealed class TrayIconSet : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly List<IntPtr> _handles = new();
    private readonly Dictionary<MicIconState, Icon> _icons = new();

    public TrayIconSet(int size = 32)
    {
        foreach (var state in Enum.GetValues<MicIconState>())
            _icons[state] = Build(state, size);
    }

    public Icon this[MicIconState state] => _icons[state];

    private Icon Build(MicIconState state, int size)
    {
        var color = state switch
        {
            MicIconState.On => Color.FromArgb(0x2E, 0xA0, 0x43),
            MicIconState.Mixed => Color.FromArgb(0xE0, 0x8B, 0x1E),
            MicIconState.Off => Color.FromArgb(0xD1, 0x34, 0x38),
            _ => Color.FromArgb(0x8A, 0x8A, 0x8A)
        };

        // Unknown gets no slash: a struck-through icon would assert the microphone is off,
        // which is exactly the thing we could not determine.
        var struckThrough = state is MicIconState.Mixed or MicIconState.Off;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var s = size / 32f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 2.6f * s)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            // Capsule (the mic body).
            using (var body = RoundedRect(new RectangleF(11.5f * s, 3.5f * s, 9f * s, 15f * s), 4.5f * s))
                g.FillPath(brush, body);

            // Cradle, stem and base.
            g.DrawArc(pen, new RectangleF(6.5f * s, 8.5f * s, 19f * s, 15f * s), 20f, 140f);
            g.DrawLine(pen, 16f * s, 23.5f * s, 16f * s, 27f * s);
            g.DrawLine(pen, 11f * s, 28f * s, 21f * s, 28f * s);

            if (struckThrough)
            {
                var from = new PointF(5f * s, 27.5f * s);
                var to = new PointF(27f * s, 4.5f * s);

                // Punch a transparent gutter so the slash stays legible over the glyph.
                g.SmoothingMode = SmoothingMode.None;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var eraser = new Pen(Color.Transparent, 5.6f * s)
                       {
                           StartCap = LineCap.Round,
                           EndCap = LineCap.Round
                       })
                {
                    g.DrawLine(eraser, from, to);
                }

                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var slash = new Pen(color, 3f * s)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawLine(slash, from, to);
            }
        }

        var handle = bitmap.GetHicon();
        _handles.Add(handle);
        return Icon.FromHandle(handle);
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
            icon.Dispose();
        _icons.Clear();

        // Icon.FromHandle does not own the handle, so release them here.
        foreach (var handle in _handles)
            DestroyIcon(handle);
        _handles.Clear();
    }
}
