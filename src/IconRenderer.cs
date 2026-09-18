using System.Drawing;
using System.Drawing.Text;

namespace BarracudaBattery
{
    /// <summary>Draws the tray icon: the battery level as large as fits, in the level's color.</summary>
    static class IconRenderer
    {
        public static Bitmap Render(Size size, string text, Color color)
        {
            var bmp = new Bitmap(size.Width, size.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                StringFormat fmt = StringFormat.GenericTypographic;

                // Pick the largest font that fits the icon
                float em = size.Height;
                Font font = null;
                SizeF measured;
                do
                {
                    if (font != null) font.Dispose();
                    font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                    measured = g.MeasureString(text, font, PointF.Empty, fmt);
                    em -= 0.5f;
                } while ((measured.Width > size.Width || measured.Height > size.Height) && em > 4);

                using (font)
                using (var brush = new SolidBrush(color))
                    g.DrawString(text, font, brush,
                        (size.Width - measured.Width) / 2, (size.Height - measured.Height) / 2, fmt);
            }
            return bmp;
        }
    }
}
