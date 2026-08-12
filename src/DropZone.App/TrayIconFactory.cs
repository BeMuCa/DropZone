using System.Drawing;
using System.Drawing.Drawing2D;

namespace DropZone.App;

/// <summary>
/// Draws the tray icon at runtime so the app carries no binary asset. Two states:
/// active (filled) when receiving, idle (outline) when not.
/// </summary>
public static class TrayIconFactory
{
    public static Icon Create(bool active)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var accent = active ? Color.FromArgb(255, 88, 166, 255) : Color.FromArgb(255, 150, 150, 150);

            // Downward arrow into a tray line — "receive here".
            using var pen = new Pen(accent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 16, 5, 16, 19);
            g.DrawLine(pen, 9, 13, 16, 20);
            g.DrawLine(pen, 23, 13, 16, 20);

            if (active)
            {
                using var fill = new SolidBrush(accent);
                g.FillRectangle(fill, 7, 24, 18, 3);
            }
            else
            {
                g.DrawLine(pen, 7, 25, 25, 25);
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
