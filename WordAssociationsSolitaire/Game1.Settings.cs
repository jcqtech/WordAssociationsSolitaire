using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace WordAssociationsSolitaire
{
    /// The Settings popup (gear button just left of New Game). Lets the player set the move
    /// budget, turn the move limiter off entirely, and adjust the sound-effect volume. Move
    /// settings live on Config.Active (so they carry into the next deal) and are also applied
    /// live to the current board; volume drives SoundEffect.MasterVolume via ApplyVolume().
    public partial class Game1
    {
        private bool _settingsOpen;
        private float _settingsT;          // entrance fade 0..1
        private bool _draggingVolume;      // the volume handle is being dragged
        private Rectangle _settingsButton;  // gear button, positioned in SyncLayout

        private const int MoveLimitMin = 160;
        private const int MoveLimitMax = 500;
        private const int MoveLimitStep = 20;

        // ---- palette (raised controls share the New Game "top-gloss / bottom-shade" depth) ----
        // Silvery metallic dimensional buttons: the HUD gear and the panel's Close button.
        private static readonly Color Pearl = new(210, 215, 223);
        private static readonly Color PearlHi = new(228, 232, 238);
        private static readonly Color PearlRing = new(142, 149, 162);
        private static readonly Color PearlInk = new(70, 80, 96);
        private static readonly Color PearlLower = new(64, 74, 90, 44);
        // Warm gold steppers (the − / + move-limit buttons) — the same gold as New Game.
        private static readonly Color StepGold = new(250, 206, 78);
        private static readonly Color StepGoldHi = new(255, 224, 120);
        private static readonly Color StepGoldRing = new(196, 146, 36);
        private static readonly Color StepInk = new(96, 66, 12);
        private static readonly Color StepOff = new(150, 156, 150);
        private static readonly Color StepOffRing = new(112, 120, 112);
        // Recessed value field.
        private static readonly Color FieldBg = new(18, 32, 25);
        private static readonly Color FieldRing = new(64, 90, 74);
        private static readonly Color FieldInk = new(236, 242, 232);
        // Green used for the "on" toggle track and the filled slider level.
        private static readonly Color OnGreen = new(88, 190, 126);
        private static readonly Color OnGreenEdge = new(56, 150, 96);
        private static readonly Color OffTrack = new(74, 90, 80);
        private static readonly Color OffTrackEdge = new(52, 66, 58);
        private static readonly Color Groove = new(46, 62, 53);
        private static readonly Color GrooveEdge = new(30, 44, 37);
        private static readonly Color Knob = new(246, 244, 238);
        private static readonly Color KnobRing = new(150, 156, 164);

        private void OpenSettings()
        {
            _settingsOpen = true;
            _settingsT = 0f;
            _hasSel = false;
            PlaySound("open-menu");
        }

        private void CloseSettings()
        {
            _settingsOpen = false;
            _draggingVolume = false;
            SaveUserSettings();
            PlaySound("close-menu");
        }

        /// Restore persisted preferences into Config.Active + the sound volume (called once at
        /// startup, before the first deal, so the initial game already honours them).
        private void LoadUserSettings()
        {
            var s = UserSettings.Load();
            Config.Active.Moves = Math.Clamp(s.MoveLimit, MoveLimitMin, MoveLimitMax);
            Config.Active.UnlimitedMoves = s.UnlimitedMoves;
            _soundVolume = Clamp01f(s.SoundVolume);
        }

        private void SaveUserSettings() => UserSettings.Save(new UserSettings
        {
            MoveLimit = Config.Active.Moves,
            UnlimitedMoves = Config.Active.UnlimitedMoves,
            SoundVolume = _soundVolume,
        });

        // ---- geometry ----

        /// The panel and every interactive sub-rectangle, computed from the current layout so
        /// hit-testing and drawing always agree.
        private (Rectangle panel, Rectangle minus, Rectangle value, Rectangle plus,
                 Rectangle toggle, Rectangle track, Rectangle close) SettingsLayout()
        {
            int pad = Sc(28);
            int pw = Math.Min(Sc(560), Layout.ScreenW - Sc(40));
            float titleScale = 1.1f * S * (22f / 48f);
            int titleH = (int)(_titleFont.MeasureString("Settings").Y * titleScale);
            int rowH = Sc(62);
            int closeH = Sc(46);
            int contentH = titleH + Sc(22) + rowH * 3 + Sc(14) + closeH;
            int ph = Math.Min(pad * 2 + contentH, Layout.ScreenH - Sc(24));
            var panel = new Rectangle(Layout.ScreenW / 2 - pw / 2, Layout.ScreenH / 2 - ph / 2, pw, ph);

            int contentTop = panel.Y + pad + titleH + Sc(22);
            int r1 = contentTop, r2 = r1 + rowH, r3 = r2 + rowH;

            // Move-limit cluster: [−] value [+], right-aligned.
            int btn = Sc(38), gap = Sc(12), valW = Sc(78), valH = Sc(38);
            int clusterW = btn + gap + valW + gap + btn;
            int cx0 = panel.Right - pad - clusterW;
            int m1 = r1 + rowH / 2;
            var minus = new Rectangle(cx0, m1 - btn / 2, btn, btn);
            var value = new Rectangle(minus.Right + gap, m1 - valH / 2, valW, valH);
            var plus = new Rectangle(value.Right + gap, m1 - btn / 2, btn, btn);

            // Unlimited toggle switch (right-aligned).
            int togW = Sc(62), togH = Sc(32);
            int m2 = r2 + rowH / 2;
            var toggle = new Rectangle(panel.Right - pad - togW, m2 - togH / 2, togW, togH);

            // Volume slider with a fixed percentage readout reserved on its right.
            int pctReserve = Sc(54), trkH = Sc(7), trkW = Sc(176);
            int m3 = r3 + rowH / 2;
            var track = new Rectangle(panel.Right - pad - pctReserve - trkW, m3 - trkH / 2, trkW, trkH);

            int cwid = Sc(148);
            var close = new Rectangle(panel.Center.X - cwid / 2, panel.Bottom - pad - closeH, cwid, closeH);

            return (panel, minus, value, plus, toggle, track, close);
        }

        /// Generous hit area around the thin volume track so it's easy to grab.
        private static Rectangle Inflate(Rectangle r, int dx, int dy)
            => new Rectangle(r.X - dx, r.Y - dy, r.Width + 2 * dx, r.Height + 2 * dy);

        // ---- input ----

        private void HandleSettingsPress(Point pt)
        {
            var L = SettingsLayout();
            if (!L.panel.Contains(pt)) { CloseSettings(); return; } // click outside closes
            if (L.close.Contains(pt)) { CloseSettings(); return; }

            if (L.toggle.Contains(pt))
            {
                Config.Active.UnlimitedMoves = !Config.Active.UnlimitedMoves;
                ApplyMoveSettingsToBoard();
                SaveUserSettings();
                PlaySound("highlight");
                return;
            }

            if (!Config.Active.UnlimitedMoves)
            {
                if (L.minus.Contains(pt)) { AdjustMoveLimit(-MoveLimitStep); return; }
                if (L.plus.Contains(pt)) { AdjustMoveLimit(+MoveLimitStep); return; }
            }

            if (Inflate(L.track, Sc(12), Sc(16)).Contains(pt))
            {
                _draggingVolume = true;
                SetVolumeFromX(_mouse.X, L.track);
                return;
            }
            // a click elsewhere inside the panel does nothing (keeps the popup open)
        }

        private void AdjustMoveLimit(int delta)
        {
            Config.Active.Moves = Math.Clamp(Config.Active.Moves + delta, MoveLimitMin, MoveLimitMax);
            ApplyMoveSettingsToBoard();
            SaveUserSettings();
            PlaySound("highlight");
        }

        private void SetVolumeFromX(float x, Rectangle track)
        {
            _soundVolume = Clamp01f((x - track.X) / Math.Max(1f, track.Width));
            ApplyVolume();
        }

        /// Push the current move-limit / unlimited settings onto the LIVE board. Moves already
        /// spent are preserved when limited; enabling unlimited forgives them (and hides the
        /// counter). Called whenever the player changes a move setting.
        private void ApplyMoveSettingsToBoard()
        {
            if (_board == null) return;
            int used = Math.Max(0, _board.InitialMoves - _board.MovesLeft);
            _board.Unlimited = Config.Active.UnlimitedMoves;
            _board.InitialMoves = Config.Active.Moves;
            _board.MovesLeft = _board.Unlimited
                ? Config.Active.Moves
                : Math.Max(0, Config.Active.Moves - used);
            _board.UpdateState();
        }

        /// Continuous volume dragging (called each frame from Update while the button is held).
        private void UpdateSettingsDrag(bool active, MouseState ms)
        {
            if (!_settingsOpen || !active || ms.LeftButton == ButtonState.Released)
            {
                if (_draggingVolume) { _draggingVolume = false; SaveUserSettings(); } // persist final level
                return;
            }
            if (_draggingVolume) SetVolumeFromX(_mouse.X, SettingsLayout().track);
        }

        // ---- drawing ----

        /// A horizontal pill (fully rounded ends) filled with `c`: circle end-caps + a middle bar.
        private void DrawPillFill(Rectangle r, Color c)
        {
            int h = r.Height;
            if (r.Width <= h) { DrawDisc(r, c); return; }
            DrawDisc(new Rectangle(r.X, r.Y, h, h), c);
            DrawDisc(new Rectangle(r.Right - h, r.Y, h, h), c);
            FillRect(new Rectangle(r.X + h / 2, r.Y, r.Width - h, h), c);
        }

        /// The neutral silvery-gray raised button (gear + Close), using the shared dimensional style.
        private void DrawPearlButton(Rectangle r, bool hover, float e)
            => DrawDimensionalButton(r, hover, Pearl, PearlHi, PearlRing, PearlLower, 82, 76, e);

        private void DrawSettingsButton()
        {
            var btn = _settingsButton;
            bool hover = btn.Contains(_mouse.ToPoint());
            DrawPearlButton(btn, hover, 1f);
            var g = _icons?.Get("settings") ?? default;
            DrawGlyph(g, btn.Center.X, btn.Center.Y, btn.Height * 0.52f, PearlInk, 1f);
        }

        private void DrawSettingsPopup()
        {
            if (_settingsT <= 0.001f) return;
            float e = EaseOutCubic(Clamp01f(_settingsT));
            FillRect(new Rectangle(0, 0, Layout.ScreenW, Layout.ScreenH), new Color(0, 0, 0, (int)(150 * e)));

            var L = SettingsLayout();
            var panel = L.panel;
            if (e > 0.1f) DrawRoundShadow(panel);
            DrawRoundFill(panel, new Color(26, 42, 34, (int)(248 * e)));
            DrawRoundRing(panel, new Color(120, 182, 142, (int)(205 * e)));

            int cx = panel.Center.X;
            float titleScale = 1.1f * S * (22f / 48f);
            DrawTextCentered("Settings", cx, panel.Y + Sc(26), titleScale, Cov(new Color(255, 236, 150), e), true, _titleFont);

            bool unlim = Config.Active.UnlimitedMoves;
            int lx = panel.X + Sc(28);
            Color labelCol = Cov(new Color(232, 237, 230), e);
            SettingsLabel("Move limit", lx, L.minus.Center.Y, labelCol);
            SettingsLabel("Unlimited moves", lx, L.toggle.Center.Y, labelCol);
            SettingsLabel("Sound volume", lx, L.track.Center.Y, labelCol);

            // Move-limit stepper: (−) [value] (+)  — the gold buttons grey out while unlimited.
            DrawStepper(L.minus, false, e, !unlim);
            DrawStepper(L.plus, true, e, !unlim);
            DrawRoundFill(L.value, Fade(FieldBg, e));
            DrawRoundRing(L.value, Fade(FieldRing, e));
            Color valCol = unlim ? Cov(new Color(120, 140, 128), e) : Cov(FieldInk, e);
            DrawTextFit(Config.Active.Moves.ToString(), L.value.Center.X, L.value.Center.Y, L.value.Width - Sc(14), valCol, 0.72f * S);

            DrawToggle(L.toggle, unlim, e);

            int pctCx = L.track.Right + Sc(27);
            DrawVolumeSlider(L.track, pctCx, e);

            // Close button (neutral pearl, matching the gear).
            var cl = L.close;
            bool clh = cl.Contains(_mouse.ToPoint());
            DrawPearlButton(cl, clh, e);
            DrawTextFit("Close", cl.Center.X, cl.Center.Y, cl.Width - Sc(16), Cov(PearlInk, e), 0.62f * S);
        }

        private void SettingsLabel(string text, int x, int midY, Color color)
        {
            float sc = 0.64f * S;
            float th = _font.MeasureString(text).Y * sc;
            DrawTextLeft(text, x, (int)Math.Round(midY - th / 2f), color, sc);
        }

        /// A gold, glossy square stepper (the shared dimensional style, matching New Game) with a
        /// perfectly-centred plus/minus drawn from crisp bars.
        private void DrawStepper(Rectangle r, bool isPlus, float e, bool enabled)
        {
            bool hover = enabled && r.Contains(_mouse.ToPoint());
            if (enabled)
                DrawDimensionalButton(r, hover, StepGold, StepGoldHi, StepGoldRing, NewGameLower, 82, 78, e);
            else
            {
                DrawRoundShadow(r, e);
                DrawRoundFill(r, Fade(StepOff, e));
                DrawRoundRing(r, Fade(StepOffRing, e));
            }
            var c = new Vector2(r.Center.X, r.Center.Y);
            float len = r.Width * 0.38f;
            float thick = Math.Max(2f, Sc(3.2f));
            Color ink = enabled ? StepInk : new Color(90, 98, 92);
            DrawIconBar(c, len, thick, 0f, ink, e);              // horizontal stroke
            if (isPlus) DrawIconBar(c, thick, len, 0f, ink, e);  // vertical stroke -> plus
        }

        /// An iOS-style pill switch: coloured track (green when on), a cream knob with a soft
        /// shadow that slides to the lit side.
        private void DrawToggle(Rectangle tog, bool on, float e)
        {
            int bt = Math.Max(1, Sc(2));
            DrawPillFill(tog, Fade(on ? OnGreenEdge : OffTrackEdge, e));
            DrawPillFill(Inflate(tog, -bt, -bt), Fade(on ? OnGreen : OffTrack, e));

            int kd = tog.Height - Sc(8);
            int kx = on ? tog.Right - Sc(4) - kd : tog.X + Sc(4);
            var knob = new Rectangle(kx, tog.Y + Sc(4), kd, kd);
            DrawDiscShadow(knob);
            DrawDisc(knob, Fade(Knob, e));
            DrawDiscRing(knob, Fade(KnobRing, e));
        }

        private void DrawVolumeSlider(Rectangle trk, int pctCx, float e)
        {
            int bt = Math.Max(1, Sc(2));
            DrawPillFill(trk, Fade(GrooveEdge, e));
            DrawPillFill(Inflate(trk, -bt, -bt), Fade(Groove, e));

            float vol = Clamp01f(_soundVolume);
            int fillW = (int)Math.Round(trk.Width * vol);
            if (fillW > bt * 2)
                DrawPillFill(Inflate(new Rectangle(trk.X, trk.Y, Math.Min(trk.Width, fillW), trk.Height), -bt, -bt), Fade(OnGreen, e));

            int hx = trk.X + fillW;
            int hd = trk.Height + Sc(12);
            var handle = new Rectangle(hx - hd / 2, trk.Center.Y - hd / 2, hd, hd);
            DrawDiscShadow(handle);
            DrawDisc(handle, Fade(Knob, e));
            DrawDiscRing(handle, Fade(KnobRing, e));

            string pct = $"{(int)Math.Round(vol * 100)}%";
            DrawTextFit(pct, pctCx, trk.Center.Y, Sc(50), Cov(new Color(232, 237, 230), e), 0.52f * S);
        }
    }
}
