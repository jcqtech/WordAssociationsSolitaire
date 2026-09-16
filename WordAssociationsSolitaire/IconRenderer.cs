using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;

namespace WordAssociationsSolitaire
{
    /// Rasterizes simple single-&lt;path&gt; SVG icons (the MIT-licensed Fluent UI System Icons
    /// shipped in the Icons/ folder) to premultiplied WHITE-mask MonoGame textures via SkiaSharp,
    /// so each can be tinted any colour at draw time. Records the tight opaque bounds of each glyph
    /// so it can be drawn centred and scaled-to-fit, exactly like the emoji glyphs.
    public sealed class IconRenderer : IDisposable
    {
        private const int Px = 128;

        private readonly GraphicsDevice _gd;
        private readonly string _dir;
        private readonly Dictionary<string, EmojiRenderer.Glyph> _cache = new();

        public IconRenderer(GraphicsDevice gd, string dir) { _gd = gd; _dir = dir; }

        /// Cached white-mask glyph for the icon file &lt;name&gt;.svg (Valid == false if missing/bad).
        public EmojiRenderer.Glyph Get(string name)
        {
            if (_cache.TryGetValue(name, out var g)) return g;
            g = Load(name);
            _cache[name] = g; // cache failures too
            return g;
        }

        private EmojiRenderer.Glyph Load(string name)
        {
            try
            {
                string path = Path.Combine(_dir, name + ".svg");
                if (!File.Exists(path)) return default;
                string svg = File.ReadAllText(path);

                var dm = Regex.Match(svg, "\\sd=\"([^\"]+)\"");
                if (!dm.Success) return default;

                float vb = 24f;
                var vm = Regex.Match(svg, "viewBox=\"([\\d.\\s-]+)\"");
                if (vm.Success)
                {
                    var p = vm.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 4 && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
                        vb = w;
                }

                using var skpath = SKPath.ParseSvgPathData(dm.Groups[1].Value);
                if (skpath == null) return default;

                float pad = Px * 0.06f;
                float scale = (Px - 2 * pad) / vb;
                var bmp = new SKBitmap(Px, Px, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.Translate(pad, pad);
                    canvas.Scale(scale);
                    using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
                    canvas.DrawPath(skpath, paint); // painted white -> the tint mask
                }

                Rectangle src = TryOpaqueBounds(bmp, out int x0, out int y0, out int x1, out int y1)
                    ? new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1)
                    : new Rectangle(0, 0, bmp.Width, bmp.Height);
                var tex = new Texture2D(_gd, bmp.Width, bmp.Height, false, SurfaceFormat.Color);
                tex.SetData(bmp.Bytes); // premultiplied RGBA, matching MonoGame's AlphaBlend
                bmp.Dispose();
                return new EmojiRenderer.Glyph(tex, src);
            }
            catch { return default; }
        }

        private static bool TryOpaqueBounds(SKBitmap bmp, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = bmp.Width; minY = bmp.Height; maxX = -1; maxY = -1;
            var bytes = bmp.Bytes;
            int w = bmp.Width, h = bmp.Height, stride = bmp.RowBytes;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (bytes[row + x * 4 + 3] > 20)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX >= 0;
        }

        public void Dispose()
        {
            foreach (var g in _cache.Values) g.Tex?.Dispose();
            _cache.Clear();
        }
    }
}
