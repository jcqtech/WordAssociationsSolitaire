using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WordAssociationsSolitaire
{
    /// A collapsible bottom-left toolbar. Collapsed it is a white circular "menu" button just
    /// right of the moves ribbon; tapped, its options (Undo, Hint, Help, Define) fan out on an
    /// animated arc. Undo/Hint mirror the keyboard shortcuts, Help opens a rules popup, and
    /// Define turns the pointer into a "?" so any card can be clicked to read its definition.
    public partial class Game1
    {
        // ---- state ----
        private bool _toolbarOpen;
        private float _toolbarT;      // eased 0 (collapsed) .. 1 (expanded)
        private bool _defineMode;     // pointer is a "?"; clicking a card shows its definition
        private bool _helpOpen;
        private float _helpT;         // help popup entrance 0..1
        private bool _defineActive;   // a definition popup is currently showing
        private float _defineT;       // definition popup entrance 0..1
        private string _defineWord = "";
        private string _defineText = "";
        private int _helpWrapWidth = -1;
        private float _helpWrapScale = -1f;
        private List<List<string>> _helpWrapped = new();
        private string _definitionWrapText = "";
        private int _definitionWrapWidth = -1;
        private float _definitionWrapScale = -1f;
        private List<string> _definitionWrapped = new();

        private static readonly string[] ToolNames = { "Undo", "Hint", "Help", "Define" };
        // Fluent UI System Icons (MIT-licensed, in Icons/) rendered as tinted white masks.
        private static readonly string[] ToolIconNames = { "arrow_undo", "lightbulb", "question_circle", "search" };
        private static readonly Color[] ToolIconColors =
        {
            new Color(74, 105, 140),   // Undo   — steel blue
            new Color(228, 162, 28),   // Hint   — amber (lightbulb)
            new Color(56, 158, 136),   // Help   — teal
            new Color(124, 100, 184),  // Define — violet
        };

        private static readonly (string head, string body)[] HelpItems =
        {
            ("Goal", "Clear every word set before you run out of moves."),
            ("Foundations", "Only a set's base word can start an empty slot at the top. Then stack the set's other words onto it. Fill a slot completely to clear that set."),
            ("Columns", "Move a card, or a run of same-set cards, onto another card of the same set, or onto an empty column. A base word may only go on an empty column."),
            ("The Deck", "Click the deck to turn over a card into the draw pile. When the deck runs out, click it to flip the pile back into the deck, face-down."),
            ("Moves", "Every move and every draw costs one move. Keep an eye on the counter to the left."),
            ("Toolbar", "Undo takes back your last move (Ctrl+Z). Hint flashes a legal move (H). Help opens this guide. Define turns your pointer into a ? so you can click any card to read what its word means."),
        };

        private void ResetToolbar()
        {
            _toolbarOpen = false; _toolbarT = 0f;
            _defineMode = false; _helpOpen = false; _helpT = 0f;
            _defineActive = false; _defineT = 0f;
            IsMouseVisible = true;
        }

        private void ExitDefineMode()
        {
            _defineMode = false;
            _defineActive = false;
            IsMouseVisible = true;
        }

        /// Escape handler: close whatever toolbar overlay is topmost. Returns false when nothing
        /// is open (so the caller can fall back to quitting the game).
        private bool CloseTopmostOverlay()
        {
            if (_settingsOpen) { CloseSettings(); return true; }
            if (_helpOpen) { _helpOpen = false; return true; }
            if (_defineActive) { _defineActive = false; return true; }
            if (_defineMode) { ExitDefineMode(); return true; }
            if (_toolbarOpen) { _toolbarOpen = false; return true; }
            return false;
        }

        // ---- geometry ----

        private int FabRadius => Sc(28);

        private Rectangle FabRect()
        {
            int r = FabRadius;
            int cx = Sc(150) + Sc(14) + r;            // just right of the moves ribbon
            int cy = Layout.ScreenH - Sc(60);
            return new Rectangle(cx - r, cy - r, 2 * r, 2 * r);
        }

        // The expanded menu fans out HORIZONTALLY to the right of the FAB as a row of uniform-width
        // labelled pills. Integrating each label into its pill (and spacing the pills) keeps text
        // from ever overlapping.
        private int OptPillH => Sc(42);
        private float OptLabelScale => 0.66f * S;
        private int OptIconBox => OptPillH - Sc(14);

        private int OptPillW()
        {
            int maxText = 0;
            foreach (var n in ToolNames)
                maxText = Math.Max(maxText, (int)Math.Ceiling(_font.MeasureString(n).X * OptLabelScale));
            return Sc(12) + OptIconBox + Sc(10) + maxText + Sc(16);
        }

        /// Raw 0..1 emergence progress for pill i (0 = tucked in the button, 1 = fully out),
        /// with a per-item stagger so they tumble out one after another.
        private float OptRaw(int i, float t) => Clamp01f(t * 1.35f - i * 0.10f);

        /// Position progress for pill i. On OPEN the pills spring out with a pronounced overshoot
        /// bounce; on CLOSE they retract with a smooth ease-in (no overshoot) so nothing shoots
        /// past the button as the menu collapses.
        private float OptExtend(int i, float t)
        {
            float raw = OptRaw(i, t);
            return _toolbarOpen ? EaseOutBack(raw, 3.4f) : EaseInCubic(raw);
        }

        /// The menu fans out horizontally when there's room; on a narrow window it fans vertically
        /// upward instead (a horizontal row along the bottom gets cramped when the window is narrow).
        private bool MenuVertical()
        {
            if (Layout.ScreenW < 920) return true; // narrow window
            var fab = FabRect();
            int w = OptPillW();
            int rightEdge = fab.Right + Sc(12) + (ToolNames.Length - 1) * (w + Sc(10)) + w;
            return rightEdge > Layout.WasteX - Sc(8);
        }

        /// Rectangle of option i for a given open-fraction t. Pills fan out horizontally (or
        /// vertically upward when the window is narrow) from behind the FAB, like shaking items
        /// out of a container.
        private Rectangle OptPillRect(int i, float t)
        {
            var fab = FabRect();
            int w = OptPillW(), h = OptPillH;
            float ext = OptExtend(i, t);
            if (MenuVertical())
            {
                int step = h + Sc(10);
                int openTop = fab.Y - Sc(12) - h - i * step;   // rise up
                int closedTop = fab.Center.Y - h / 2;
                int top = (int)Math.Round(Lerp(closedTop, openTop, ext));
                return new Rectangle(fab.X, top, w, h);
            }
            else
            {
                int step = w + Sc(10);
                int openLeft = fab.Right + Sc(12) + i * step;
                int top = fab.Center.Y - h / 2;
                int closedLeft = fab.Center.X - w / 2;
                int left = (int)Math.Round(Lerp(closedLeft, openLeft, ext));
                return new Rectangle(left, top, w, h);
            }
        }

        private static float EaseOutBack(float x, float overshoot = 1.70158f)
        {
            x = Clamp01f(x);
            float c1 = overshoot, c3 = c1 + 1f;
            float u = x - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        private static float EaseInCubic(float x) { x = Clamp01f(x); return x * x * x; }

        // ---- update ----

        private void UpdateToolbar(float dt)
        {
            // The toolbar and its modes only make sense during play; retract on win/lose.
            if (_board.State != GameState.Playing)
            {
                _toolbarOpen = false;
                _helpOpen = false;
                _defineActive = false;
                if (_defineMode) ExitDefineMode();
            }

            float k = Math.Min(1f, dt * 14f);
            _toolbarT += ((_toolbarOpen ? 1f : 0f) - _toolbarT) * k;
            _helpT += ((_helpOpen ? 1f : 0f) - _helpT) * k;
            _defineT += ((_defineActive ? 1f : 0f) - _defineT) * k;
            _settingsT += ((_settingsOpen ? 1f : 0f) - _settingsT) * k;
            if (Math.Abs((_toolbarOpen ? 1f : 0f) - _toolbarT) < 0.001f) _toolbarT = _toolbarOpen ? 1f : 0f;
            if (Math.Abs((_helpOpen ? 1f : 0f) - _helpT) < 0.001f) _helpT = _helpOpen ? 1f : 0f;
            if (Math.Abs((_defineActive ? 1f : 0f) - _defineT) < 0.001f) _defineT = _defineActive ? 1f : 0f;
            if (Math.Abs((_settingsOpen ? 1f : 0f) - _settingsT) < 0.001f) _settingsT = _settingsOpen ? 1f : 0f;

            IsMouseVisible = !_defineMode;
        }

        // ---- input ----

        /// Handle a press against the toolbar / its popups / define-mode. Returns true when the
        /// click was consumed (so board interaction is skipped).
        private bool HandleToolbarPress(Point pt)
        {
            if (_board.State != GameState.Playing) return false;

            if (FabRect().Contains(pt)) { _toolbarOpen = !_toolbarOpen; return true; }

            // Option buttons are interactive whenever they're visible. Clicking one RUNS it and
            // leaves the menu open — it only closes when the user taps the X (FAB) or presses Escape.
            if (_toolbarT > 0.02f)
                for (int i = 0; i < ToolNames.Length; i++)
                    if (OptPillRect(i, _toolbarT).Contains(pt))
                    {
                        if (_toolbarOpen) ActivateTool(i);
                        return true;
                    }

            if (_defineMode)
            {
                // Touch-friendly exit: tapping the "Done" button (or the instruction banner) leaves
                // define mode; otherwise a tap on a card shows its definition.
                var (banner, done) = DefineBannerLayout();
                if (done.Contains(pt) || banner.Contains(pt)) { ExitDefineMode(); return true; }
                TryDefineAt(pt);
                return true; // in define mode a click never moves cards
            }

            return false; // the click reaches the board; the open menu stays open
        }

        private void ActivateTool(int i)
        {
            // The menu stays open (the user closes it explicitly via the X / Escape).
            switch (i)
            {
                case 0: Undo(); break;
                case 1: ShowHint(); break;
                case 2: _helpOpen = true; break;
                case 3: if (_defineMode) ExitDefineMode(); else _defineMode = true; break;
            }
        }

        /// If a face-up card sits under the point, show its definition popup.
        private bool TryDefineAt(Point pt)
        {
            Card card = null;
            var (col, idx) = HitColumnCard(pt);
            if (col >= 0) card = _board.Columns[col][idx];
            else
            {
                int s = SlotAt(pt);
                if (s >= 0 && _board.Slots[s].Cards.Count > 0) card = _board.Slots[s].Cards[^1];
                else if (_board.Waste.Count > 0 && WasteRect().Contains(pt)) card = _board.Waste[^1];
            }
            if (card == null || !card.FaceUp) return false;

            _defineWord = Display(card.Word);
            _defineText = WordData.DefinitionFor(card.Word);
            if (string.IsNullOrWhiteSpace(_defineText)) _defineText = "No definition available.";
            _defineActive = true;
            _defineT = 0f;
            return true;
        }

        // ---- drawing ----

        private void DrawToolbar()
        {
            if (_board.State != GameState.Playing) return;

            DrawDefineBanner();

            if (_toolbarT > 0.001f)
                for (int i = ToolNames.Length - 1; i >= 0; i--)
                    DrawToolOption(i);

            DrawFab();
        }

        /// The define-mode banner: an instruction pill plus a tappable "Done" button, centred at
        /// the top. Computed (not stored from Draw) so hit-testing and drawing always agree.
        private (Rectangle banner, Rectangle done) DefineBannerLayout()
        {
            float sc = 0.58f * S;
            int bh = Sc(40);
            int bw = (int)(_font.MeasureString(DefineBannerText).X * sc) + Sc(30);
            int dw = (int)(_font.MeasureString("Done").X * sc) + Sc(44);
            int gap = Sc(12);
            int totalW = bw + gap + dw;
            int x = Layout.ScreenW / 2 - totalW / 2;
            int y = Sc(16);
            return (new Rectangle(x, y, bw, bh), new Rectangle(x + bw + gap, y, dw, bh));
        }

        private const string DefineBannerText = "Tap a card for its meaning  (right-click to exit)";

        /// True when the define banner is wide enough (or the window narrow enough) that it lacks
        /// comfortable breathing room from the top-left "Solved" pill or the top-right "New Game"
        /// button — the cue to lay a masking band behind the banner so they don't visually collide.
        /// Uses raw screen-pixel clearance (a perceptual gap) so it behaves consistently once the
        /// card scale clamps on very narrow windows and the banner stops shrinking.
        private bool BannerCrowded(Rectangle banner, Rectangle done)
        {
            string solved = $"Solved  {_board.ClearedCategories}/{_board.TotalCategories}";
            int solvedRight = Sc(24) + (int)(_font.MeasureString(solved).X * (0.72f * S)) + Sc(28);
            const int clearance = 84; // screen px of breathing room wanted on each side
            return (banner.X - solvedRight) < clearance
                || (_newGameButton.Left - done.Right) < clearance;
        }

        /// A hint banner while the define pointer is active — the OS cursor is hidden and replaced
        /// by a drawn "?", so this explains how to use and (tap Done / right-click to) leave the mode.
        private void DrawDefineBanner()
        {
            if (!_defineMode) return;
            var (banner, done) = DefineBannerLayout();
            float sc = 0.58f * S;

            // On a narrow window the centred banner + Done button crowd into the top-left "Solved"
            // pill and the top-right "New Game" button. Lay a translucent black band across the top
            // FIRST (it's drawn after the HUD) so it masks those elements, leaving the instructions
            // and the Done button reading cleanly against it instead of overlapping the HUD.
            if (BannerCrowded(banner, done))
            {
                int bandBottom = Math.Max(banner.Bottom, _newGameButton.Bottom) + Sc(8);
                FillRect(new Rectangle(0, 0, Layout.ScreenW, bandBottom), new Color(6, 10, 16, 200));
            }

            DrawRoundShadow(banner);
            DrawRoundFill(banner, new Color(30, 40, 54, 240));
            DrawRoundRing(banner, new Color(228, 196, 96, 190));
            DrawTextFit(DefineBannerText, banner.Center.X, banner.Center.Y, banner.Width - Sc(18), new Color(240, 244, 248), sc);

            // A glossy yellow "Done" button, matching the New Game button styling.
            bool hover = done.Contains(_mouse.ToPoint());
            DrawRoundShadow(done);
            DrawRoundFill(done, hover ? ButtonYellowHi : ButtonYellow);
            DrawRoundFill(new Rectangle(done.X + Sc(4), done.Center.Y, done.Width - Sc(8), done.Height / 2 - Sc(4)), new Color(150, 96, 8, 40));
            DrawRoundFill(new Rectangle(done.X + Sc(5), done.Y + Sc(4), done.Width - Sc(10), done.Height / 2 - Sc(2)), new Color(255, 255, 255, 82));
            DrawRoundRing(done, ButtonRing);
            DrawRoundRing(new Rectangle(done.X + Sc(2), done.Y + Sc(2), done.Width - Sc(4), done.Height - Sc(4)), new Color(255, 255, 255, 78));
            DrawTextFit("Done", done.Center.X, done.Center.Y, done.Width - Sc(16), ButtonInk, 0.6f * S);
        }

        private void DrawFab()
        {
            var r = FabRect();
            bool hover = r.Contains(_mouse.ToPoint());
            DrawDiscShadow(r);
            DrawDisc(r, hover ? Color.White : new Color(247, 247, 250));
            DrawDiscRing(r, new Color(150, 152, 160, 130));

            var c = new Vector2(r.Center.X, r.Center.Y);
            float t = _toolbarT;
            var ink = new Color(70, 82, 98);
            float len = r.Width * 0.42f;
            float thick = Math.Max(2f, Sc(3.2f));
            float gap = r.Height * 0.17f;

            // Morph the hamburger into an X (t: 0 = menu, 1 = X): the top and bottom bars slide to
            // the centre and rotate to become the X's two diagonals, while the middle bar fades out.
            float topAng = Lerp(0f, MathHelper.PiOver4, t);
            float botAng = Lerp(0f, -MathHelper.PiOver4, t);
            float topY = Lerp(-gap, 0f, t);
            float botY = Lerp(gap, 0f, t);
            float xlen = Lerp(len, len * 1.18f, t);       // the X's arms reach a touch further
            DrawIconBar(c + new Vector2(0, topY), xlen, thick, topAng, ink, 1f);
            DrawIconBar(c, len, thick, 0f, ink, 1f - t); // middle bar fades as the X forms
            DrawIconBar(c + new Vector2(0, botY), xlen, thick, botAng, ink, 1f);
        }

        private void DrawToolOption(int i)
        {
            // Opacity follows the emergence progress (not raw t), so on collapse the pill AND its
            // shadow fade out well before reaching the button — no dark smudge from piled shadows.
            float appear = Clamp01f(OptRaw(i, _toolbarT) * 2.5f);
            if (appear <= 0.01f) return;

            var rect = OptPillRect(i, _toolbarT);
            bool hover = _toolbarOpen && rect.Contains(_mouse.ToPoint());

            DrawRoundShadow(rect, appear);
            DrawRoundFill(rect, Cov(hover ? new Color(255, 249, 224) : Color.White, appear));
            DrawRoundRing(rect, Cov(new Color(150, 152, 160, 150), appear));

            // Fluent icon at the left, tinted with its per-tool colour.
            int box = OptIconBox;
            float iconCx = rect.X + Sc(12) + box / 2f;
            var g = _icons?.Get(ToolIconNames[i]) ?? default;
            DrawGlyph(g, iconCx, rect.Center.Y, box * 0.94f, ToolIconColors[i], appear);

            // Label, left-aligned after the icon and vertically centred (no shadow on the white pill).
            float sc = OptLabelScale;
            int textX = rect.X + Sc(12) + box + Sc(10);
            float th = _font.MeasureString(ToolNames[i]).Y * sc;
            var lpos = new Vector2(textX, (float)Math.Round(rect.Center.Y - th / 2f));
            _spriteBatch.DrawString(_font, ToolNames[i], lpos, new Color(58, 68, 82) * appear,
                                    0f, Vector2.Zero, sc, SpriteEffects.None, 0f);
        }

        private void DrawDefineCursor()
        {
            if (!_defineMode) return;
            var m = _mouse;
            int r = Sc(15);
            var rect = new Rectangle((int)m.X - r, (int)m.Y - r, 2 * r, 2 * r);
            DrawDiscShadow(rect);
            DrawDisc(rect, new Color(44, 52, 66, 230));
            DrawDiscRing(rect, new Color(255, 255, 255, 235));
            DrawTextFit("?", (int)m.X, (int)m.Y - Sc(1), 2 * r - Sc(6), Color.White, 0.9f * S);
        }

        // ---- popups ----

        private void DrawHelpPopup()
        {
            if (_helpT <= 0.001f) return;
            float e = EaseOutCubic(Clamp01f(_helpT));
            FillRect(new Rectangle(0, 0, Layout.ScreenW, Layout.ScreenH), new Color(0, 0, 0, (int)(150 * e)));

            int pw = Math.Min(Sc(620), Layout.ScreenW - Sc(40));
            int pad = Sc(26);
            int innerW = pw - 2 * pad;
            float titleScale = 1.15f * S * (22f / 48f);
            float headScale = 0.72f * S;
            float bodyScale = 0.62f * S;
            float lineH = _font.MeasureString("Ag").Y;
            float titleH = _titleFont.MeasureString("How to Play").Y * titleScale;

            var wrapped = WrappedHelpLines(innerW - Sc(2), bodyScale);
            float contentH = titleH + Sc(16);
            for (int i = 0; i < HelpItems.Length; i++)
            {
                var lines = wrapped[i];
                contentH += lineH * headScale + Sc(3) + lines.Count * (lineH * bodyScale) + Sc(12);
            }
            float footH = lineH * 0.5f * S + Sc(26); // extra breathing room above the close hint
            int ph = Math.Min((int)(pad * 2 + contentH + footH), Layout.ScreenH - Sc(24));
            int rise = (int)((1f - e) * Sc(48));
            var panel = new Rectangle(Layout.ScreenW / 2 - pw / 2, Layout.ScreenH / 2 - ph / 2 + rise, pw, ph);

            if (e > 0.1f) DrawRoundShadow(panel);
            DrawRoundFill(panel, new Color(26, 42, 34, (int)(248 * e)));
            DrawRoundRing(panel, new Color(120, 182, 142, (int)(205 * e)));

            int cx = panel.Center.X;
            int ty = panel.Y + pad;
            DrawTextCentered("How to Play", cx, ty, titleScale, Cov(new Color(255, 236, 150), e), true, _titleFont);
            ty += (int)titleH + Sc(16);

            int lx = panel.X + pad;
            for (int i = 0; i < HelpItems.Length; i++)
            {
                DrawTextLeft(HelpItems[i].head, lx, ty, Cov(new Color(255, 220, 130), e), headScale);
                ty += (int)(lineH * headScale) + Sc(3);
                foreach (var ln in wrapped[i])
                {
                    DrawTextLeft(ln, lx, ty, Cov(new Color(230, 235, 229), e), bodyScale);
                    ty += (int)(lineH * bodyScale);
                }
                ty += Sc(12);
            }
            DrawTextCentered("Click anywhere to close", cx, panel.Bottom - pad - (int)(lineH * 0.5f * S),
                             0.5f * S, Cov(new Color(255, 255, 255, 165), e), false);
        }

        private void DrawDefinePopup()
        {
            if (_defineT <= 0.001f) return;
            float e = EaseOutCubic(Clamp01f(_defineT));
            FillRect(new Rectangle(0, 0, Layout.ScreenW, Layout.ScreenH), new Color(0, 0, 0, (int)(120 * e)));

            int pw = Math.Min(Sc(470), Layout.ScreenW - Sc(40));
            int pad = Sc(24);
            int innerW = pw - 2 * pad;
            float titleScale = 1.0f * S * (22f / 48f);
            float bodyScale = 0.72f * S;
            float lineH = _font.MeasureString("Ag").Y;
            float titleH = _titleFont.MeasureString(_defineWord).Y * titleScale;
            var lines = WrappedDefinitionLines(_defineText, innerW, bodyScale);

            int ph = (int)(pad * 2 + titleH + Sc(16) + lines.Count * (lineH * bodyScale) + Sc(44));
            int rise = (int)((1f - e) * Sc(40));
            var panel = new Rectangle(Layout.ScreenW / 2 - pw / 2, Layout.ScreenH / 2 - ph / 2 + rise, pw, ph);

            if (e > 0.1f) DrawRoundShadow(panel);
            DrawRoundFill(panel, new Color(26, 42, 34, (int)(248 * e)));
            DrawRoundRing(panel, new Color(228, 196, 96, (int)(210 * e)));

            int cx = panel.Center.X;
            int ty = panel.Y + pad;
            DrawTextCentered(_defineWord, cx, ty, titleScale, Cov(new Color(255, 232, 140), e), true, _titleFont);
            ty += (int)titleH + Sc(16);
            foreach (var ln in lines)
            {
                DrawTextCentered(ln, cx, ty, bodyScale, Cov(new Color(232, 236, 230), e), false);
                ty += (int)(lineH * bodyScale);
            }
            DrawTextCentered("Click anywhere to close", cx, panel.Bottom - pad - (int)(lineH * 0.5f * S),
                             0.5f * S, Cov(new Color(255, 255, 255, 160), e), false);
        }

        // ---- small drawing helpers ----

        private void DrawDisc(Rectangle r, Color c) => _spriteBatch.Draw(_disc, r, Premult(c));
        private void DrawDiscRing(Rectangle r, Color c) => _spriteBatch.Draw(_discRing, r, Premult(c));

        private void DrawDiscShadow(Rectangle r)
        {
            int pad = Sc(4);
            var d = new Rectangle(r.X - pad, r.Y - pad + Sc(3), r.Width + 2 * pad, r.Height + 2 * pad);
            _spriteBatch.Draw(_disc, d, Premult(new Color(0, 0, 0, 55)));
        }

        /// A thin rounded-ish bar (a stretched pixel) centred at `center`, rotated by `angle`.
        private void DrawIconBar(Vector2 center, float length, float thick, float angle, Color c, float alpha)
        {
            alpha = Clamp01f(alpha);
            if (alpha <= 0.01f) return;
            var col = Premult(new Color(c.R, c.G, c.B, (byte)(255 * alpha)));
            _spriteBatch.Draw(_pixel, center, null, col, angle, new Vector2(0.5f, 0.5f),
                              new Vector2(length, thick), SpriteEffects.None, 0f);
        }

        private void DrawTextLeft(string text, int x, int topY, Color color, float scale)
        {
            var pos = new Vector2(x, (float)Math.Round((double)topY));
            _spriteBatch.DrawString(_font, text, pos + new Vector2(1, 1), new Color(0, 0, 0, 110),
                                    0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        private List<List<string>> WrappedHelpLines(int maxWidth, float scale)
        {
            if (_helpWrapWidth == maxWidth && Math.Abs(_helpWrapScale - scale) < 0.0001f)
                return _helpWrapped;

            _helpWrapWidth = maxWidth;
            _helpWrapScale = scale;
            _helpWrapped = new List<List<string>>(HelpItems.Length);
            foreach (var (_, body) in HelpItems)
                _helpWrapped.Add(WrapText(body, maxWidth, scale));
            return _helpWrapped;
        }

        private List<string> WrappedDefinitionLines(string text, int maxWidth, float scale)
        {
            if (_definitionWrapText == text && _definitionWrapWidth == maxWidth
                && Math.Abs(_definitionWrapScale - scale) < 0.0001f)
                return _definitionWrapped;

            _definitionWrapText = text;
            _definitionWrapWidth = maxWidth;
            _definitionWrapScale = scale;
            _definitionWrapped = WrapText(text, maxWidth, scale);
            return _definitionWrapped;
        }

        /// Greedy word-wrap of `text` to `maxWidth` pixels at the given font scale.
        private List<string> WrapText(string text, float maxWidth, float scale)
        {
            var result = new List<string>();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cur = "";
            foreach (var w in words)
            {
                var test = cur.Length == 0 ? w : cur + " " + w;
                if (cur.Length > 0 && _font.MeasureString(test).X * scale > maxWidth)
                {
                    result.Add(cur);
                    cur = w;
                }
                else cur = test;
            }
            if (cur.Length > 0) result.Add(cur);
            return result;
        }
    }
}
