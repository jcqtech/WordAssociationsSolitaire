using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WordAssociationsSolitaire
{
    /// Animation + extra-HUD layer for the polished game. Kept as a partial class
    /// so the core loop / texture generation in Game1.cs stays untouched.
    ///
    /// Three kinds of motion are tracked:
    ///  - a deck-draw FLIP (back -> face) that also slides stock -> waste,
    ///  - placement SLIDES (a card / run easing into its slot or column, and a
    ///    gentle slide-back when a drop is rejected),
    ///  - per-card WORD migration (the word easing between the card centre and the
    ///    thin "peek" strip when a card becomes covered / uncovered).
    /// Cards currently driven by an animation are listed in _animCardIds so the
    /// static board renderer skips them (the animation draws them instead).
    public partial class Game1
    {
        // ---- animation tunables ----
        private const float SlideDur = 0.17f;
        private const float FlipDur = 0.36f;
        private const float WordDur = 0.20f;
        private const float WasteShuffleDur = 0.30f; // right-fan reshuffle on each draw
        private const float ShineDur = 0.75f;        // gold shine before a completed stack flies
        private const float FlyDur = 1.15f;          // slow, fluid fly-off to the bottom-left
        private const float FlyStagger = 0.07f;      // per-card delay so the stack streams away
        private const int WordMargin = 30;   // horizontal breathing room for a centred word
        private const float ScootTau = 0.07f; // bottom-row scoot smoothing time constant (cascade lag)
        private const float RecyclePerCard = 0.18f;  // fast slide of each waste card into the deck
        private const float RecycleStagger = 0.025f;  // rapid stagger between recycled cards
        private const float RecycleFlipFrac = 0.72f;  // fraction of the trip spent flipping (rest zooms in)

        // ---- extra palette ----
        private static readonly Color RibbonLight = new(196, 226, 158);
        private static readonly Color RibbonBorder = new(150, 196, 110);
        private static readonly Color MovesGreen = new(28, 96, 52);
        private static readonly Color MovesWarn = new(190, 60, 46);
        private static readonly Color ButtonYellow = new(250, 206, 78);
        private static readonly Color ButtonYellowHi = new(255, 224, 120);
        private static readonly Color ButtonInk = new(96, 66, 12);
        private static readonly Color ButtonRing = new(196, 146, 36);
        private static readonly Color TabYellow = new(250, 206, 78);
        private static readonly Color TabInk = new(96, 66, 12);

        // ---- animation state ----
        private readonly HashSet<int> _animCardIds = new();

        private sealed class FlipState { public Card Card; public Vector2 Start, End; public float T; }
        private FlipState _flip;

        private sealed class SlideState
        {
            public List<Card> Cards;
            public Vector2[] Start, End;
            public bool[] SlotTop;
            public bool ToSlot;
            public float T;
        }
        private readonly List<SlideState> _slides = new();

        // Opening deal: at the start of a game the column cards fly out of the deck to their
        // resting spots, staggered so they're dealt quickly one after another.
        private sealed class DealCard { public Card Card; public int Col, Idx; public float Delay; public bool Flip; }
        private sealed class DealAnim { public List<DealCard> Cards = new(); public float T; }
        private DealAnim _dealAnim;
        private const float DealCardDur = 0.26f;  // each card's flight time
        private const float DealStagger = 0.03f;  // gap between successive cards leaving the deck
        private const float DealFlipDur = 0.26f;  // the exposed front card's back->face flip once it lands
        public bool Dealing => _dealAnim != null; // opening deal in progress (blocks board input)

        private sealed class WordTween { public float From, To, T; }
        private readonly Dictionary<int, WordTween> _word = new();

        // A right-fan reshuffle: the cards already in the waste slide one position deeper
        // (their words rotating to the sideways peek pose) while the deepest visible card
        // is shuffled out of view, every time a new card is drawn on top.
        private sealed class WasteShuffle
        {
            public Card[] Cards;
            public int[] FromD, ToD;          // fan depth: 0 = top, 1/2 = peek, 3 = gone
            public float[] FromWord, ToWord;  // 0 = centred word, 1 = sideways word
            public float T;
        }
        private WasteShuffle _wasteShuffle;

        // A finished category: its cards shine gold in the slot, then fly off to the
        // bottom-left of the screen along a slow, fluid arc before disappearing.
        private sealed class CompletionAnim
        {
            public int Slot;
            public List<Card> Cards; // bottom .. top
            public float T;
        }
        // A list (not a single anim) so that when two categories are cleared in quick
        // succession the first fly-off keeps playing and the second runs alongside it,
        // instead of the newer completion cancelling/skipping the older one.
        private readonly List<CompletionAnim> _completions = new();

        // Responsive two-row "scoot": the bottom column row eases down to keep a healthy
        // margin when a top-row column grows. _colScoot is each column's vertical offset
        // (only bottom-row columns move); a left-to-right follow-the-leader chain gives the
        // cascade its slightly staggered per-card start. A fresh deal snaps without animating.
        private float[] _colScoot = Array.Empty<float>();
        private bool _scootSnap = true;

        // Waste-fan "shift over": 0 = the top card is in place, 1 = the top card has been
        // lifted off (drag), so the two peeking cards slide one slot left to take its place.
        // Eases between the two so the fan glides instead of snapping when a card is picked up
        // and glides back if the drop is cancelled.
        private float _wasteShift;

        // Recycle: every waste card rapidly flips face-down back onto the deck. No card
        // is dealt afterwards — the deck is left fully overturned and the waste empty.
        private sealed class RecycleAnim
        {
            public Card[] Cards;     // old waste cards, flying to the stock
            public Vector2[] Start;
            public float T;
        }
        private RecycleAnim _recycle;

        // Win celebration: fireworks shoot up from the bottom until the player clicks,
        // which reveals the popup (gated by _winPopupShown).
        private sealed class Particle
        {
            public Vector2 Pos, Vel;
            public Color Color;
            public float Age, Life, Size;
            public bool Rocket;
            public bool Twinkle;   // late-life sparkle flicker for some burst embers
            public float Drag;     // per-second velocity damping (0 = none) for a soft burst "puff"
            public bool RiseSound; // rocket whose burst sound comes from firework-rise (no extra boom)
        }
        private sealed class Fireworks { public List<Particle> Parts = new(); public float SpawnTimer; }
        private Fireworks _fireworks;
        // Gentle gravity (pre-scale px/s^2) shared by the launch-speed solve and the per-frame physics —
        // lower than real so the whole celebration drifts slowly and gracefully.
        private const float FireworkGravity = 600f;
        // Seconds from the start of the firework-rise cue to its explosion crack (measured from the
        // audio). A "rise rocket" is launched to reach its burst exactly this long after the cue
        // starts, so the recorded boom lands on the on-screen burst.
        private const float FireworkRiseExplosion = 1.575f;
        private bool _winPopupShown;
        private Texture2D _spark;

        // ---- easing ----
        private static float Clamp01f(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float SmoothStep(float x) { x = Clamp01f(x); return x * x * (3f - 2f * x); }
        private static float EaseOutCubic(float x) { x = Clamp01f(x); float u = 1f - x; return 1f - u * u * u; }
        private static float EaseInOutCubic(float x)
        {
            x = Clamp01f(x);
            return x < 0.5f ? 4f * x * x * x : 1f - (float)Math.Pow(-2f * x + 2f, 3) / 2f;
        }
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// Quadratic Bézier point (one control point) for a gentle flight arc.
        private static Vector2 Bezier(Vector2 a, Vector2 c, Vector2 b, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * c + t * t * b;
        }

        /// Title-case a stored UPPERCASE word ("EGYPT" -> "Egypt", "CARD CATALOG" -> "Card
        /// Catalog") for a softer, modern look. Each space-separated word is title-cased so
        /// two-word entries read naturally (and render on two lines).
        // Display() runs for every visible word card every frame; cache the (deterministic) result
        // per source word so we don't allocate ~300 throwaway strings per frame (GC pressure -> stutter).
        private static readonly Dictionary<string, string> _displayCache = new();
        private static string Display(string w)
        {
            if (string.IsNullOrEmpty(w)) return w;
            if (_displayCache.TryGetValue(w, out var cached)) return cached;
            var result = Narrate.Word(w);
            _displayCache[w] = result;
            return result;
        }

        private void ResetAnimations()
        {
            _animCardIds.Clear();
            _slides.Clear();
            _dealAnim = null;
            _flip = null;
            _word.Clear();
            _wasteShuffle = null;
            _completions.Clear();
            _recycle = null;
            _colScoot = Array.Empty<float>();
            _scootSnap = true;
            _wasteShift = 0f;
        }

        private void UpdateAnimations(float dt)
        {
            if (_flip != null)
            {
                _flip.T += dt;
                if (_flip.T >= FlipDur) { _animCardIds.Remove(_flip.Card.Id); _flip = null; }
            }

            if (_wasteShuffle != null)
            {
                _wasteShuffle.T += dt;
                if (_wasteShuffle.T >= WasteShuffleDur)
                {
                    foreach (var c in _wasteShuffle.Cards) _animCardIds.Remove(c.Id);
                    _wasteShuffle = null;
                }
            }

            for (int i = _completions.Count - 1; i >= 0; i--)
            {
                var comp = _completions[i];
                comp.T += dt;
                if (comp.T >= CompletionTotal(comp)) _completions.RemoveAt(i);
            }

            for (int i = _slides.Count - 1; i >= 0; i--)
            {
                var s = _slides[i];
                s.T += dt;
                if (s.T >= SlideDur)
                {
                    foreach (var c in s.Cards) _animCardIds.Remove(c.Id);
                    _slides.RemoveAt(i);
                }
            }

            UpdateWordTweens(dt);
            UpdateColumnScoot(dt);
            UpdateWasteShift(dt);
            UpdateRecycle(dt);
            UpdateUndoAnim(dt);
            UpdateDealAnim(dt);
            UpdateHint(dt);
            UpdateFireworks(dt);

            // After the final set's fly-off completes, the win celebration (fireworks)
            // begins; the popup waits for a click.
            if (_board.State == GameState.Won && _completions.Count == 0 && _fireworks == null && !_winPopupShown)
                StartFireworks();

            // Drive the end-overlay float-up/fade entrance.
            _overlayT = EndOverlayVisible ? Math.Min(_overlayT + dt, OverlayDur) : 0f;
        }

        private static float CompletionTotal(CompletionAnim a) =>
            ShineDur + FlyDur + Math.Max(0, a.Cards.Count - 1) * FlyStagger;

        // ------------------------------------------------------------- opening deal

        /// Begin the opening deal: every column card is lifted off the board (drawn by the deal,
        /// not the board) and flies out of the deck to its resting spot, dealt row-by-row (across
        /// the columns, then down) so they land in quick succession.
        private void StartDealAnim()
        {
            _dealAnim = null;
            if (_board?.Columns == null || _board.Columns.Count == 0) return;

            int maxLen = 0;
            foreach (var c in _board.Columns) maxLen = Math.Max(maxLen, c.Count);

            var anim = new DealAnim();
            int order = 0;
            for (int row = 0; row < maxLen; row++)
                for (int col = 0; col < _board.Columns.Count; col++)
                {
                    var c = _board.Columns[col];
                    if (row >= c.Count) continue;
                    anim.Cards.Add(new DealCard { Card = c[row], Col = col, Idx = row, Delay = order * DealStagger, Flip = c[row].FaceUp });
                    _animCardIds.Add(c[row].Id);
                    order++;
                }

            if (anim.Cards.Count == 0) return;
            _dealAnim = anim;
            PlaySound("shuffling"); // a card-riffle as the deal begins
        }

        private void UpdateDealAnim(float dt)
        {
            if (_dealAnim == null) return;
            _dealAnim.T += dt;
            float t = _dealAnim.T, last = 0f;
            foreach (var dc in _dealAnim.Cards)
            {
                float done = dc.Delay + DealCardDur + (dc.Flip ? DealFlipDur : 0f);
                last = Math.Max(last, done);
                if (t >= done) _animCardIds.Remove(dc.Card.Id); // flight (+ flip) finished -> board draws it
            }
            if (t >= last) _dealAnim = null;
        }

        private void DrawDealAnim()
        {
            if (_dealAnim == null) return;
            var start = new Vector2(Layout.StockX, Layout.StockY);
            // Column resting Ys, computed once (the board is static during the deal).
            var ysByCol = new int[_board.Columns.Count][];
            for (int col = 0; col < _board.Columns.Count; col++) ysByCol[col] = ColumnCardYs(col);

            float t = _dealAnim.T;
            foreach (var dc in _dealAnim.Cards)
            {
                float local = t - dc.Delay;
                if (local < 0f) continue; // still in the deck
                var end = new Vector2(Layout.ColX(dc.Col), ysByCol[dc.Col][dc.Idx]);

                if (local < DealCardDur)
                {
                    // In flight from the deck: a face-down card riding out to its spot.
                    float e = EaseOutCubic(local / DealCardDur);
                    var p = Vector2.Lerp(start, end, e);
                    DrawCardBack(new Rectangle((int)Math.Round(p.X), (int)Math.Round(p.Y), Layout.CardW, Layout.CardH));
                }
                else if (dc.Flip && local < DealCardDur + DealFlipDur)
                {
                    // Landed: the exposed front card flips over (back -> face) in place.
                    float ft = (local - DealCardDur) / DealFlipDur;
                    DrawCardMidFlip(dc.Card, end, ft, withShadow: true);
                }
                // else: settled -> removed from _animCardIds, the board renders it.
            }
        }

        /// Drive each face-up column card's word toward its resting place: covered
        /// cards pull the word to the top strip (phase 1), the front card pulls it
        /// back to the centre (phase 0). A first-seen card snaps to its target so a
        /// fresh deal / reveal doesn't animate; later target changes ease in-out.
        private void UpdateWordTweens(float dt)
        {
            for (int col = 0; col < _board.Columns.Count; col++)
            {
                var c = _board.Columns[col];
                int eff = (_dragKind == DragKind.Column && col == _dragCol) ? _dragIdx : c.Count;
                for (int i = 0; i < eff; i++)
                {
                    if (!c[i].FaceUp) continue;
                    float target = (i < eff - 1) ? 1f : 0f;
                    int id = c[i].Id;
                    if (!_word.TryGetValue(id, out var wt))
                    {
                        _word[id] = new WordTween { From = target, To = target, T = WordDur };
                    }
                    else if (Math.Abs(wt.To - target) > 0.001f)
                    {
                        wt.From = Lerp(wt.From, wt.To, SmoothStep(Clamp01f(wt.T / WordDur)));
                        wt.To = target;
                        wt.T = 0f;
                    }
                    else
                    {
                        wt.T += dt;
                    }
                }
            }
        }

        private float WordPhase(int id) =>
            _word.TryGetValue(id, out var wt) ? Lerp(wt.From, wt.To, SmoothStep(Clamp01f(wt.T / WordDur))) : 0f;

        /// Ease the waste-fan shift toward 1 while the top card is being dragged, 0 otherwise,
        /// so the two peeking cards slide over to fill the gap (and back if the drop fails).
        private void UpdateWasteShift(float dt)
        {
            float target = _dragKind == DragKind.Waste ? 1f : 0f;
            float a = Math.Min(1f, dt * 16f); // ~0.12s glide
            _wasteShift += (target - _wasteShift) * a;
            if (Math.Abs(target - _wasteShift) < 0.001f) _wasteShift = target;
        }

        /// Keep the bottom column row a healthy margin below the (possibly grown) top row
        /// when the columns are wrapped into two rows. Each bottom-row card eases toward the
        /// needed downward offset, but each one follows its LEFT neighbour's current position
        /// rather than the target directly — a chain of low-pass filters that yields a smooth
        /// left-to-right cascade with a slightly staggered start per card. A fresh deal snaps
        /// straight to the resting offsets so dealing never triggers the animation.
        private void UpdateColumnScoot(float dt)
        {
            int cols = _board.Columns.Count;
            if (_colScoot.Length != cols) { _colScoot = new float[cols]; _scootSnap = true; }
            if (cols == 0) return;

            // raw = how far the whole bottom row must drop; per = first bottom-row column.
            float raw = 0f;
            int per = cols; // default: no wrapped bottom row (1-row layout) -> nothing scoots
            if (Layout.ColRows == 2 && cols >= 2)
            {
                per = Math.Clamp(Layout.ColsPerRow, 1, cols);

                int topBottom = int.MinValue;
                for (int col = 0; col < per; col++)
                    topBottom = Math.Max(topBottom, Layout.ColTopY(col) + ColStackExtent(col));

                int botTop = Layout.ColTopY(per);
                int healthy = Math.Max(Sc(14), Layout.ColRowGap);
                raw = Math.Max(0f, (topBottom + healthy) - botTop);

                // Keep a healthy margin above the deck / draw pile at the bottom: never let the
                // scoot push the bottom row down onto the stock or waste (they share StockY/WasteY).
                int botExtent = Layout.CardH;
                for (int col = per; col < cols; col++)
                    botExtent = Math.Max(botExtent, ColStackExtent(col));
                int deckTop = Math.Min(Layout.StockY, Layout.WasteY);
                int deckMargin = Math.Max(Sc(12), (int)Math.Round(0.16f * Layout.CardH));
                float maxPush = Math.Max(0f, (deckTop - deckMargin) - (botTop + botExtent));
                raw = Math.Min(raw, maxPush);
            }

            if (_scootSnap)
            {
                for (int col = 0; col < cols; col++) _colScoot[col] = (col >= per) ? raw : 0f;
                _scootSnap = false;
                return;
            }

            // Frame-rate-independent ease; `prev` keeps the one-frame lag that staggers the chain.
            float a = 1f - (float)Math.Exp(-dt / ScootTau);
            var prev = (float[])_colScoot.Clone();
            for (int col = 0; col < cols; col++)
            {
                float tgt = col < per ? 0f          // top row (and 1-row layouts): rest at 0
                          : col == per ? raw         // leftmost bottom column leads
                          : prev[col - 1];           // the rest follow their left neighbour
                _colScoot[col] += (tgt - _colScoot[col]) * a;
            }
        }

        // ---- starting animations ----

        private void StartDrawFlip()
        {
            if (_flip != null) { _animCardIds.Remove(_flip.Card.Id); _flip = null; }
            if (_board.Waste.Count == 0) return;
            var card = _board.Waste[^1];
            _flip = new FlipState
            {
                Card = card,
                Start = new Vector2(Layout.StockX, Layout.StockY),
                End = new Vector2(Layout.WasteX, Layout.WasteY),
                T = 0f
            };
            _animCardIds.Add(card.Id);
        }

        /// On each draw, the cards already in the waste slide one fan position deeper
        /// (top -> peek, peek -> deeper) and the deepest visible card is shuffled out of
        /// view. The freshly drawn card (handled by the flip) lands on top. Reads the
        /// post-draw waste, where the new card is on top and the previous cards sit just
        /// beneath it; a recycle (waste emptied) leaves nothing to shuffle.
        private void StartWasteShuffle()
        {
            if (_wasteShuffle != null)
            {
                foreach (var c in _wasteShuffle.Cards) _animCardIds.Remove(c.Id);
                _wasteShuffle = null;
            }

            int top = _board.Waste.Count - 1; // the new card, driven by the flip
            var moving = new List<(Card card, int from, int to, float fw, float tw)>();
            if (top - 1 >= 0) moving.Add((_board.Waste[top - 1], 0, 1, 0f, 1f)); // old top -> peek
            if (top - 2 >= 0) moving.Add((_board.Waste[top - 2], 1, 2, 1f, 1f)); // peek -> deeper
            if (top - 3 >= 0) moving.Add((_board.Waste[top - 3], 2, 3, 1f, 1f)); // deepest -> gone
            if (moving.Count == 0) return;

            int n = moving.Count;
            var s = new WasteShuffle
            {
                Cards = new Card[n],
                FromD = new int[n],
                ToD = new int[n],
                FromWord = new float[n],
                ToWord = new float[n],
                T = 0f
            };
            for (int k = 0; k < n; k++)
            {
                s.Cards[k] = moving[k].card;
                s.FromD[k] = moving[k].from;
                s.ToD[k] = moving[k].to;
                s.FromWord[k] = moving[k].fw;
                s.ToWord[k] = moving[k].tw;
                _animCardIds.Add(moving[k].card.Id);
            }
            _wasteShuffle = s;
        }

        /// Capture a just-completed category so its stack can shine, then fly off. Added to
        /// the list so several completions can shine and stream away at the same time.
        private void StartSlotCompletion(CompletionEvent ev)
        {
            _completions.Add(new CompletionAnim
            {
                Slot = ev.Slot,
                Cards = new List<Card>(ev.Cards),
                T = 0f
            });
        }

        /// Cards collapsing onto a single point (a foundation slot, or a rejected
        /// waste card returning to the draw pile).
        private void StartSlideCollapse(List<Card> moving, Vector2 origin, Vector2 target, bool toSlot)
        {
            int n = moving.Count;
            var s = new SlideState
            {
                Cards = new List<Card>(moving),
                Start = new Vector2[n],
                End = new Vector2[n],
                SlotTop = new bool[n],
                ToSlot = toSlot,
                T = 0f
            };
            for (int k = 0; k < n; k++)
            {
                s.Start[k] = origin + new Vector2(0, k * Layout.FaceUpOffset);
                s.End[k] = target;
                s.SlotTop[k] = toSlot && k == n - 1;
                _animCardIds.Add(moving[k].Id);
            }
            _slides.Add(s);
        }

        /// A run settling onto the tail of a column (its real resting positions are
        /// read straight from the freshly-updated board).
        private void StartSlideToColumn(List<Card> moving, Vector2 origin, int col)
        {
            var c = _board.Columns[col];
            int n = moving.Count;
            int baseIdx = Math.Max(0, c.Count - n);
            int[] ys = ColumnCardYs(col);
            var s = new SlideState
            {
                Cards = new List<Card>(moving),
                Start = new Vector2[n],
                End = new Vector2[n],
                SlotTop = new bool[n],
                ToSlot = false,
                T = 0f
            };
            for (int k = 0; k < n; k++)
            {
                s.Start[k] = origin + new Vector2(0, k * Layout.FaceUpOffset);
                int idx = Math.Min(baseIdx + k, ys.Length - 1);
                s.End[k] = new Vector2(Layout.ColX(col), ys[idx]);
                _animCardIds.Add(moving[k].Id);
            }
            _slides.Add(s);
        }

        // ---- drawing animations ----

        private void DrawSlideAnims()
        {
            foreach (var s in _slides)
            {
                float e = EaseOutCubic(s.T / SlideDur);
                int n = s.Cards.Count;
                for (int k = 0; k < n; k++)
                {
                    var p = Vector2.Lerp(s.Start[k], s.End[k], e);
                    var r = new Rectangle((int)p.X, (int)p.Y, Layout.CardW, Layout.CardH);
                    bool covered = !s.ToSlot && k < n - 1;
                    DrawCardFace(r, s.Cards[k], covered, s.SlotTop[k]);
                }
            }
        }

        private void DrawFlipAnim()
        {
            if (_flip == null) return;
            float t = Clamp01f(_flip.T / FlipDur);
            var pos = Vector2.Lerp(_flip.Start, _flip.End, SmoothStep(t));
            DrawCardMidFlip(_flip.Card, pos, t, withShadow: false);
        }

        /// Draw a single card mid-flip at top-left `pos`, squished horizontally by
        /// |cos(pi*t)|: a full face-down back at t=0, edge-on (invisible) at t=0.5, and a
        /// full face-up card at t=1. Shared by the stock->waste draw flip and the opening
        /// deal's front-card reveal (the latter passes withShadow so it matches the board).
        private void DrawCardMidFlip(Card card, Vector2 pos, float t, bool withShadow)
        {
            float sx = Math.Max(0.03f, Math.Abs((float)Math.Cos(Math.PI * t)));
            int w = Math.Max(2, (int)(Layout.CardW * sx));
            int cx = (int)(pos.X + Layout.CardW / 2f);
            var r = new Rectangle(cx - w / 2, (int)pos.Y, w, Layout.CardH);

            if (withShadow) DrawCardShadow(r);

            if (t < 0.5f)
            {
                _spriteBatch.Draw(_cardBack, r, Color.White);
                return;
            }

            _spriteBatch.Draw(_cardFill, r, CardCream);
            _spriteBatch.Draw(card.IsBase ? _cardRingGold : _cardRingThin, r,
                              card.IsBase ? Gold : EdgeGray);
            var fg = card.ShowEmoji && _emoji != null ? _emoji.Get(card.Emoji) : default;
            if (fg.Valid)
            {
                float box = Math.Min(Layout.CardW * 0.6f, Layout.CardH * 0.5f);
                float gs = box / Math.Max(fg.Src.Width, fg.Src.Height);
                int gw = Math.Max(1, (int)Math.Round(fg.Src.Width * gs));
                int gh = Math.Max(1, (int)Math.Round(fg.Src.Height * gs));
                int dw = Math.Max(1, (int)Math.Round(gw * sx));
                var dst = new Rectangle(cx - dw / 2, (int)(pos.Y + Layout.CardH / 2f - gh / 2f), dw, gh);
                _spriteBatch.Draw(fg.Tex, dst, fg.Src, Color.White);
            }
            else
            {
                string wd = Display(card.Word);
                var ms = _font.MeasureString(wd);
                float bs = 0.92f * S;
                float maxW = Layout.CardW - Sc(WordMargin);
                if (ms.X * bs > maxW) bs = maxW / ms.X;
                var sc = new Vector2(bs * sx, bs);
                var p = new Vector2(cx - ms.X * sc.X / 2f, pos.Y + Layout.CardH / 2f - ms.Y * sc.Y / 2f);
                _spriteBatch.DrawString(_font, wd, p, Ink, 0f, Vector2.Zero, sc, SpriteEffects.None, 0f);
            }
        }

        // ---- waste fan reshuffle ----

        private void DrawWasteShuffle()
        {
            if (_wasteShuffle == null) return;
            var s = _wasteShuffle;
            int peek = Layout.WastePeek;
            float e = EaseOutCubic(s.T / WasteShuffleDur);

            // Back-to-front: the deepest (last) entries first, the rising top card last.
            for (int k = s.Cards.Length - 1; k >= 0; k--)
            {
                // The card leaving the visible fan (ToD >= 3) does NOT fade or slide off — it STAYS
                // put at its current slot and is simply COVERED by the card sliding into that slot,
                // then removed when the shuffle ends (it's no longer one of the visible three).
                bool gone = s.ToD[k] >= 3;
                float d = gone ? s.FromD[k] : Lerp(s.FromD[k], s.ToD[k], e);
                float wp = gone ? s.FromWord[k] : Lerp(s.FromWord[k], s.ToWord[k], e);
                int x = Layout.WasteX + (int)Math.Round(d * peek);
                var r = new Rectangle(x, Layout.WasteY, Layout.CardW, Layout.CardH);
                DrawWasteCardMigrating(r, s.Cards[k], peek, wp, 1f);
            }
        }

        /// A waste card whose word eases between the centred pose (wp = 0) and the
        /// sideways peek pose along the right strip (wp = 1); alpha fades a card that
        /// is being shuffled out of view.
        private void DrawWasteCardMigrating(Rectangle r, Card card, int peek, float wp, float alpha)
        {
            DrawPlateAlpha(r, alpha, card.IsBase);

            var eg = card.ShowEmoji && _emoji != null ? _emoji.Get(card.Emoji) : default;
            if (eg.Valid)
            {
                float big = Math.Min(r.Width * 0.6f, r.Height * 0.5f);
                float side = Math.Max(8, peek - Sc(4));
                float box = Lerp(big, side, wp);
                var centerPos = new Vector2(r.Center.X, r.Center.Y);
                var sidePos = new Vector2(r.Right - peek / 2f, r.Center.Y);
                var p2 = Vector2.Lerp(centerPos, sidePos, wp);
                float gs = box / Math.Max(eg.Src.Width, eg.Src.Height);
                int gw = Math.Max(1, (int)Math.Round(eg.Src.Width * gs));
                int gh = Math.Max(1, (int)Math.Round(eg.Src.Height * gs));
                var dst = new Rectangle((int)Math.Round(p2.X - gw / 2f), (int)Math.Round(p2.Y - gh / 2f), gw, gh);
                _spriteBatch.Draw(eg.Tex, dst, eg.Src, Color.White * alpha);
                return;
            }

            string w = Display(card.Word);
            Vector2 size2 = _font.MeasureString(w);

            float centerScale = 0.92f * S;
            float maxCenterW = r.Width - Sc(WordMargin);
            if (size2.X * centerScale > maxCenterW) centerScale = maxCenterW / size2.X;

            float sideScale = 0.5f * S;
            float maxSideLen = r.Height - Sc(14);
            if (size2.X * sideScale > maxSideLen) sideScale = maxSideLen / size2.X;
            float maxThick = peek - Sc(5);
            if (size2.Y * sideScale > maxThick) sideScale = maxThick / size2.Y;

            var centerPos2 = new Vector2(r.Center.X, r.Center.Y);
            var sidePos2 = new Vector2(r.Right - peek / 2f, r.Center.Y);
            var pos = Vector2.Lerp(centerPos2, sidePos2, wp);
            float rot = Lerp(0f, -MathHelper.PiOver2, wp);
            float sc = Lerp(centerScale, sideScale, wp);
            _spriteBatch.DrawString(_font, w, pos, Ink * alpha, rot, size2 / 2f, sc, SpriteEffects.None, 0f);
        }

        /// Card body (shadow + cream plate + edge/gold ring) tinted by alpha so it can
        /// fade. Used by the waste fan and the completion fly-off.
        private void DrawPlateAlpha(Rectangle r, float alpha, bool gold)
        {
            float px = ShadowPad * (r.Width / (float)Layout.BaseCardW);
            float py = ShadowPad * (r.Height / (float)Layout.BaseCardH);
            float drop = ShadowDrop * (r.Height / (float)Layout.BaseCardH);
            var sdst = new Rectangle(
                (int)Math.Round(r.X - px), (int)Math.Round(r.Y - py + drop),
                (int)Math.Round(r.Width + 2 * px), (int)Math.Round(r.Height + 2 * py));
            _spriteBatch.Draw(_cardShadow, sdst, Color.White * alpha);
            _spriteBatch.Draw(_cardFill, r, CardCream * alpha);
            _spriteBatch.Draw(gold ? _cardRingGold : _cardRingThin, r, (gold ? Gold : EdgeGray) * alpha);
        }

        // ---- completed-stack shine + fly-off ----

        private void DrawCompletionAnim()
        {
            for (int i = 0; i < _completions.Count; i++)
                DrawOneCompletion(_completions[i]);
        }

        private void DrawOneCompletion(CompletionAnim a)
        {
            int n = a.Cards.Count;
            var slotPos = new Vector2(Layout.SlotX(a.Slot), Layout.SlotY(a.Slot));

            if (a.T < ShineDur)
            {
                // Phase 1: the completed stack sits in the slot and glows gold.
                float p = a.T / ShineDur;
                float pulse = (float)Math.Sin(p * Math.PI);       // one smooth glow up and down
                float grow = 1f + 0.05f * (float)Math.Sin(p * Math.PI);
                int gw = (int)(Layout.CardW * grow), gh = (int)(Layout.CardH * grow);
                var r = new Rectangle(
                    (int)(slotPos.X + (Layout.CardW - gw) / 2f),
                    (int)(slotPos.Y + (Layout.CardH - gh) / 2f), gw, gh);

                DrawCardFace(r, a.Cards[n - 1], covered: false, slotTop: true);
                DrawRoundFill(r, new Color(255, 216, 100, (int)(120 * pulse)));
                _spriteBatch.Draw(_cardRingGold, r, GoldStar * (0.5f + 0.5f * pulse));
                return;
            }

            // Phase 2: every card flies off to the bottom-left along a slow, arcing path,
            // staggered so the stack streams away (top card leads), shrinking as it goes.
            float ft = a.T - ShineDur;
            var target = new Vector2(-Layout.CardW * 1.15f, Layout.ScreenH - Layout.CardH * 0.1f);
            var ctrl = new Vector2(slotPos.X - Layout.CardW * 1.2f, slotPos.Y - Layout.CardH * 0.55f);

            for (int i = 0; i < n; i++)
            {
                float local = ft - (n - 1 - i) * FlyStagger; // i = n-1 (top) leaves first
                float e = EaseInOutCubic(local / FlyDur);
                var pos = Bezier(slotPos, ctrl, target, e);
                float sc = Lerp(1f, 0.62f, e);
                int cw = (int)(Layout.CardW * sc), ch = (int)(Layout.CardH * sc);
                var r = new Rectangle((int)pos.X, (int)pos.Y, cw, ch);
                DrawCardFace(r, a.Cards[i], covered: false, slotTop: true);
            }
        }

        // ---- extra HUD pieces ----

        private void DrawMovesRibbon()
        {
            int moves = _board.MovesLeft;
            int ribW = Sc(118);
            int ribH = Sc(300);
            int x = Sc(26);
            int visible = Sc(156);               // how much shows above the bottom edge
            int y = Layout.ScreenH - visible;    // the lower tail (y + ribH) bleeds off-screen
            var rib = new Rectangle(x, y, ribW, ribH);

            // Soft drop shadow, the satin-green ribbon body, a subtle centre sheen, border.
            _spriteBatch.Draw(_ribbonFill, new Rectangle(x + Sc(3), y, ribW, ribH), Premult(new Color(0, 0, 0, 55)));
            _spriteBatch.Draw(_ribbonFill, rib, Premult(RibbonLight));
            int notch = (int)Math.Round(RibbonNotchFrac * ribH);
            FillRect(new Rectangle(x + (int)(ribW * 0.30f), y + notch, Sc(9), ribH), new Color(255, 255, 255, 34));
            _spriteBatch.Draw(_ribbonRing, rib, Premult(RibbonBorder));

            // "MOVES / LEFT" heading stacked above a big move count, all below the fork.
            bool unlimited = _board.Unlimited;
            Color tc = unlimited ? MovesGreen : (moves <= 20 ? MovesWarn : MovesGreen);
            int cx = rib.Center.X;
            int top = y + notch;
            DrawLabel("MOVES", cx, top + Sc(24), ribW - Sc(22), tc, 0.62f * S, shadow: false);
            DrawLabel("LEFT", cx, top + Sc(50), ribW - Sc(22), tc, 0.62f * S, shadow: false);
            if (unlimited)
                DrawInfinity(cx, top + Sc(96), tc);
            else
                DrawTextFit($"{moves}", cx, top + Sc(96), ribW - Sc(26), tc, 1.55f * S);
        }

        /// The infinity mark shown on the moves ribbon when the limiter is off. The count font is
        /// ASCII-only, so a real "∞" is rasterized once (see _infinityGlyph) and drawn tinted; a
        /// crossed two-loop fallback covers the rare case the glyph couldn't be rasterized.
        private void DrawInfinity(int cx, int cy, Color c)
        {
            if (_infinityGlyph.Valid)
            {
                DrawGlyph(_infinityGlyph, cx, cy, Sc(58), c, 1f);
                return;
            }
            int d = Sc(26), overlap = Sc(9);
            int top = cy - d / 2;
            DrawDiscRing(new Rectangle(cx - d + overlap / 2, top, d, d), c);
            DrawDiscRing(new Rectangle(cx - overlap / 2, top, d, d), c);
        }

        /// A little folder-style tab that pops out of the top of an occupied slot,
        /// labelled with the set's base word.
        private void DrawSlotTab(Rectangle card, string name)
        {
            float scale = 0.6f * S;
            int textW = (int)(_font.MeasureString(name).X * scale);
            int w = Math.Min(card.Width - Sc(4), Math.Max(Sc(56), textW + Sc(26)));
            int h = Sc(34);
            var tab = new Rectangle(card.Center.X - w / 2, card.Y - Sc(22), w, h);
            DrawRoundFill(tab, TabYellow);
            DrawRoundRing(tab, new Color(255, 255, 255, 120));
            DrawTextFit(name, tab.Center.X, card.Y - Sc(11), w - Sc(14), TabInk, scale);
        }

        // ---------------------------------------------------- recycle (flip into deck)

        /// The current on-screen fan position of each waste card (deeper-than-visible
        /// cards stack at the deepest fan slot), captured before a recycle so they can
        /// fly back to the deck from where they were.
        private Vector2[] CaptureWasteFanPositions()
        {
            int n = _board.Waste.Count;
            var pos = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                int d = Math.Min((n - 1) - i, 2);
                pos[i] = new Vector2(Layout.WasteX + d * Layout.WastePeek, Layout.WasteY);
            }
            return pos;
        }

        /// All waste cards rapidly flip face-down back onto the deck. Nothing is dealt
        /// afterwards: the deck is left fully overturned and the waste empty.
        private void StartRecycleAnim(List<Card> oldWaste, Vector2[] positions)
        {
            if (oldWaste == null || oldWaste.Count == 0) return;
            _recycle = new RecycleAnim { Cards = oldWaste.ToArray(), Start = positions, T = 0f };
            foreach (var c in oldWaste) _animCardIds.Add(c.Id);
        }

        private float RecycleTotal(RecycleAnim a) => RecyclePerCard + Math.Max(0, a.Cards.Length - 1) * RecycleStagger;

        private void UpdateRecycle(float dt)
        {
            if (_recycle == null) return;
            _recycle.T += dt;
            if (_recycle.T < RecycleTotal(_recycle)) return;

            foreach (var c in _recycle.Cards) _animCardIds.Remove(c.Id);
            _recycle = null;
        }

        private void DrawRecycleAnim()
        {
            if (_recycle == null) return;
            var a = _recycle;
            var stockPos = new Vector2(Layout.StockX, Layout.StockY);
            int n = a.Cards.Length;

            for (int k = 0; k < n; k++)
            {
                float delay = (n - 1 - k) * RecycleStagger;   // the front (top) card peels off first
                float local = a.T - delay;
                var card = a.Cards[k];

                if (local <= 0f)
                {
                    var sr = new Rectangle((int)a.Start[k].X, (int)a.Start[k].Y, Layout.CardW, Layout.CardH);
                    DrawCardFace(sr, card, covered: false);
                    continue;
                }

                float e = Clamp01f(local / RecyclePerCard);
                // Ease-in slide: the card lingers near the waste while it flips (so the flip is
                // clearly visible and not blurred by fast travel), then zooms into the deck. Keeps
                // the whole recycle snappy while making each face->back flip readable.
                var pos = Vector2.Lerp(a.Start[k], stockPos, EaseInCubic(e));
                float flipE = Clamp01f(e / RecycleFlipFrac);                     // a quick, distinct flip
                float sx = Math.Max(0.03f, Math.Abs((float)Math.Cos(Math.PI * flipE))); // 1 -> 0 -> 1 flip
                int w = Math.Max(2, (int)(Layout.CardW * sx));
                int cx = (int)(pos.X + Layout.CardW / 2f);
                var r = new Rectangle(cx - w / 2, (int)pos.Y, w, Layout.CardH);

                if (flipE < 0.5f)
                {
                    // Front half: the card's face, with its word/emoji, so it visibly flips OVER.
                    _spriteBatch.Draw(_cardFill, r, CardCream);
                    _spriteBatch.Draw(card.IsBase ? _cardRingGold : _cardRingThin, r, card.IsBase ? Gold : EdgeGray);
                    DrawCardWord(r, card, 0f, false);
                }
                else
                {
                    _spriteBatch.Draw(_cardBack, r, Color.White);
                }
            }
        }

        // ---------------------------------------------------------- win fireworks

        private void StartFireworks() => _fireworks = new Fireworks { SpawnTimer = 0f };

        private void UpdateFireworks(float dt)
        {
            if (_fireworks == null) return;
            var f = _fireworks;
            float g = Sc(FireworkGravity); // gentle gravity

            // Keep launching only while the celebration owns the screen. Once the player clicks to
            // raise the popup we STOP spawning, so the "YOU WIN!" panel floats up over a calming sky
            // (the lingering sparks fade on their own) — matching the clean lose-overlay entrance.
            // A soft cap on the live particle count keeps the GPU/GC load (and frame pacing) steady.
            if (_board.State == GameState.Won && !_winPopupShown && f.Parts.Count < 520)
            {
                f.SpawnTimer -= dt;
                if (f.SpawnTimer <= 0f)
                {
                    // Occasionally launch a "rise rocket": firework-rise plays now and the rocket is
                    // timed to burst exactly when that cue's recorded explosion hits.
                    bool rise = _rng.NextDouble() < 0.22 && f.Parts.Count < 300;
                    SpawnRocket(f, rise);
                    if (!rise && _rng.NextDouble() < 0.40 && f.Parts.Count < 360) SpawnRocket(f); // occasional doubles
                    f.SpawnTimer = 0.38f + (float)_rng.NextDouble() * 0.42f;
                }
            }

            for (int i = f.Parts.Count - 1; i >= 0; i--)
            {
                var p = f.Parts[i];
                p.Age += dt;
                p.Vel.Y += g * dt;
                if (p.Drag > 0f) p.Vel *= Math.Max(0f, 1f - p.Drag * dt); // air drag softens bursts
                p.Pos += p.Vel * dt;

                if (p.Rocket)
                {
                    // A short glowing trail streams behind each climbing rocket — emitted on ~every
                    // other frame so the trail stays light on the particle budget.
                    if (_rng.NextDouble() < 0.5)
                        f.Parts.Add(new Particle
                        {
                            Pos = p.Pos + new Vector2((float)(_rng.NextDouble() - 0.5) * Sc(3), Sc(2)),
                            Vel = new Vector2((float)(_rng.NextDouble() - 0.5) * Sc(16), Sc(20)),
                            Color = Color.Lerp(p.Color, Color.White, 0.5f),
                            Age = 0f,
                            Life = 0.34f + (float)_rng.NextDouble() * 0.18f,
                            Size = Sc(6),
                            Rocket = false,
                            Drag = 1.4f
                        });

                    if (p.Vel.Y >= 0f || p.Age >= p.Life) // reached apex -> burst
                    {
                        Burst(f, p);
                        f.Parts.RemoveAt(i);
                        continue;
                    }
                }

                if (p.Age >= p.Life) f.Parts.RemoveAt(i);
            }
        }

        private void SpawnRocket(Fireworks f, bool rise = false)
        {
            // Spread launches across the whole width and let each rocket peak at a RANDOM height across
            // the middle band of the window (~25%..75% from the top) so bursts are scattered all over
            // rather than clustered at the centre.
            float x = Layout.ScreenW * (0.12f + (float)_rng.NextDouble() * 0.76f);
            float spawnY = Layout.ScreenH + Sc(8);
            float g = Sc(FireworkGravity);
            float v0;
            if (rise)
            {
                // Launch so the time to apex equals the cue's rise-to-explosion time, then start the
                // firework-rise cue now: its recorded boom lands exactly on this rocket's burst.
                v0 = g * FireworkRiseExplosion;
                PlayFireworkSound("firework-rise", x, 0.85f);
            }
            else
            {
                float apexY = Layout.ScreenH * (0.25f + (float)_rng.NextDouble() * 0.50f);
                float rise2 = Math.Max(Sc(120), spawnY - apexY);
                v0 = (float)Math.Sqrt(2f * g * rise2);
            }
            float vx = (float)(_rng.NextDouble() - 0.5) * Sc(70); // gentle drift, no pull toward centre
            f.Parts.Add(new Particle
            {
                Pos = new Vector2(x, spawnY),
                Vel = new Vector2(vx, -v0),
                Color = FireworkColor(),
                Age = 0f,
                Life = 5f,
                Size = Sc(10),
                Rocket = true,
                RiseSound = rise
            });
        }

        private void Burst(Fireworks f, Particle rocket)
        {
            // Rise rockets already carry their own explosion (firework-rise). Everyone else gets a
            // boom on burst — usually the short pop, occasionally the big boom — panned to the burst.
            if (!rocket.RiseSound)
                PlayFireworkSound(_rng.NextDouble() < 0.14 ? "firework-2" : "firework-1", rocket.Pos.X);

            var baseColor = rocket.Color;
            // Half the bursts are two-tone (a second hue mixed in) for variety.
            var accent = _rng.NextDouble() < 0.5 ? baseColor : FireworkColor();
            int n = 50 + _rng.Next(26);
            float baseAngle = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            float maxSpeed = Sc(150) + (float)_rng.NextDouble() * Sc(120); // slower, gentler expansion
            for (int k = 0; k < n; k++)
            {
                float ang = baseAngle + MathHelper.TwoPi * k / n + (float)(_rng.NextDouble() - 0.5) * 0.10f;
                // sqrt() radius fills the disc evenly so the burst reads as a full, round bloom.
                float sp = maxSpeed * (0.45f + 0.55f * (float)Math.Sqrt(_rng.NextDouble()));
                Color col = _rng.NextDouble() < 0.16 ? Color.White
                          : (_rng.NextDouble() < 0.5 ? baseColor : accent);
                f.Parts.Add(new Particle
                {
                    Pos = rocket.Pos,
                    Vel = new Vector2((float)Math.Cos(ang) * sp, (float)Math.Sin(ang) * sp),
                    Color = col,
                    Age = 0f,
                    Life = 1.9f + (float)_rng.NextDouble() * 1.3f,   // linger a bit longer
                    Size = Sc(9) + (float)_rng.NextDouble() * Sc(5), // thicker = more visible
                    Rocket = false,
                    Twinkle = _rng.NextDouble() < 0.5,
                    Drag = 0.5f + (float)_rng.NextDouble() * 0.4f    // light drag -> slow, lazy drift
                });
            }
            // A bright, quickly-fading central flash at the burst point.
            f.Parts.Add(new Particle
            {
                Pos = rocket.Pos,
                Vel = Vector2.Zero,
                Color = Color.White,
                Age = 0f,
                Life = 0.34f,
                Size = Sc(48),
                Rocket = false
            });
        }

        private Color FireworkColor()
        {
            Color[] palette =
            {
                new(255, 90, 90), new(255, 196, 70), new(120, 220, 120),
                new(90, 180, 255), new(220, 130, 255), new(255, 240, 140), new(120, 240, 230)
            };
            return palette[_rng.Next(palette.Length)];
        }

        private void DrawFireworks()
        {
            if (_fireworks == null) return;
            foreach (var p in _fireworks.Parts)
            {
                float lifeFrac = Clamp01f(1f - p.Age / p.Life);
                // Bright for most of the life, easing out near the end; a glowing core plus a larger
                // soft halo so each spark reads clearly on the felt.
                float alpha = p.Rocket ? 1f : Clamp01f(lifeFrac * 1.7f);
                if (p.Twinkle && lifeFrac < 0.65f)
                    alpha *= 0.5f + 0.5f * (float)Math.Sin(p.Age * 34f); // gentle late-life flicker
                float scale = p.Rocket ? 1f : (0.55f + 0.45f * lifeFrac);
                int sz = Math.Max(3, (int)(p.Size * scale));
                int halo = (int)(sz * 2.2f);
                var hr = new Rectangle((int)(p.Pos.X - halo / 2f), (int)(p.Pos.Y - halo / 2f), halo, halo);
                var r = new Rectangle((int)(p.Pos.X - sz / 2f), (int)(p.Pos.Y - sz / 2f), sz, sz);
                _spriteBatch.Draw(_spark, hr, Premult(new Color(p.Color, alpha * 0.45f)));
                _spriteBatch.Draw(_spark, r, Premult(new Color(p.Color, alpha)));
            }
        }

        private Texture2D MakeSpark(int size)
        {
            var tex = new Texture2D(GraphicsDevice, size, size);
            var data = new Color[size * size];
            float r = size / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    float a = Clamp01(1f - d);
                    a = a * a;                         // soft glow falloff
                    byte v = (byte)(255 * a);
                    data[y * size + x] = new Color(v, v, v, v); // premultiplied white
                }
            tex.SetData(data);
            return tex;
        }

        // ---------------------------------------------------- shared anim helpers

        private void ClearTransientAnimations()
        {
            _slides.Clear();
            _flip = null;
            _wasteShuffle = null;
            _completions.Clear();
            _recycle = null;
            _undoAnim = null;
            _animCardIds.Clear();
        }

        private void ResetWinState()
        {
            _fireworks = null;
            _winPopupShown = false;
            _overlayT = 0f;
        }
    }
}
