using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace WordAssociationsSolitaire
{
    public partial class Game1 : Game
    {
        private readonly GraphicsDeviceManager _graphics;
        private SpriteBatch _spriteBatch;
        private SpriteFont _font;
        private SpriteFont _titleFont;   // high-res font for the big end-overlay title (crisp at large scale)
        private EmojiRenderer _emoji;
        private IconRenderer _icons;     // Fluent SVG icons for the toolbar options

        // Textures (procedurally generated in LoadContent).
        private Texture2D _pixel, _star, _bg;
        private Texture2D _cardFill, _cardRingThin, _cardRingGold, _cardShadow, _cardBack;
        private Texture2D _round, _roundRing, _roundShadow;
        private Texture2D _ribbonFill, _ribbonRing, _refresh;
        private Texture2D _disc, _discRing;   // filled circle + ring, for the toolbar buttons
        private EmojiRenderer.Glyph _infinityGlyph; // rasterized "∞" mask for the unlimited-moves ribbon
        private bool _resizing;          // guards re-entrancy from ApplyChanges
        private bool _ticking;           // guards re-entrancy of the live-resize Tick()

        private GameBoard _board;
        private readonly Random _rng = new();

        /// Optional preset board for dev demos / visual tests (see Program.cs --demo).
        internal static GameBoard StartupBoard;

        private MouseState _prevMouse;
        private KeyboardState _prevKeyboard;

        private enum DragKind { None, Column, Waste }
        private DragKind _dragKind = DragKind.None;
        private int _dragCol;
        private int _dragIdx;
        private List<Card> _dragCards = new();
        private Vector2 _grabOffset;
        private Vector2 _mouse;

        private Rectangle _newGameButton;

        // Win scoring: time the game and record moves-remaining + duration into the high-score table.
        private readonly System.Diagnostics.Stopwatch _gameTimer = new();
        private bool _scoreRecorded;
        private int _scoreRank = -1;               // this run's row in the top-5 (or -1)
        private int _winMovesLeft;                 // captured at the moment of winning
        private double _winSeconds;
        private System.Collections.Generic.List<ScoreEntry> _topScores = new();

        // Geometry constants for the rounded look.
        private const int CardRadius = 12;
        private const int ShadowPad = 7;
        private const int ShadowDrop = 4;
        private const int RoundMargin = 14;
        private const int RoundShadowMargin = 24;
        private const float RibbonNotchFrac = 0.09f; // shallow forked top of the moves ribbon / height

        // Scale helpers: every fixed pixel offset / font size in the draw code below is
        // authored for the baseline 1280x820 / 96x128 layout, then multiplied by the live
        // Layout.Scale so the whole board (HUD, badges, words, shadows) grows or shrinks
        // coherently with the window instead of being uniformly stretched.
        private static float S => Layout.Scale;
        private static int Sc(float v) => (int)Math.Round(v * Layout.Scale);

        // Palette
        private static readonly Color FeltCenter = new(48, 142, 90);
        private static readonly Color FeltEdge = new(17, 72, 50);
        private static readonly Color CardCream = new(250, 249, 243);
        private static readonly Color Ink = new(72, 72, 78); // soft dark gray, not pure black
        private static readonly Color Gold = new(210, 166, 50);
        private static readonly Color GoldStar = new(244, 200, 72);
        private static readonly Color EdgeGray = new(198, 198, 200);

        public Game1()
        {
            _graphics = new GraphicsDeviceManager(this);
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            _graphics.PreferredBackBufferWidth = Layout.BaseW;
            _graphics.PreferredBackBufferHeight = Layout.BaseH;
        }

        protected override void Initialize()
        {
            Window.Title = "Word Associations Solitaire";
            Window.AllowUserResizing = true;
            Window.ClientSizeChanged += OnClientSizeChanged;
            Layout.Update(Layout.BaseW, Layout.BaseH);
            LoadUserSettings();   // restore saved move budget / unlimited / volume before the first deal
            _board = StartupBoard ?? WordData.NewGame(_rng);
            _gameTimer.Restart();
            _topScores = ScoreBoard.Load();
            base.Initialize();
            HookLiveResize();
        }

        // Smallest the window may be resized to. Below this the cards would have to shrink past a
        // comfortable size, so we stop the shrink by clamping the window instead.
        private const int MinWindowW = 400;
        private const int MinWindowH = 560;

        /// MonoGame's WindowsDX backend SUPPRESSES Window.ClientSizeChanged for the whole
        /// duration of a border drag — it only fires once, on mouse release — so during the
        /// drag the swap chain keeps its old size and Windows stretches the last frame to
        /// fill the growing window (the "squish"). Hook the underlying WinForms Form.Resize
        /// event instead: it fires continuously throughout the drag, letting us resize the
        /// back buffer and repaint the reflowed board live, on every mouse movement.
        private void HookLiveResize()
        {
            if (System.Windows.Forms.Control.FromHandle(Window.Handle) is System.Windows.Forms.Form form)
            {
                form.MinimumSize = new System.Drawing.Size(MinWindowW, MinWindowH); // stop cards shrinking further
                form.Resize += (s, e) =>
                {
                    var size = ((System.Windows.Forms.Form)s).ClientSize;
                    ResizeToWindow(size.Width, size.Height);
                };
            }
        }

        private void OnClientSizeChanged(object sender, EventArgs e)
            => ResizeToWindow(Window.ClientBounds.Width, Window.ClientBounds.Height);

        /// Match the back buffer to the window and immediately Tick() so the responsive
        /// layout reflows and repaints in the same beat — including inside the OS modal
        /// resize loop, which otherwise blocks the normal Run()/Update/Draw cadence.
        private void ResizeToWindow(int w, int h)
        {
            if (_resizing || w <= 0 || h <= 0) return;
            _resizing = true;
            _graphics.PreferredBackBufferWidth = w;
            _graphics.PreferredBackBufferHeight = h;
            _graphics.ApplyChanges();
            _resizing = false;

            if (_spriteBatch != null && !_ticking)
            {
                _ticking = true;
                try { Tick(); }
                finally { _ticking = false; }
            }
        }

        protected override void LoadContent()
        {
            _spriteBatch = new SpriteBatch(GraphicsDevice);
            _font = Content.Load<SpriteFont>("Fonts/GameFont");
            _titleFont = Content.Load<SpriteFont>("Fonts/GameFontLarge");
            _emoji = new EmojiRenderer(GraphicsDevice);
            _infinityGlyph = EmojiRenderer.RasterizeMask(GraphicsDevice, "\u221E"); // ∞ for unlimited moves
            _icons = new IconRenderer(GraphicsDevice, System.IO.Path.Combine(AppContext.BaseDirectory, "Icons"));
            LoadSounds();
            ApplyVolume();  // start at the default (50%) sound-effect volume
            PrewarmEmojis(_board);

            _pixel = new Texture2D(GraphicsDevice, 1, 1);
            _pixel.SetData(new[] { Color.White });
            _star = CreateStarTexture(24, GoldStar);

            // Card / background textures are generated once at the baseline size and then
            // drawn scaled into the live (responsive) rectangles. Text, stars and badges
            // are drawn at logical resolution each frame so they stay crisp at any size.
            _bg = MakeBackground(Layout.BaseW, Layout.BaseH);

            _cardFill = MakeRoundedFill(Layout.BaseCardW, Layout.BaseCardH, CardRadius, Color.White);
            _cardRingThin = MakeRoundedRing(Layout.BaseCardW, Layout.BaseCardH, CardRadius, 2.0f, Color.White);
            _cardRingGold = MakeRoundedRing(Layout.BaseCardW, Layout.BaseCardH, CardRadius, 3.4f, Color.White);
            _cardShadow = MakeShadow(Layout.BaseCardW, Layout.BaseCardH, CardRadius, ShadowPad, ShadowPad + 1f, 0.30f);
            _cardBack = MakeCardBack(Layout.BaseCardW, Layout.BaseCardH, CardRadius);

            _round = MakeRoundedFill(40, 40, RoundMargin, Color.White);
            _roundRing = MakeRoundedRing(40, 40, RoundMargin, 2.2f, Color.White);
            _roundShadow = MakeShadow(40, 40, RoundMargin, 10, 11f, 0.32f);

            // Vertical hanging "Moves Left" ribbon: a tall banner with a shallow forked
            // top, drawn so its lower tail bleeds off the bottom edge of the window.
            _ribbonFill = MakeBannerFill(120, 300, RibbonNotchFrac, 2f, Color.White);
            _ribbonRing = MakeBannerRing(120, 300, RibbonNotchFrac, 2f, 4f, Color.White);
            _refresh = MakeRefreshIcon(48, Color.White);
            _spark = MakeSpark(32);

            // Circle textures for the toolbar FAB and its fan-out option buttons.
            _disc = MakeRoundedFill(64, 64, 32, Color.White);
            _discRing = MakeRoundedRing(64, 64, 32, 2.4f, Color.White);
            foreach (var n in ToolIconNames) _icons?.Get(n); // prewarm toolbar icons
            _icons?.Get("settings");                          // prewarm the gear icon

            SyncLayout();
            if (StartupBoard == null) StartDealAnim(); // deal the opening board (skip preset demo boards)
        }

        // ----------------------------------------------------------------- update

        protected override void Update(GameTime gameTime)
        {
            SyncLayout();

            if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed)
                Exit();

            var ms = Mouse.GetState();
            _mouse = new Vector2(ms.X, ms.Y);

            // Only the FOREGROUND window may drive input. MonoGame's Mouse/Keyboard state is
            // global, so without this gate a click-and-drag would still move cards while the
            // game is unfocused or hidden behind another window.
            bool active = IsActive;
            bool mouseMoved = ms.X != _prevMouse.X || ms.Y != _prevMouse.Y;
            bool pressed = active && ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
            bool released = active && ms.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
            bool rightPressed = active && ms.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;

            // Losing focus mid-drag drops the held cards back where they started (the board is
            // only mutated on release, so nothing else needs undoing).
            if (!active && _dragKind != DragKind.None) CancelDrag();

            if (pressed) HandlePress(_mouse.ToPoint());
            else if (released) HandleRelease(_mouse.ToPoint());
            if (rightPressed && _defineMode) ExitDefineMode(); // right-click leaves the define cursor
            UpdateSettingsDrag(active, ms); // continuous volume-handle dragging

            var ks = Keyboard.GetState();
            bool ctrl = ks.IsKeyDown(Keys.LeftControl) || ks.IsKeyDown(Keys.RightControl);
            bool modal = _helpOpen || _defineActive; // don't mutate the board behind a popup
            if (active)
            {
                if (!modal && ctrl && ks.IsKeyDown(Keys.Z) && !_prevKeyboard.IsKeyDown(Keys.Z)) Undo();
                if (!modal && ks.IsKeyDown(Keys.H) && !_prevKeyboard.IsKeyDown(Keys.H)) ShowHint();
                // Escape peels back one layer: close the topmost popup / open menu, otherwise de-select
                // a keyboard-selected card. It never quits the game (use the window's close button).
                if (ks.IsKeyDown(Keys.Escape) && !_prevKeyboard.IsKeyDown(Keys.Escape) && !CloseTopmostOverlay())
                {
                    if (_hasSel) { _hasSel = false; Speak("Selection cleared.", true, true); }
                }
                UpdateAccessibilityInput(ks);   // Tab / arrows / Enter / Space / n / d
            }
            _prevKeyboard = ks;

            UpdateAnimations((float)gameTime.ElapsedGameTime.TotalSeconds);
            UpdateToolbar((float)gameTime.ElapsedGameTime.TotalSeconds);
            UpdateHoverNarration(active && mouseMoved);
            UpdateAccessibilityAnnouncements();

            // Record the score the instant the game is won (time stops, moves-left frozen).
            if (_board.State == GameState.Won && !_scoreRecorded)
            {
                _scoreRecorded = true;
                _gameTimer.Stop();
                _winMovesLeft = _board.MovesLeft;
                _winSeconds = _gameTimer.Elapsed.TotalSeconds;
                _topScores = ScoreBoard.Record(_winMovesLeft, _winSeconds, out _scoreRank);
            }

            _prevMouse = ms;
            base.Update(gameTime);
        }

        /// Abandon an in-progress drag without moving anything (used when the window loses
        /// focus). The dragged cards were never removed from the board, so clearing the drag
        /// state simply makes them reappear where they were picked up.
        private void CancelDrag()
        {
            _dragKind = DragKind.None;
            _dragCards = new List<Card>();
        }

        private void HandlePress(Point pt)
        {
            _hintActive = false; // any click clears a lingering hint highlight
            _kbActive = false;   // mouse takes over: hide the keyboard focus ring
            _hasSel = false;     // and drop any keyboard move-source selection

            // The Settings popup is modal: while it's open every click is routed to it.
            if (_settingsOpen) { HandleSettingsPress(pt); return; }

            // Modal popups (Help, a definition) swallow the entire click first — even over the
            // New Game button — so dismissing one can never accidentally reset the game.
            if (_helpOpen) { _helpOpen = false; return; }
            if (_defineActive) { _defineActive = false; return; }

            if (_settingsButton.Contains(pt)) { OpenSettings(); return; }
            if (_newGameButton.Contains(pt)) { NewGame(); return; }

            // The toolbar (FAB, its options, and define-mode card clicks) takes priority over
            // board interaction.
            if (HandleToolbarPress(pt)) return;

            // The first click during the win celebration dismisses the fireworks and shows the popup.
            if (_board.State == GameState.Won && !_winPopupShown) { _winPopupShown = true; return; }
            // On the lose screen, the Undo button takes back moves so play can resume.
            if (_board.State == GameState.Lost && _undoStack.Count > 0 && UndoButtonRect(LosePanelRect()).Contains(pt))
            {
                Undo();
                return;
            }
            if (_board.State != GameState.Playing) return;
            if (Dealing) return; // ignore board clicks until the opening deal finishes

            if (StockRect().Contains(pt))
            {
                bool recycling = _board.Stock.Count == 0 && _board.Waste.Count > 0;
                var oldWaste = recycling ? new List<Card>(_board.Waste) : null;
                var wastePos = recycling ? CaptureWasteFanPositions() : null;
                var snap = _board.Clone();
                if (_board.Draw())
                {
                    PushUndo(snap);
                    if (recycling) { StartRecycleAnim(oldWaste, wastePos); PlaySound("shuffling"); Speak("Recycled the deck.", true, true); }
                    else { StartDrawFlip(); StartWasteShuffle(); PlaySound("take-card"); Speak($"Card drawn: {Display(_board.Waste[^1].Word)}.", true, true); }
                }
                return;
            }

            if (_board.Waste.Count > 0 && WasteRect().Contains(pt))
            {
                _dragKind = DragKind.Waste;
                _dragCards = new List<Card> { _board.Waste[^1] };
                _grabOffset = _mouse - new Vector2(Layout.WasteX, Layout.WasteY);
                PlaySound("take-card");
                return;
            }

            var (col, idx) = HitColumnCard(pt);
            if (col >= 0 && _board.Columns[col][idx].FaceUp && _board.IsValidTail(col, idx))
            {
                _dragKind = DragKind.Column;
                _dragCol = col;
                _dragIdx = idx;
                _dragCards = _board.Columns[col].GetRange(idx, _board.Columns[col].Count - idx);
                int[] ys = ColumnCardYs(col);
                _grabOffset = _mouse - new Vector2(Layout.ColX(col), ys[idx]);
                PlaySound("take-card");
            }
        }

        private void HandleRelease(Point pt)
        {
            if (_dragKind == DragKind.None) return;

            var moving = _dragCards;
            var origin = _mouse - _grabOffset;
            int srcCol = _dragCol;
            int srcIdx = _dragIdx;
            var srcKind = _dragKind;

            // Clear the drag immediately so the freshly-updated board geometry (used
            // to compute landing positions) is no longer affected by the drag.
            _dragKind = DragKind.None;
            _dragCards = new List<Card>();

            var snap = _board.Clone();

            int slot = SlotAt(pt);
            if (slot >= 0)
            {
                bool ok = srcKind == DragKind.Column
                    ? _board.MoveColumnTailToSlot(srcCol, srcIdx, slot)
                    : _board.MoveWasteToSlot(slot);
                if (ok)
                {
                    PushUndo(snap);
                    // A successful waste placement removes the top card for good, and the
                    // remaining fan cards are already at their resting positions. Snap the
                    // shift to 0 (instead of easing it back) so the fan holds still — easing
                    // caused a leftover inward-collapse-then-fan-out shuffle.
                    if (srcKind == DragKind.Waste) _wasteShift = 0f;
                    if (_board.LastCompletion != null)
                    {
                        StartSlotCompletion(_board.LastCompletion);
                        _board.LastCompletion = null;
                        PlaySound("success");
                    }
                    else
                    {
                        StartSlideCollapse(moving, origin, new Vector2(Layout.SlotX(slot), Layout.SlotY(slot)), toSlot: true);
                        PlaySound("place-card");
                    }
                    return;
                }
            }

            int toCol = ColumnAt(pt);
            if (toCol >= 0)
            {
                bool ok = srcKind == DragKind.Column
                    ? _board.MoveColumnTailToColumn(srcCol, srcIdx, toCol)
                    : _board.MoveWasteToColumn(toCol);
                if (ok)
                {
                    PushUndo(snap);
                    if (srcKind == DragKind.Waste) _wasteShift = 0f; // fan already settled; don't shuffle back
                    StartSlideToColumn(moving, origin, toCol);
                    PlaySound("place-card");
                    return;
                }
            }

            // Rejected drop: gently slide the cards back to where they came from.
            PlaySound("deny");
            if (srcKind == DragKind.Column)
                StartSlideToColumn(moving, origin, srcCol);
            else
                StartSlideCollapse(moving, origin, new Vector2(Layout.WasteX, Layout.WasteY), toSlot: false);
        }

        private void NewGame()
        {
            _board = WordData.NewGame(_rng);
            PrewarmEmojis(_board);
            _dragKind = DragKind.None;
            _dragCards = new List<Card>();
            ResetAnimations();
            ResetInteractions();
            ResetToolbar();
            _gameTimer.Restart();
            _scoreRecorded = false;
            _scoreRank = -1;
            StartDealAnim(); // deal the fresh columns out of the deck
        }

        /// Rasterize (and cache) every EMOJI-card's emoji on the board up front so the first
        /// frame that shows a card never hitches while a glyph is generated.
        private void PrewarmEmojis(GameBoard b)
        {
            if (_emoji == null || b == null) return;
            foreach (var col in b.Columns)
                foreach (var c in col) if (c.ShowEmoji) _emoji.Get(c.Emoji);
            foreach (var s in b.Slots)
                foreach (var c in s.Cards) if (c.ShowEmoji) _emoji.Get(c.Emoji);
            foreach (var c in b.Stock) if (c.ShowEmoji) _emoji.Get(c.Emoji);
            foreach (var c in b.Waste) if (c.ShowEmoji) _emoji.Get(c.Emoji);
        }

        protected override void UnloadContent()
        {
            _emoji?.Dispose();
            _emoji = null;
            base.UnloadContent();
        }

        // ------------------------------------------------------------- geometry

        private static Rectangle StockRect() => new(Layout.StockX, Layout.StockY, Layout.CardW, Layout.CardH);
        private static Rectangle WasteRect() => new(Layout.WasteX, Layout.WasteY, Layout.CardW, Layout.CardH);
        private static Rectangle SlotRect(int i) => new(Layout.SlotX(i), Layout.SlotY(i), Layout.CardW, Layout.CardH);

        /// Column top Y including the responsive "scoot": in two-row mode the bottom
        /// row eases downward (in a left-to-right cascade) when a top-row column grows
        /// tall enough to crowd it, preserving a healthy margin between the two rows.
        private int ColTop(int col)
        {
            int y = Layout.ColTopY(col);
            if (col >= 0 && col < _colScoot.Length) y += (int)Math.Round(_colScoot[col]);
            return y;
        }

        /// Pixel height of a column's fanned stack (top of the first card to the bottom
        /// of the last), honouring each card's face-up / face-down peek offset.
        private int ColStackExtent(int col)
        {
            var c = _board.Columns[col];
            int h = Layout.CardH;
            for (int i = 0; i < c.Count - 1; i++)
                h += c[i].FaceUp ? Layout.FaceUpOffset : Layout.FaceDownOffset;
            return h;
        }

        private int[] ColumnCardYs(int col)
        {
            var c = _board.Columns[col];
            var ys = new int[c.Count];
            int y = ColTop(col);
            for (int i = 0; i < c.Count; i++)
            {
                ys[i] = y;
                y += c[i].FaceUp ? Layout.FaceUpOffset : Layout.FaceDownOffset;
            }
            return ys;
        }

        private (int col, int idx) HitColumnCard(Point pt)
        {
            for (int col = 0; col < _board.Columns.Count; col++)
            {
                int x = Layout.ColX(col);
                if (pt.X < x || pt.X > x + Layout.CardW) continue;
                var c = _board.Columns[col];
                if (c.Count == 0) continue;
                int[] ys = ColumnCardYs(col);
                for (int i = 0; i < c.Count; i++)
                {
                    int top = ys[i];
                    int h = (i < c.Count - 1) ? ys[i + 1] - ys[i] : Layout.CardH;
                    if (pt.Y >= top && pt.Y < top + h) return (col, i);
                }
            }
            return (-1, -1);
        }

        private int SlotAt(Point pt)
        {
            for (int i = 0; i < _board.Slots.Count; i++)
                if (SlotRect(i).Contains(pt)) return i;
            return -1;
        }

        private int ColumnAt(Point pt)
        {
            for (int col = 0; col < _board.Columns.Count; col++)
            {
                int x = Layout.ColX(col);
                if (pt.X < x || pt.X > x + Layout.CardW) continue;
                var c = _board.Columns[col];
                int bottom = ColTop(col) + Layout.CardH;
                if (c.Count > 0)
                {
                    int[] ys = ColumnCardYs(col);
                    bottom = ys[c.Count - 1] + Layout.CardH;
                }
                if (pt.Y >= ColTop(col) && pt.Y <= bottom + Sc(30)) return col;
            }
            return -1;
        }

        // -------------------------------------------------------------- drawing

        protected override void Draw(GameTime gameTime)
        {
            SyncLayout();

            // Direct, responsive rendering: the board is laid out for the real back-buffer
            // size each frame, so resizing reflows the board instead of letterboxing it.
            GraphicsDevice.Clear(FeltEdge);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp);

            _spriteBatch.Draw(_bg, new Rectangle(0, 0, Layout.ScreenW, Layout.ScreenH), Color.White);

            DrawSlots();
            DrawColumns();
            DrawStockWaste();
            DrawWasteShuffle();
            DrawHud();
            DrawSlideAnims();
            DrawRecycleAnim();
            DrawUndoAnim();
            DrawDealAnim();
            DrawFlipAnim();
            DrawDraggedStack();
            DrawCompletionAnim();
            DrawHint();
            DrawFireworks();
            DrawToolbar();
            DrawKeyboardFocus();
            if (EndOverlayVisible)
            {
                DrawEndOverlay();
                DrawNewGameButton();   // keep the call-to-action crisp above the dim overlay
                DrawSettingsButton();
            }
            DrawHelpPopup();
            DrawDefinePopup();
            DrawSettingsPopup();
            DrawDefineCursor();

            _spriteBatch.End();

            base.Draw(gameTime);
        }

        /// Recompute the responsive layout from the current back-buffer size and re-anchor
        /// the size-dependent HUD widgets. Called at the top of Update and Draw (and from
        /// the live-resize handler) so geometry, input hit-testing and rendering always
        /// agree on the same window-fitted layout.
        private void SyncLayout()
        {
            int w = Math.Max(1, GraphicsDevice.PresentationParameters.BackBufferWidth);
            int h = Math.Max(1, GraphicsDevice.PresentationParameters.BackBufferHeight);
            Layout.Update(w, h);

            int bw = Sc(146), bh = Sc(42);
            _newGameButton = new Rectangle(Layout.ScreenW - bw - Sc(18), Sc(16), bw, bh);
            int sbw = bh; // square gear button, just left of New Game
            _settingsButton = new Rectangle(_newGameButton.Left - Sc(10) - sbw, Sc(16), sbw, bh);
        }

        private void DrawSlots()
        {
            for (int i = 0; i < _board.Slots.Count; i++)
            {
                var slot = _board.Slots[i];
                var rect = SlotRect(i);

                if (slot.IsEmpty)
                {
                    DrawRoundFill(rect, new Color(0, 0, 0, 60));
                    DrawRoundRing(rect, new Color(255, 255, 255, 48));
                    var sr = new Rectangle(rect.Center.X - Sc(19), rect.Center.Y - Sc(21), Sc(38), Sc(38));
                    _spriteBatch.Draw(_star, sr, Premult(new Color(255, 255, 255, 42)));
                    continue;
                }

                // Occupied: a yellow tab pops out of the top with the base word, and
                // the most recently placed card fully covers the rest (gold-outlined,
                // progress number top-left).
                var cat = _board.Categories[slot.CategoryId];
                DrawSlotTab(rect, cat.Name);

                Card top = null;
                for (int k = slot.Cards.Count - 1; k >= 0; k--)
                    if (!_animCardIds.Contains(slot.Cards[k].Id)) { top = slot.Cards[k]; break; }

                if (top != null)
                    DrawCardFace(rect, top, covered: false, slotTop: true);
                else
                    DrawRoundFill(rect, new Color(0, 0, 0, 50)); // base card still sliding in
            }
        }

        private void DrawColumns()
        {
            for (int col = 0; col < _board.Columns.Count; col++)
            {
                var c = _board.Columns[col];
                int drawCount = (_dragKind == DragKind.Column && col == _dragCol) ? _dragIdx : c.Count;

                // Always draw the "where cards go" outline so it stays put while the
                // column's cards are lifted off by an animation (otherwise the slot
                // flashes empty with no placeholder mid-animation).
                DrawRoundRing(new Rectangle(Layout.ColX(col), ColTop(col), Layout.CardW, Layout.CardH),
                              new Color(255, 255, 255, 38));
                if (c.Count == 0) continue;
                int[] ys = ColumnCardYs(col);

                // Pass 1: plates / backs (no words).
                for (int i = 0; i < drawCount; i++)
                {
                    if (_animCardIds.Contains(c[i].Id)) continue;
                    var r = new Rectangle(Layout.ColX(col), ys[i], Layout.CardW, Layout.CardH);
                    if (!c[i].FaceUp) DrawCardBack(r);
                    else DrawCardPlate(r, c[i], slotTop: false, covered: i < drawCount - 1);
                }

                // Pass 2: words on top, eased between centre and the peek strip so a
                // migrating word stays visible over the card covering it.
                for (int i = 0; i < drawCount; i++)
                {
                    if (!c[i].FaceUp || _animCardIds.Contains(c[i].Id)) continue;
                    var r = new Rectangle(Layout.ColX(col), ys[i], Layout.CardW, Layout.CardH);
                    DrawCardWord(r, c[i], WordPhase(c[i].Id), slotTop: false);
                }
            }
        }

        private void DrawStockWaste()
        {
            var stockR = StockRect();
            if (_board.Stock.Count > 0)
            {
                int layers = Math.Min(3, _board.Stock.Count);
                for (int k = layers - 1; k >= 1; k--)
                    DrawCardBack(new Rectangle(stockR.X + k * 2, stockR.Y - k * 3, stockR.Width, stockR.Height));
                DrawCardBack(stockR);
            }
            else
            {
                DrawRoundFill(stockR, new Color(0, 0, 0, 60));
                DrawRoundRing(stockR, new Color(255, 255, 255, 70));
                int ic = Sc(32);
                _spriteBatch.Draw(_refresh,
                    new Rectangle(stockR.Center.X - ic / 2, stockR.Center.Y - Sc(22), ic, ic),
                    Premult(new Color(255, 255, 255, 185)));
                DrawLabel("Recycle", stockR.Center.X, stockR.Center.Y + Sc(22), stockR.Width - Sc(12),
                          new Color(255, 255, 255, 205), 0.56f * S);
            }
            DrawLabel($"Deck  {_board.Stock.Count}", stockR.Center.X, stockR.Bottom + Sc(18),
                      stockR.Width + Sc(30), Color.White, 0.62f * S);

            DrawWasteFan();
            DrawLabel("Draw Pile", Layout.WasteX + Layout.CardW / 2, Layout.WasteY + Layout.CardH + Sc(18),
                      Layout.CardW + Sc(30), Color.White, 0.62f * S);
        }

        /// The waste is a right-opening fan: the newest (movable) card sits fully visible
        /// on top at WasteX, while the previous one or two peek out to its right with their
        /// words turned sideways along the exposed strip. Cards currently being shuffled by
        /// an animation are skipped here (the animation draws them instead).
        private void DrawWasteFan()
        {
            // Always draw the draw-pile outline so it stays visible while the top card
            // is lifted off by a shuffle / recycle / undo animation.
            DrawRoundRing(new Rectangle(Layout.WasteX, Layout.WasteY, Layout.CardW, Layout.CardH),
                          new Color(255, 255, 255, 38));

            int peek = Layout.WastePeek;
            int n = _board.Waste.Count;
            if (n == 0) return;

            // Draw the (up to) three visible cards. The two front PEEK cards (d==1,2) ease one slot
            // left to fill the gap as the top card is lifted off (and back as it returns). The TOP
            // card (d==0) always sits at its resting slot — omitted only while it rides the cursor
            // (drag) or is drawn by its own slide. The newly-uncovered BOTTOM card (d==3) does NOT
            // fade/slide in: it sits at the deepest visible slot at full opacity and is simply
            // REVEALED as the card in front of it slides left.
            for (int d = 3; d >= 0; d--)
            {
                int wi = n - 1 - d;
                if (wi < 0) continue;
                var card = _board.Waste[wi];
                if (_animCardIds.Contains(card.Id)) continue;

                if (d == 0)
                {
                    if (_dragKind == DragKind.Waste) continue; // top card is on the cursor
                    var rTop = new Rectangle(Layout.WasteX, Layout.WasteY, Layout.CardW, Layout.CardH);
                    DrawWasteCardMigrating(rTop, card, peek, 0f, 1f);
                    continue;
                }

                if (d == 3)
                {
                    // Only present while the fan is open (a card is being lifted off). It is drawn
                    // BEFORE (behind) the d==2 card, which fully covers it at rest and slides left to
                    // reveal it — so it pops in instantly (no fade) and is uncovered, not slid in.
                    if (_wasteShift <= 0.001f) continue;
                    var rBot = new Rectangle(Layout.WasteX + 2 * peek, Layout.WasteY, Layout.CardW, Layout.CardH);
                    DrawWasteCardMigrating(rBot, card, peek, 1f, 1f);
                    continue;
                }

                float dd = d - _wasteShift;
                if (dd < -0.01f) continue;
                float wp = Clamp01f(dd);
                var r = new Rectangle(Layout.WasteX + (int)Math.Round(dd * peek), Layout.WasteY, Layout.CardW, Layout.CardH);
                DrawWasteCardMigrating(r, card, peek, wp, 1f);
            }
        }

        private void DrawDraggedStack()
        {
            if (_dragKind == DragKind.None) return;
            var origin = _mouse - _grabOffset;
            for (int i = 0; i < _dragCards.Count; i++)
            {
                var r = new Rectangle((int)origin.X, (int)origin.Y + i * Layout.FaceUpOffset,
                                      Layout.CardW, Layout.CardH);
                DrawCardFace(r, _dragCards[i], covered: i < _dragCards.Count - 1);
            }
        }

        private void DrawHud()
        {
            DrawPill(Sc(24), Sc(18), Sc(34), $"Solved  {_board.ClearedCategories}/{_board.TotalCategories}",
                     Color.White, 0.72f * S);

            DrawNewGameButton();
            DrawSettingsButton();
            DrawMovesRibbon();
        }

        /// A polished dimensional button matching the New Game look: soft shadow, fill, a subtle
        /// darker lower half + a soft top gloss (the gentle top-light / bottom-shade that gives it
        /// depth), a coloured rim and a faint inner highlight ring. Shared by every raised button
        /// (New Game, the settings gear, Close) so they read as one family.
        private void DrawDimensionalButton(Rectangle btn, bool hover, Color baseCol, Color hiCol,
                                           Color ring, Color lowerShade, int glossAlpha, int innerAlpha, float e)
        {
            DrawRoundShadow(btn, e);
            DrawRoundFill(btn, Fade(hover ? hiCol : baseCol, e));
            var lower = new Rectangle(btn.X + Sc(4), btn.Center.Y, btn.Width - Sc(8), btn.Height / 2 - Sc(4));
            DrawRoundFill(lower, Fade(lowerShade, e));
            var gloss = new Rectangle(btn.X + Sc(5), btn.Y + Sc(4), btn.Width - Sc(10), btn.Height / 2 - Sc(2));
            DrawRoundFill(gloss, Fade(new Color(255, 255, 255, glossAlpha), e));
            DrawRoundRing(btn, Fade(ring, e));
            var inset = new Rectangle(btn.X + Sc(2), btn.Y + Sc(2), btn.Width - Sc(4), btn.Height - Sc(4));
            DrawRoundRing(inset, Fade(new Color(255, 255, 255, innerAlpha), e));
        }

        private static readonly Color NewGameLower = new(150, 96, 8, 40);

        /// Polished yellow New Game button: shadow, fill, a soft lower shade and top
        /// gloss for a glossier look, a gold ring and a faint inner highlight ring.
        /// Drawn again above the end overlay so the call-to-action stays crisp.
        private void DrawNewGameButton()
        {
            bool hover = _newGameButton.Contains(_mouse.ToPoint());
            DrawDimensionalButton(_newGameButton, hover, ButtonYellow, ButtonYellowHi, ButtonRing, NewGameLower, 82, 78, 1f);
            DrawTextFit("New Game", _newGameButton.Center.X, _newGameButton.Center.Y, _newGameButton.Width - Sc(22), ButtonInk, 0.72f * S);
        }

        // "Out of moves" / win panel entrance: the dim backdrop fades in while the
        // panel floats up into place (driven by _overlayT in UpdateAnimations).
        private const float OverlayDur = 0.42f;
        private float _overlayT;
        private bool EndOverlayVisible =>
            _board.State != GameState.Playing && _completions.Count == 0
            && (_board.State == GameState.Lost || _winPopupShown);

        private void DrawEndOverlay()
        {
            float e = EaseOutCubic(Clamp01f(_overlayT / OverlayDur));
            FillRect(new Rectangle(0, 0, Layout.ScreenW, Layout.ScreenH), new Color(0, 0, 0, (int)(150 * e)));

            bool won = _board.State == GameState.Won;
            int pw = won ? Sc(560) : Sc(520), ph = won ? Sc(470) : Sc(300);
            pw = Math.Min(pw, Layout.ScreenW - Sc(16));   // keep the panel inside a narrow window
            ph = Math.Min(ph, Layout.ScreenH - Sc(16));
            int rise = (int)((1f - e) * Sc(48));   // start lower, float up to the centre
            var panel = new Rectangle(Layout.ScreenW / 2 - pw / 2, Layout.ScreenH / 2 - ph / 2 + rise, pw, ph);

            if (e > 0.1f) DrawRoundShadow(panel);
            DrawRoundFill(panel, new Color(24, 38, 30, (int)(252 * e)));
            Color ring = won ? Gold : new Color(236, 140, 110);
            DrawRoundRing(panel, new Color(ring.R, ring.G, ring.B, (int)(ring.A * e)));

            string title = won ? "YOU WIN!" : "OUT OF MOVES";
            Color tc = won ? new Color(255, 222, 120) : new Color(255, 158, 128);
            // Rendered from the 48px GameFontLarge so the big title stays crisp instead of being a
            // blurry 2x upscale of the 22px body font; same on-screen size as before (22*2.0 / 48).
            float titleScale = 2.0f * S * (22f / 48f);
            DrawTextCentered(title, panel.Center.X, panel.Y + Sc(44), titleScale, Cov(tc, e), true, _titleFont);

            if (won)
            {
                DrawLabel($"Cleared all {_board.TotalCategories} sets with {_winMovesLeft} moves left in {FormatTime(_winSeconds)}.",
                          panel.Center.X, panel.Y + Sc(128), panel.Width - Sc(56), Cov(new Color(220, 226, 220), e), 0.62f * S);
                DrawScoreTable(panel, e, panel.Y + Sc(188));
                DrawLabel("Click \"New Game\" to play again", panel.Center.X, panel.Bottom - Sc(30),
                          panel.Width - Sc(50), Cov(new Color(214, 222, 214), e), 0.56f * S);
            }
            else
            {
                DrawLabel($"Solved {_board.ClearedCategories} of {_board.TotalCategories} sets.",
                          panel.Center.X, panel.Y + Sc(128), panel.Width - Sc(56), Cov(new Color(232, 224, 220), e), 0.72f * S);
                DrawLabel("Take back your last move to keep trying, or start a New Game.",
                          panel.Center.X, panel.Y + Sc(168), panel.Width - Sc(56), Cov(new Color(226, 210, 205), e), 0.58f * S);
                bool canUndo = _undoStack.Count > 0;
                DrawUndoButton(UndoButtonRect(panel), e, canUndo);
            }
        }

        private static string FormatTime(double seconds)
        {
            int total = (int)Math.Round(Math.Max(0, seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        /// The best-scores table on the win panel: rank, moves left, and time, with this run's row
        /// highlighted if it placed. `top` is the y of the "Best Scores" heading.
        private void DrawScoreTable(Rectangle panel, float e, int top)
        {
            DrawLabel("Best Scores", panel.Center.X, top, panel.Width - Sc(60),
                      Cov(new Color(255, 222, 140), e), 0.7f * S);

            int cRank = panel.X + Sc(96);
            int cMoves = panel.X + Sc(288);
            int cTime = panel.X + Sc(462);
            int colY = top + Sc(34);
            Color hc = Cov(new Color(184, 204, 194), e);
            DrawTextFit("#", cRank, colY, Sc(44), hc, 0.54f * S);
            DrawTextFit("Moves Left", cMoves, colY, Sc(180), hc, 0.54f * S);
            DrawTextFit("Time", cTime, colY, Sc(130), hc, 0.54f * S);

            int rowH = Sc(30);
            int rowY = colY + Sc(30);
            for (int i = 0; i < ScoreBoard.MaxEntries; i++)
            {
                int y = rowY + i * rowH;
                if (i < _topScores.Count)
                {
                    var s = _topScores[i];
                    bool mine = i == _scoreRank;
                    if (mine)
                        DrawRoundFill(new Rectangle(panel.X + Sc(28), y - rowH / 2 + Sc(1), panel.Width - Sc(56), rowH - Sc(3)),
                                      Cov(new Color(74, 132, 96, 150), e));
                    Color rc = Cov(mine ? new Color(255, 236, 150) : Color.White, e);
                    DrawTextFit($"{i + 1}", cRank, y, Sc(44), rc, 0.62f * S);
                    DrawTextFit($"{s.MovesLeft}", cMoves, y, Sc(180), rc, 0.62f * S);
                    DrawTextFit(FormatTime(s.Seconds), cTime, y, Sc(130), rc, 0.62f * S);
                }
                else
                {
                    DrawTextFit("-", cRank, y, Sc(44), Cov(new Color(255, 255, 255, 60), e), 0.62f * S);
                }
            }
        }

        /// Lose-screen "Undo" button rect, anchored to the bottom of the panel (sized as a
        /// comfortable touch target).
        private Rectangle UndoButtonRect(Rectangle panel)
        {
            int bw = Sc(230), bh = Sc(56);
            return new Rectangle(panel.Center.X - bw / 2, panel.Bottom - bh - Sc(18), bw, bh);
        }

        /// The settled lose-panel (no float-up offset) — used for hit-testing the Undo button.
        /// Must match the lose panel drawn in DrawEndOverlay.
        private Rectangle LosePanelRect()
        {
            int pw = Math.Min(Sc(520), Layout.ScreenW - Sc(16));
            int ph = Math.Min(Sc(300), Layout.ScreenH - Sc(16));
            return new Rectangle(Layout.ScreenW / 2 - pw / 2, Layout.ScreenH / 2 - ph / 2, pw, ph);
        }

        private void DrawUndoButton(Rectangle btn, float e, bool enabled)
        {
            bool hover = enabled && btn.Contains(_mouse.ToPoint());
            var baseCol = enabled ? (hover ? new Color(104, 188, 130) : new Color(80, 160, 108)) : new Color(72, 90, 80);
            int A(int a) => (int)(a * Clamp01f(e));
            DrawRoundShadow(btn);
            DrawRoundFill(btn, new Color(baseCol.R, baseCol.G, baseCol.B, A(255)));
            DrawRoundFill(new Rectangle(btn.X + Sc(4), btn.Center.Y, btn.Width - Sc(8), btn.Height / 2 - Sc(4)), new Color(16, 66, 38, A(60)));
            DrawRoundFill(new Rectangle(btn.X + Sc(5), btn.Y + Sc(4), btn.Width - Sc(10), btn.Height / 2 - Sc(2)), new Color(255, 255, 255, A(72)));
            DrawRoundRing(btn, new Color(46, 112, 74, A(255)));
            DrawRoundRing(new Rectangle(btn.X + Sc(2), btn.Y + Sc(2), btn.Width - Sc(4), btn.Height - Sc(4)), new Color(255, 255, 255, A(70)));
            DrawTextFit("Undo Move", btn.Center.X, btn.Center.Y, btn.Width - Sc(26),
                        Cov(enabled ? Color.White : new Color(200, 205, 200), e), 0.66f * S);
        }

        // ------------------------------------------------------------ card faces

        private void DrawCardFace(Rectangle r, Card card, bool covered, bool slotTop = false)
        {
            DrawCardPlate(r, card, slotTop, covered);
            float phase = (covered && !slotTop) ? 1f : 0f;
            DrawCardWord(r, card, phase, slotTop);
        }

        /// Card body only (shadow, plate, gold/edge ring, progress badge, base star) —
        /// everything except the word, which is drawn separately so it can animate.
        /// A covered card shows just its gold edge + word strip (no badge / star) so
        /// the peeking strip stays uncluttered.
        private void DrawCardPlate(Rectangle r, Card card, bool slotTop, bool covered)
        {
            DrawCardShadow(r);
            bool gold = slotTop || card.IsBase;
            DrawPlate(r, CardCream, gold);

            if (covered && !slotTop) return;

            if (slotTop || card.IsBase) DrawBadge(_board.Categories[card.CategoryId], r);
            if (card.IsBase)
                _spriteBatch.Draw(_star, new Rectangle(r.Right - Sc(27), r.Y + Sc(7), Sc(20), Sc(20)), Color.White);
        }

        /// A card's content: EMOJI-cards show just their emoji (large, eased to the peek
        /// strip when covered); WORD-cards show the title-cased word the same way. Cards are
        /// either-or — the data marks a mix of word and emoji cards per set.
        private void DrawCardWord(Rectangle r, Card card, float phase, bool slotTop)
        {
            if (card.ShowEmoji && _emoji != null)
            {
                var g = _emoji.Get(card.Emoji);
                if (g.Valid) { DrawCardEmoji(r, g, phase, slotTop); return; }
            }

            string w = Display(card.Word);
            if (slotTop)
            {
                DrawWordLines(w, r.Center.X, r.Center.Y, r.Width - Sc(WordMargin), Ink, 0.92f * S);
                return;
            }

            float centerY = r.Center.Y;
            float stripY = r.Y + Layout.FaceUpOffset / 2f + 1f;
            float y = Lerp(centerY, stripY, phase);
            float scale = Lerp(0.96f, 0.64f, phase) * S;
            float centerW = r.Width - Sc(WordMargin);
            float stripW = r.Width - Sc(16);
            float maxW = Lerp(centerW, stripW, phase);
            DrawWordLines(w, r.Center.X, y, maxW, Ink, scale);
        }

        /// An emoji-only card: the emoji's tight glyph shown large + centred (phase 0), easing
        /// up to the thin top peek strip when the card is covered (phase 1). The glyph is scaled
        /// to fit the target box by its larger dimension, so it's never truncated and stays
        /// centred regardless of the emoji's internal padding.
        private void DrawCardEmoji(Rectangle r, EmojiRenderer.Glyph g, float phase, bool slotTop)
        {
            if (slotTop)
            {
                float box = Math.Min(r.Width * 0.58f, r.Height * 0.58f);
                DrawGlyph(g, r.Center.X, r.Center.Y, box);
                return;
            }
            float big = Math.Min(r.Width * 0.6f, r.Height * 0.5f);
            float strip = Layout.FaceUpOffset * 0.95f;
            float box2 = Lerp(big, strip, phase);
            float cy = Lerp(r.Center.Y, r.Y + Layout.FaceUpOffset / 2f + 1f, phase);
            DrawGlyph(g, r.Center.X, cy, box2);
        }

        /// Draw an emoji glyph's tight region scaled so its larger dimension == box, centred at
        /// (cx, cy). Preserves aspect, fills the box, never clips.
        private void DrawGlyph(EmojiRenderer.Glyph g, float cx, float cy, float box, float alpha = 1f)
        {
            if (!g.Valid) return;
            float scale = box / Math.Max(g.Src.Width, g.Src.Height);
            int w = Math.Max(1, (int)Math.Round(g.Src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(g.Src.Height * scale));
            var dst = new Rectangle((int)Math.Round(cx - w / 2f), (int)Math.Round(cy - h / 2f), w, h);
            _spriteBatch.Draw(g.Tex, dst, g.Src, Color.White * alpha);
        }

        /// Draw a WHITE-mask glyph (e.g. a Fluent SVG icon) tinted with `tint` at opacity `alpha`.
        private void DrawGlyph(EmojiRenderer.Glyph g, float cx, float cy, float box, Color tint, float alpha)
        {
            if (!g.Valid) return;
            float scale = box / Math.Max(g.Src.Width, g.Src.Height);
            int w = Math.Max(1, (int)Math.Round(g.Src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(g.Src.Height * scale));
            var dst = new Rectangle((int)Math.Round(cx - w / 2f), (int)Math.Round(cy - h / 2f), w, h);
            _spriteBatch.Draw(g.Tex, dst, g.Src, Cov(tint, alpha));
        }

        private void DrawBadge(Category cat, Rectangle r)
        {
            string t = $"{cat.Placed}/{cat.Size}";
            var pos = new Vector2(r.X + Sc(10), r.Y + Sc(7));
            _spriteBatch.DrawString(_font, t, pos, Ink, 0f, Vector2.Zero, 0.5f * S, SpriteEffects.None, 0f);
        }

        private void DrawCardBack(Rectangle r)
        {
            DrawCardShadow(r);
            _spriteBatch.Draw(_cardBack, r, Color.White);
        }

        /// The card shadow texture is a baked rounded-rect glow; stretch it into a
        /// scaled destination (proportional pad / drop) so it tracks the live card size.
        private void DrawCardShadow(Rectangle r)
        {
            float px = ShadowPad * (r.Width / (float)Layout.BaseCardW);
            float py = ShadowPad * (r.Height / (float)Layout.BaseCardH);
            float drop = ShadowDrop * (r.Height / (float)Layout.BaseCardH);
            var dst = new Rectangle(
                (int)Math.Round(r.X - px),
                (int)Math.Round(r.Y - py + drop),
                (int)Math.Round(r.Width + 2 * px),
                (int)Math.Round(r.Height + 2 * py));
            _spriteBatch.Draw(_cardShadow, dst, Color.White);
        }

        private void DrawPlate(Rectangle r, Color fill, bool gold)
        {
            _spriteBatch.Draw(_cardFill, r, fill);
            if (gold) _spriteBatch.Draw(_cardRingGold, r, Gold);
            else _spriteBatch.Draw(_cardRingThin, r, EdgeGray);
        }

        // ----------------------------------------------------------- 9-slice UI

        // 9-slice corner radius scales with Layout.Scale so the rounded corners shrink
        // and grow together with the cards instead of staying a fixed pixel size.
        private int RoundDst => Math.Max(2, Sc(RoundMargin));
        private void DrawRoundFill(Rectangle r, Color c) => DrawNine(_round, r, RoundMargin, RoundDst, c);
        private void DrawRoundRing(Rectangle r, Color c) => DrawNine(_roundRing, r, RoundMargin, RoundDst, c);

        private void DrawRoundShadow(Rectangle r)
        {
            int pad = Sc(10);
            var dst = new Rectangle(r.X - pad, r.Y - pad + Sc(ShadowDrop), r.Width + 2 * pad, r.Height + 2 * pad);
            DrawNine(_roundShadow, dst, RoundShadowMargin, Math.Max(4, Sc(RoundShadowMargin)), Color.White);
        }

        /// A round shadow faded to `alpha` (0..1) — used by animating toolbar pills so a retracting
        /// pill's shadow fades with it instead of stacking into a dark smudge at the button.
        private void DrawRoundShadow(Rectangle r, float alpha)
        {
            int pad = Sc(10);
            var dst = new Rectangle(r.X - pad, r.Y - pad + Sc(ShadowDrop), r.Width + 2 * pad, r.Height + 2 * pad);
            DrawNine(_roundShadow, dst, RoundShadowMargin, Math.Max(4, Sc(RoundShadowMargin)),
                     new Color(255, 255, 255, (int)(255 * Clamp01(alpha))));
        }

        private int DrawPill(int x, int y, int h, string text, Color textColor, float scale)
        {
            int w = (int)(_font.MeasureString(text).X * scale) + Sc(28);
            var rect = new Rectangle(x, y, w, h);
            DrawRoundFill(rect, new Color(0, 0, 0, 78));
            DrawTextFit(text, rect.Center.X, rect.Center.Y, w - Sc(14), textColor, scale);
            return x + w;
        }

        private void DrawNine(Texture2D tex, Rectangle d, int srcM, int dstM, Color c)
        {
            c = Premult(c);
            int tw = tex.Width, th = tex.Height;
            int sm = srcM;
            int mx = Math.Min(dstM, d.Width / 2);
            int my = Math.Min(dstM, d.Height / 2);
            int cw = d.Width - 2 * mx, ch = d.Height - 2 * my;

            // corners (source band = srcM, destination band = scaled dstM)
            _spriteBatch.Draw(tex, new Rectangle(d.X, d.Y, mx, my), new Rectangle(0, 0, sm, sm), c);
            _spriteBatch.Draw(tex, new Rectangle(d.Right - mx, d.Y, mx, my), new Rectangle(tw - sm, 0, sm, sm), c);
            _spriteBatch.Draw(tex, new Rectangle(d.X, d.Bottom - my, mx, my), new Rectangle(0, th - sm, sm, sm), c);
            _spriteBatch.Draw(tex, new Rectangle(d.Right - mx, d.Bottom - my, mx, my), new Rectangle(tw - sm, th - sm, sm, sm), c);

            if (cw > 0)
            {
                _spriteBatch.Draw(tex, new Rectangle(d.X + mx, d.Y, cw, my), new Rectangle(sm, 0, tw - 2 * sm, sm), c);
                _spriteBatch.Draw(tex, new Rectangle(d.X + mx, d.Bottom - my, cw, my), new Rectangle(sm, th - sm, tw - 2 * sm, sm), c);
            }
            if (ch > 0)
            {
                _spriteBatch.Draw(tex, new Rectangle(d.X, d.Y + my, mx, ch), new Rectangle(0, sm, sm, th - 2 * sm), c);
                _spriteBatch.Draw(tex, new Rectangle(d.Right - mx, d.Y + my, mx, ch), new Rectangle(tw - sm, sm, sm, th - 2 * sm), c);
            }
            if (cw > 0 && ch > 0)
                _spriteBatch.Draw(tex, new Rectangle(d.X + mx, d.Y + my, cw, ch), new Rectangle(sm, sm, tw - 2 * sm, th - 2 * sm), c);
        }

        // ------------------------------------------------------------- text

        private void FillRect(Rectangle r, Color c) => _spriteBatch.Draw(_pixel, r, Premult(c));

        private void DrawTextFit(string text, int centerX, int centerY, int maxWidth, Color color, float baseScale)
        {
            Vector2 size = _font.MeasureString(text);
            float scale = baseScale;
            if (size.X * scale > maxWidth) scale = maxWidth / size.X;
            var pos = new Vector2((float)Math.Round(centerX - size.X * scale / 2f),
                                  (float)Math.Round(centerY - size.Y * scale / 2f));
            _spriteBatch.DrawString(_font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        /// Draw a card word, splitting a multi-WORD entry (a space in the display string) onto
        /// stacked, centred lines so two-word names like "Card Catalog" read on two lines. A
        /// single word behaves exactly like DrawTextFit.
        private void DrawWordLines(string text, float cx, float cy, float maxWidth, Color color, float baseScale)
        {
            var lines = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= 1)
            {
                DrawTextFit(text, (int)Math.Round(cx), (int)Math.Round(cy), (int)Math.Round(maxWidth), color, baseScale);
                return;
            }

            float lineH = _font.MeasureString("Ag").Y;
            float scale = baseScale;
            foreach (var ln in lines)
            {
                float wdt = _font.MeasureString(ln).X;
                if (wdt > 0 && wdt * scale > maxWidth) scale = maxWidth / wdt;
            }

            float step = lineH * scale * 0.92f; // slight overlap so two lines stay compact
            float totalH = step * (lines.Length - 1);
            float y = cy - totalH / 2f;
            foreach (var ln in lines)
            {
                Vector2 sz = _font.MeasureString(ln);
                var pos = new Vector2((float)Math.Round(cx - sz.X * scale / 2f),
                                      (float)Math.Round(y - sz.Y * scale / 2f));
                _spriteBatch.DrawString(_font, ln, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                y += step;
            }
        }

        private void DrawLabel(string text, int centerX, int centerY, int maxWidth, Color color, float baseScale, bool shadow = true)
        {
            Vector2 size = _font.MeasureString(text);
            float scale = baseScale;
            if (size.X * scale > maxWidth) scale = maxWidth / size.X;
            // Snap to whole pixels so the glyphs stay crisp (fractional positions get blurred by the linear sampler).
            var pos = new Vector2((float)Math.Round(centerX - size.X * scale / 2f),
                                  (float)Math.Round(centerY - size.Y * scale / 2f));
            if (shadow)
                _spriteBatch.DrawString(_font, text, pos + new Vector2(1, 1), new Color(0, 0, 0, 120),
                                        0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        private void DrawTextCentered(string text, int cx, int topY, float scale, Color color, bool shadow, SpriteFont font = null)
        {
            font ??= _font;
            Vector2 size = font.MeasureString(text);
            // Snap to whole pixels so the glyphs stay crisp (fractional positions get blurred by the linear sampler).
            var pos = new Vector2((float)Math.Round(cx - size.X * scale / 2f), (float)Math.Round((double)topY));
            if (shadow)
                _spriteBatch.DrawString(font, text, pos + new Vector2(Sc(2), Sc(2)), new Color(0, 0, 0, 120),
                                        0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        // ---------------------------------------------------- texture generation

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        // Generated textures store PREMULTIPLIED alpha so they composite correctly
        // with MonoGame's default (premultiplied) AlphaBlend and the premultiplied
        // SpriteFont glyphs.
        private static Color Cov(Color c, float cov)
        {
            cov = Clamp01(cov);
            float fa = (c.A / 255f) * cov;
            return new Color((byte)(c.R * fa), (byte)(c.G * fa), (byte)(c.B * fa), (byte)(255f * fa));
        }

        /// Scale a STRAIGHT-alpha colour's opacity by e for the shape helpers (DrawRoundFill /
        /// DrawDisc / DrawPillFill / FillRect), which premultiply their argument internally. Unlike
        /// Cov — which returns an already-premultiplied tint for _spriteBatch.Draw of premultiplied
        /// textures and SpriteFont text — Fade leaves the colour straight-alpha so those helpers
        /// premultiply it exactly once (double-premultiplying a translucent gloss turns it dark).
        private static Color Fade(Color c, float e)
        {
            e = Clamp01(e);
            return new Color(c.R, c.G, c.B, (byte)(c.A * e));
        }

        /// Premultiply a (straight-alpha) tint so it can be applied to premultiplied
        /// textures without light/transparent areas blowing out to white.
        private static Color Premult(Color c)
        {
            float a = c.A / 255f;
            return new Color((byte)(c.R * a), (byte)(c.G * a), (byte)(c.B * a), c.A);
        }

        /// Signed distance from point (px,py) relative to centre to a rounded rect
        /// with half extents (hx,hy) and corner radius r. Negative = inside.
        private static float RoundRectSDF(float px, float py, float hx, float hy, float r)
        {
            float qx = Math.Abs(px) - (hx - r);
            float qy = Math.Abs(py) - (hy - r);
            float ax = Math.Max(qx, 0f), ay = Math.Max(qy, 0f);
            float outside = (float)Math.Sqrt(ax * ax + ay * ay);
            float inside = Math.Min(Math.Max(qx, qy), 0f);
            return outside + inside - r;
        }

        private Texture2D MakeRoundedFill(int w, int h, float r, Color color)
        {
            var data = new Color[w * h];
            float hx = w / 2f, hy = h / 2f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundRectSDF(x + 0.5f - hx, y + 0.5f - hy, hx, hy, r);
                    data[y * w + x] = Cov(color, 0.5f - d);
                }
            var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            return tex;
        }

        private Texture2D MakeRoundedRing(int w, int h, float r, float thick, Color color)
        {
            var data = new Color[w * h];
            float hx = w / 2f, hy = h / 2f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundRectSDF(x + 0.5f - hx, y + 0.5f - hy, hx, hy, r);
                    float outer = Clamp01(0.5f - d);
                    float inner = Clamp01(0.5f - (d + thick));
                    data[y * w + x] = Cov(color, outer - inner);
                }
            var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            return tex;
        }

        /// Coverage (0..1, anti-aliased) of the "banner" silhouette inset on every side
        /// by `inset`: a rectangle with a downward V notch carved from the top-centre edge.
        private static float BannerCoverage(float px, float py, int w, int h, float notch, float pad, float inset)
        {
            float left = pad + inset, right = w - pad - inset;
            float top = pad + inset, bottom = h - pad - inset;
            float bx = Math.Min(px - left, right - px);
            float by = Math.Min(py - top, bottom - py);
            float boxCov = Clamp01(bx + 0.5f) * Clamp01(by + 0.5f);

            float cx = w / 2f;
            float halfW = (right - left) / 2f;
            if (halfW < 1f) halfW = 1f;
            float t = Clamp01(1f - Math.Abs(px - cx) / halfW); // 1 at centre, 0 at the corners
            float slope = (float)Math.Sqrt(halfW * halfW + notch * notch) / halfW;
            float edgeY = (pad + notch * t) + inset * slope;   // V line, pushed down for insets
            float notchCov = Clamp01(edgeY - py + 0.5f);        // 1 inside the notch (above the V)
            return Clamp01(boxCov * (1f - notchCov));
        }

        private Texture2D MakeBannerFill(int w, int h, float notchFrac, float pad, Color color)
        {
            var data = new Color[w * h];
            float notch = notchFrac * h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    data[y * w + x] = Cov(color, BannerCoverage(x + 0.5f, y + 0.5f, w, h, notch, pad, 0f));
            var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            return tex;
        }

        private Texture2D MakeBannerRing(int w, int h, float notchFrac, float pad, float thick, Color color)
        {
            var data = new Color[w * h];
            float notch = notchFrac * h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float outer = BannerCoverage(x + 0.5f, y + 0.5f, w, h, notch, pad, 0f);
                    float inner = BannerCoverage(x + 0.5f, y + 0.5f, w, h, notch, pad, thick);
                    data[y * w + x] = Cov(color, Clamp01(outer - inner));
                }
            var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            return tex;
        }

        private Texture2D MakeShadow(int shapeW, int shapeH, float r, int pad, float feather, float baseAlpha)
        {
            int w = shapeW + pad * 2, h = shapeH + pad * 2;
            var data = new Color[w * h];
            float hx = shapeW / 2f, hy = shapeH / 2f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundRectSDF(x + 0.5f - w / 2f, y + 0.5f - h / 2f, hx, hy, r);
                    float a = d <= 0 ? baseAlpha : baseAlpha * Clamp01(1f - d / feather);
                    a *= a; // soften the falloff
                    data[y * w + x] = new Color((byte)0, (byte)0, (byte)0, (byte)(255 * a));
                }
            var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            return tex;
        }

        private Texture2D MakeBackground(int w, int h)
        {
            var data = new Color[w * h];
            float cx = w / 2f, cy = h / 2f;
            float maxd = (float)Math.Sqrt(cx * cx + cy * cy);
            var noise = new Random(20240617);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    float t = (float)Math.Sqrt(dx * dx + dy * dy) / maxd;
                    t = Clamp01(t * t * 0.92f);
                    int n = noise.Next(-5, 6);
                    byte R = (byte)MathHelper.Clamp(FeltCenter.R + (FeltEdge.R - FeltCenter.R) * t + n, 0, 255);
                    byte G = (byte)MathHelper.Clamp(FeltCenter.G + (FeltEdge.G - FeltCenter.G) * t + n, 0, 255);
                    byte B = (byte)MathHelper.Clamp(FeltCenter.B + (FeltEdge.B - FeltCenter.B) * t + n, 0, 255);
                    data[y * w + x] = new Color(R, G, B);
                }
            var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            return tex;
        }

        private Texture2D MakeCardBack(int w, int h, float r)
        {
            var fill = new Color(58, 110, 180);
            var dia1 = new Color(40, 86, 156);
            var dia2 = new Color(98, 152, 214);
            var line = new Color(126, 176, 228);
            var border = new Color(246, 248, 251);

            var data = new Color[w * h];
            float hx = w / 2f, hy = h / 2f;
            const float cell = 18f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundRectSDF(x + 0.5f - hx, y + 0.5f - hy, hx, hy, r);
                    float cov = Clamp01(0.5f - d);
                    if (cov <= 0f) { data[y * w + x] = Color.Transparent; continue; }

                    float u = x / cell, v = y / cell;
                    int cu = (int)Math.Floor(u), cv = (int)Math.Floor(v);
                    float fu = u - cu, fv = v - cv;
                    float dd = Math.Abs(fu - 0.5f) + Math.Abs(fv - 0.5f);

                    Color baseCol = fill;
                    if (dd < 0.5f) baseCol = ((cu + cv) & 1) == 0 ? dia2 : dia1;
                    if (Math.Abs(dd - 0.5f) < 0.05f) baseCol = line;

                    float innerCov = Clamp01(0.5f - (d + 3.2f));
                    float borderBand = Clamp01(cov - innerCov);
                    Color col = Color.Lerp(baseCol, border, borderBand);
                    data[y * w + x] = Cov(col, cov);
                }
            var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            return tex;
        }

        private Texture2D CreateStarTexture(int size, Color color)
        {
            var tex = new Texture2D(GraphicsDevice, size, size);
            var data = new Color[size * size];
            var pts = new Vector2[10];
            float cx = size / 2f, cy = size / 2f;
            float outer = size / 2f - 1f, inner = outer * 0.46f;
            for (int i = 0; i < 10; i++)
            {
                float ang = -MathHelper.PiOver2 + i * MathHelper.Pi / 5f;
                float rad = (i % 2 == 0) ? outer : inner;
                pts[i] = new Vector2(cx + rad * (float)Math.Cos(ang), cy + rad * (float)Math.Sin(ang));
            }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    data[y * size + x] = PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), pts)
                        ? color : Color.Transparent;
            tex.SetData(data);
            return tex;
        }

        private static bool PointInPolygon(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) &&
                    (p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X))
                    inside = !inside;
            }
            return inside;
        }

        /// A circular "refresh / recycle" arrow: an almost-closed ring with a small
        /// opening at the top and a chunky arrowhead at one tip. White + premultiplied
        /// so it can be tinted at draw time.
        private Texture2D MakeRefreshIcon(int size, Color color)
        {
            var data = new Color[size * size];
            float cx = size / 2f, cy = size / 2f;
            float ro = size * 0.40f, ri = size * 0.25f, rm = (ro + ri) / 2f;
            float gapC = -MathHelper.PiOver2;   // opening at the top
            float gapHalf = 0.60f;
            float tipA = gapC + gapHalf;        // right-hand tip of the arc

            var radial = new Vector2((float)Math.Cos(tipA), (float)Math.Sin(tipA));
            var tang = new Vector2(-radial.Y, radial.X); // increasing-angle tangent
            var point = -tang;                            // arrowhead points back toward the gap
            var baseC = new Vector2(cx + rm * radial.X, cy + rm * radial.Y);
            float aLen = size * 0.26f, aHalf = size * 0.20f;
            var tri = new[]
            {
                baseC + point * aLen,
                baseC + radial * aHalf,
                baseC - radial * aHalf,
            };

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float rad = (float)Math.Sqrt(dx * dx + dy * dy);
                    float ang = (float)Math.Atan2(dy, dx);
                    float cov = Clamp01(rad - ri + 0.5f) * Clamp01(ro - rad + 0.5f);
                    if (AngleDiff(ang, gapC) < gapHalf) cov = 0f;
                    if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), tri)) cov = 1f;
                    data[y * size + x] = Cov(color, cov);
                }
            var tex = new Texture2D(GraphicsDevice, size, size);
            tex.SetData(data);
            return tex;
        }

        private static float AngleDiff(float a, float b)
        {
            float d = Math.Abs(a - b) % MathHelper.TwoPi;
            return d > MathHelper.Pi ? MathHelper.TwoPi - d : d;
        }
    }
}
