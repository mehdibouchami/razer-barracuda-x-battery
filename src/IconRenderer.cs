using System;
using System.Collections.Generic;
using System.Drawing;

namespace BarracudaBattery
{
    /// <summary>
    /// Draws the tray icon text with a hand-made pixel font: at 16x16 a regular font blurs, while these glyphs
    /// stay crisp. "70%" is 15 px wide (5 px digits + a 3 px superscript "%"); larger icons (high DPI) scale the
    /// glyphs by an integer factor.
    /// </summary>
    static class IconRenderer
    {
        const int GlyphHeight = 9;

        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            { '0', new[] { ".###.", "##.##", "##.##", "##.##", "##.##", "##.##", "##.##", "##.##", ".###." } },
            { '1', new[] { ".##", "###", ".##", ".##", ".##", ".##", ".##", ".##", ".##" } },
            { '2', new[] { ".###.", "##.##", "...##", "...##", "..##.", ".##..", "##...", "##...", "#####" } },
            { '3', new[] { "####.", "...##", "...##", "...##", ".###.", "...##", "...##", "...##", "####." } },
            { '4', new[] { "##.##", "##.##", "##.##", "##.##", "#####", "...##", "...##", "...##", "...##" } },
            { '5', new[] { "#####", "##...", "##...", "####.", "...##", "...##", "...##", "##.##", ".###." } },
            { '6', new[] { ".###.", "##...", "##...", "####.", "##.##", "##.##", "##.##", "##.##", ".###." } },
            { '7', new[] { "#####", "...##", "...##", "..##.", "..##.", ".##..", ".##..", ".##..", ".##.." } },
            { '8', new[] { ".###.", "##.##", "##.##", "##.##", ".###.", "##.##", "##.##", "##.##", ".###." } },
            { '9', new[] { ".###.", "##.##", "##.##", "##.##", ".####", "...##", "...##", "...##", ".###." } },
            { '-', new[] { "...", "...", "...", "...", "###", "...", "...", "...", "..." } },
            // Superscript: occupies the top 5 rows only
            { '%', new[] { "#.#", "..#", ".#.", "#..", "#.#", "...", "...", "...", "..." } },
        };

        public static Bitmap Render(Size size, string text, Color color)
        {
            var bmp = new Bitmap(size.Width, size.Height);
            using (Graphics g = Graphics.FromImage(bmp))
                g.Clear(Color.Transparent);

            int width = 0;
            foreach (char c in text) width += Glyphs[c][0].Length;
            width += text.Length - 1; // 1 px between glyphs

            int scale = Math.Max(1, Math.Min(size.Width / Math.Max(width, 1), size.Height / GlyphHeight));
            int x = (size.Width - width * scale) / 2;
            int y = (size.Height - GlyphHeight * scale) / 2;

            using (Graphics g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            {
                foreach (char c in text)
                {
                    string[] rows = Glyphs[c];
                    for (int row = 0; row < rows.Length; row++)
                        for (int col = 0; col < rows[row].Length; col++)
                            if (rows[row][col] == '#')
                                g.FillRectangle(brush, x + col * scale, y + row * scale, scale, scale);
                    x += (rows[0].Length + 1) * scale;
                }
            }
            return bmp;
        }
    }
}
