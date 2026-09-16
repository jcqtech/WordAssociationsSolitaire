using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace WordAssociationsSolitaire
{
    /// Rasterizes emoji (including ZWJ sequences and variation-selector emoji) to colored
    /// MonoGame textures using SkiaSharp + HarfBuzz with the system color emoji font, and
    /// caches them by emoji string. MonoGame's SpriteFont cannot render color emoji, so the
    /// game draws each emoji as a cached texture.
    ///
    /// Each cached glyph records the TIGHT opaque bounding box of the rasterized emoji so the
    /// renderer can draw exactly that region scaled-to-fit and centered — the emoji is never
    /// truncated and always sits dead-centre, regardless of the glyph's internal padding.
    public sealed class EmojiRenderer : IDisposable
    {
        /// A rasterized emoji: the texture plus the tight pixel rectangle the glyph occupies.
        public readonly struct Glyph
        {
            public readonly Texture2D Tex;
            public readonly Rectangle Src;
            public Glyph(Texture2D tex, Rectangle src) { Tex = tex; Src = src; }
            public bool Valid => Tex != null && Src.Width > 0 && Src.Height > 0;
        }

        // Rasterization resolution. Generous margin (size below) keeps even wide glyphs from
        // being clipped; the tight bbox is then scaled into the live card size at draw time.
        private const int Px = 128;

        private readonly GraphicsDevice _gd;
        private readonly Dictionary<string, Glyph> _cache = new();

        public EmojiRenderer(GraphicsDevice gd) => _gd = gd;

        /// Returns a cached glyph for the emoji (Valid == false for an empty string or a glyph
        /// that cannot be rendered).
        public Glyph Get(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return default;
            if (_cache.TryGetValue(emoji, out var g)) return g;
            g = Rasterize(emoji);
            _cache[emoji] = g; // cache failures too, so we don't retry a bad glyph every frame
            return g;
        }

        private Glyph Rasterize(string emoji)
        {
            try
            {
                using var bmp = Paint(emoji, Px);
                if (bmp == null) return default;
                Rectangle src = TryOpaqueBounds(bmp, out int x0, out int y0, out int x1, out int y1)
                    ? new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1)
                    : new Rectangle(0, 0, bmp.Width, bmp.Height);
                var tex = new Texture2D(_gd, bmp.Width, bmp.Height, false, SurfaceFormat.Color);
                tex.SetData(bmp.Bytes); // premultiplied RGBA, matching MonoGame's AlphaBlend
                return new Glyph(tex, src);
            }
            catch { return default; }
        }

        public void Dispose()
        {
            foreach (var g in _cache.Values) g.Tex?.Dispose();
            _cache.Clear();
        }

        // ---- shared SkiaSharp rasterization (also usable headlessly by the self-test) ----

        private static readonly SKTypeface Fallback =
            SKFontManager.Default.MatchFamily("Segoe UI Emoji")
            ?? SKFontManager.Default.MatchCharacter(0x1F600)
            ?? SKTypeface.Default;

        /// Paint the emoji onto a transparent premultiplied-RGBA bitmap of size px, with enough
        /// margin that the glyph is never clipped.
        private static SKBitmap Paint(string emoji, int px)
        {
            int cp = char.ConvertToUtf32(emoji, 0);
            var tf = SKFontManager.Default.MatchCharacter("Segoe UI Emoji", SKFontStyle.Normal, null, cp)
                     ?? Fallback;
            if (tf == null) return null;

            var bmp = new SKBitmap(px, px, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);

            float size = px * 0.72f; // leaves ~14% margin each side so wide glyphs aren't clipped
            using var paint = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = size };
            using var shaper = new SKShaper(tf);
            using var font = new SKFont(tf, size);
            var m = font.Metrics;
            float x = (px - size) / 2f;
            float baseline = px / 2f - (m.Ascent + m.Descent) / 2f;
            canvas.DrawShapedText(shaper, emoji, x, baseline, paint);
            return bmp;
        }

        /// Rasterize a plain TEXT string (e.g. a math symbol the game's ASCII SpriteFont lacks,
        /// like the infinity sign) to a WHITE-mask texture that can be tinted at draw time, using
        /// a semibold system face. Returns an invalid Glyph if the character isn't available.
        public static Glyph RasterizeMask(GraphicsDevice gd, string text)
        {
            try
            {
                const int px = 128;
                int cp = char.ConvertToUtf32(text, 0);
                var style = new SKFontStyle(600, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                var tf = SKFontManager.Default.MatchCharacter("Segoe UI", style, null, cp)
                         ?? SKFontManager.Default.MatchCharacter(cp)
                         ?? SKTypeface.Default;
                if (tf == null) return default;

                var bmp = new SKBitmap(px, px, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    float size = px * 0.8f;
                    using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Typeface = tf, TextSize = size };
                    var bounds = new SKRect();
                    paint.MeasureText(text, ref bounds);
                    var m = paint.FontMetrics;
                    float x = (px - bounds.Width) / 2f - bounds.Left;
                    float baseline = px / 2f - (m.Ascent + m.Descent) / 2f;
                    canvas.DrawText(text, x, baseline, paint);
                }

                Rectangle src = TryOpaqueBounds(bmp, out int x0, out int y0, out int x1, out int y1)
                    ? new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1)
                    : new Rectangle(0, 0, bmp.Width, bmp.Height);
                var tex = new Texture2D(gd, bmp.Width, bmp.Height, false, SurfaceFormat.Color);
                tex.SetData(bmp.Bytes);
                bmp.Dispose();
                return new Glyph(tex, src);
            }
            catch { return default; }
        }

        /// Tight bounding box of the bitmap's non-transparent pixels.
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

        /// Headless check (no GraphicsDevice) that the emoji rasterizes to a non-blank glyph —
        /// used by the self-test to flag any "tofu" / unsupported emoji in the word data.
        public static bool CanRender(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return false;
            try
            {
                using var bmp = Paint(emoji, 64);
                if (bmp == null) return false;
                int lit = 0;
                for (int x = 0; x < bmp.Width; x += 2)
                    for (int y = 0; y < bmp.Height; y += 2)
                        if (bmp.GetPixel(x, y).Alpha > 24 && ++lit > 10) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
