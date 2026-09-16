using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace WordAssociationsSolitaire
{
    /// Player-interaction features layered on top of the core game: an animated
    /// UNDO (Ctrl+Z) that flies cards back to their previous positions, and a HINT
    /// (h) that pulses through every legal move (or the deck when none remain).
    public partial class Game1
    {
        // ---- tunables ----
        private const float UndoDur = 0.30f;   // how long an undo fly-back takes
        private const float HintDur = 2.6f;    // how long a hint highlight lingers
        private const int UndoCap = 200;       // max snapshots kept

        // ---- undo ----
        private readonly Stack<GameBoard> _undoStack = new();

        private sealed class UndoAnim
        {
            public List<Card> Cards;
            public Vector2[] Start, End;
            public bool[] FaceUp;
            public float T;
        }
        private UndoAnim _undoAnim;

        // ---- hint ----
        private readonly struct HintMove
        {
            public readonly bool SrcWaste;
            public readonly int SrcCol, SrcIdx;
            public readonly bool TgtColumn;     // target is a column (else a slot)
            public readonly int TgtIndex;
            public HintMove(bool srcWaste, int srcCol, int srcIdx, bool tgtColumn, int tgtIndex)
            {
                SrcWaste = srcWaste; SrcCol = srcCol; SrcIdx = srcIdx;
                TgtColumn = tgtColumn; TgtIndex = tgtIndex;
            }
        }
        private List<HintMove> _hintMoves = new();
        private int _hintIdx = -1;
        private float _hintT;
        private bool _hintActive;
        private bool _hintDeck;

        private void ResetInteractions()
        {
            _undoStack.Clear();
            _undoAnim = null;
            _hintMoves.Clear();
            _hintIdx = -1;
            _hintActive = false;
            _hintDeck = false;
            ResetAccessibility();
            ResetWinState();
        }

        // ---------------------------------------------------------------- undo

        private void PushUndo(GameBoard snapshot)
        {
            _undoStack.Push(snapshot);
            while (_undoStack.Count > UndoCap)
            {
                // Drop the oldest by rebuilding (Stack has no bottom-pop); cheap, rare.
                var keep = _undoStack.ToArray();          // index 0 = newest
                _undoStack.Clear();
                for (int i = UndoCap - 1; i >= 0; i--) _undoStack.Push(keep[i]);
                break;
            }
            // A move invalidates the current hint cycle.
            _hintActive = false;
            _hintIdx = -1;
        }

        private void Undo()
        {
            if (_undoStack.Count == 0) return;
            PlaySound("deny"); // undo cue

            // Where every card rests right now (pre-undo).
            var from = new Dictionary<int, Rectangle>();
            CollectCardRects(_board, from, null);

            // Stop in-flight animations and restore the previous board.
            var prev = _undoStack.Pop();
            ClearTransientAnimations();
            _board = prev;
            ResetWinState();
            _hintActive = false;
            _hintIdx = -1;

            // Restored rest positions, then fly each moved card from its old spot.
            var to = new Dictionary<int, Rectangle>();
            var faceUp = new Dictionary<int, bool>();
            CollectCardRects(_board, to, faceUp);
            StartUndoAnim(from, to, faceUp);
        }

        /// Fill `rects` (and optionally `faceUps`) with the resting screen rectangle of
        /// every card on the given board, matching how the static renderer lays them out.
        private void CollectCardRects(GameBoard b, Dictionary<int, Rectangle> rects, Dictionary<int, bool> faceUps)
        {
            for (int col = 0; col < b.Columns.Count; col++)
            {
                var c = b.Columns[col];
                int y = ColTop(col);
                for (int i = 0; i < c.Count; i++)
                {
                    rects[c[i].Id] = new Rectangle(Layout.ColX(col), y, Layout.CardW, Layout.CardH);
                    if (faceUps != null) faceUps[c[i].Id] = c[i].FaceUp;
                    y += c[i].FaceUp ? Layout.FaceUpOffset : Layout.FaceDownOffset;
                }
            }
            for (int s = 0; s < b.Slots.Count; s++)
            {
                var r = SlotRect(s);
                foreach (var card in b.Slots[s].Cards)
                {
                    rects[card.Id] = r;
                    if (faceUps != null) faceUps[card.Id] = true;
                }
            }
            int n = b.Waste.Count;
            for (int i = 0; i < n; i++)
            {
                int d = Math.Min((n - 1) - i, 2);
                var card = b.Waste[i];
                rects[card.Id] = new Rectangle(Layout.WasteX + d * Layout.WastePeek, Layout.WasteY, Layout.CardW, Layout.CardH);
                if (faceUps != null) faceUps[card.Id] = true;
            }
            var sr = StockRect();
            foreach (var card in b.Stock)
            {
                rects[card.Id] = sr;
                if (faceUps != null) faceUps[card.Id] = false;
            }
        }

        private void StartUndoAnim(Dictionary<int, Rectangle> from, Dictionary<int, Rectangle> to, Dictionary<int, bool> faceUp)
        {
            // Cards that vanished from the current board (e.g. a completed set that flew
            // off) fly back in from the bottom-left where the completion sent them.
            var offscreen = new Rectangle(-Layout.CardW, Layout.ScreenH - Layout.CardH, Layout.CardW, Layout.CardH);

            var cards = new List<Card>();
            var starts = new List<Vector2>();
            var ends = new List<Vector2>();
            var ups = new List<bool>();

            foreach (var col in _board.Columns)
                AddUndoMovers(col, from, to, faceUp, offscreen, cards, starts, ends, ups);
            foreach (var s in _board.Slots)
                AddUndoMovers(s.Cards, from, to, faceUp, offscreen, cards, starts, ends, ups);
            AddUndoMovers(_board.Waste, from, to, faceUp, offscreen, cards, starts, ends, ups);
            AddUndoMovers(_board.Stock, from, to, faceUp, offscreen, cards, starts, ends, ups);

            if (cards.Count == 0) { _undoAnim = null; return; }

            _undoAnim = new UndoAnim
            {
                Cards = cards,
                Start = starts.ToArray(),
                End = ends.ToArray(),
                FaceUp = ups.ToArray(),
                T = 0f
            };
            foreach (var c in cards) _animCardIds.Add(c.Id);
        }

        private void AddUndoMovers(List<Card> group, Dictionary<int, Rectangle> from, Dictionary<int, Rectangle> to,
                                   Dictionary<int, bool> faceUp, Rectangle offscreen,
                                   List<Card> cards, List<Vector2> starts, List<Vector2> ends, List<bool> ups)
        {
            foreach (var card in group)
            {
                if (!to.TryGetValue(card.Id, out var endR)) continue;
                Rectangle startR = from.TryGetValue(card.Id, out var fr) ? fr : offscreen;
                if (startR == endR) continue; // didn't move -> no animation
                cards.Add(card);
                starts.Add(new Vector2(startR.X, startR.Y));
                ends.Add(new Vector2(endR.X, endR.Y));
                ups.Add(faceUp.TryGetValue(card.Id, out var up) && up);
            }
        }

        private void UpdateUndoAnim(float dt)
        {
            if (_undoAnim == null) return;
            _undoAnim.T += dt;
            if (_undoAnim.T >= UndoDur)
            {
                foreach (var c in _undoAnim.Cards) _animCardIds.Remove(c.Id);
                _undoAnim = null;
            }
        }

        private void DrawUndoAnim()
        {
            if (_undoAnim == null) return;
            float e = EaseOutCubic(_undoAnim.T / UndoDur);
            for (int i = 0; i < _undoAnim.Cards.Count; i++)
            {
                var p = Vector2.Lerp(_undoAnim.Start[i], _undoAnim.End[i], e);
                var r = new Rectangle((int)Math.Round(p.X), (int)Math.Round(p.Y), Layout.CardW, Layout.CardH);
                if (_undoAnim.FaceUp[i]) DrawCardFace(r, _undoAnim.Cards[i], covered: false);
                else DrawCardBack(r);
            }
        }

        // ---------------------------------------------------------------- hint

        private void ShowHint()
        {
            if (_board.State != GameState.Playing) return;
            if (_undoAnim != null || _recycle != null) return; // wait for transient anims

            var moves = EnumerateMoves();
            _hintMoves = moves;
            _hintT = 0f;
            _hintActive = true;
            PlaySound("highlight");
            if (moves.Count == 0) { _hintDeck = true; _hintIdx = -1; Speak(HintNarration(), true, true); return; }
            _hintDeck = false;
            _hintIdx = (_hintIdx + 1) % moves.Count;
            Speak(HintNarration(), true, true);
        }

        /// Every legal move on the current board, in a stable order so `h` cycles through
        /// them deterministically.
        private List<HintMove> EnumerateMoves()
        {
            var list = new List<HintMove>();
            var b = _board;
            if (b.State != GameState.Playing) return list;

            for (int col = 0; col < b.Columns.Count; col++)
            {
                int start = b.FaceUpRunStart(col);
                if (start < 0) continue;
                for (int idx = start; idx < b.Columns[col].Count; idx++)
                {
                    if (!b.IsValidTail(col, idx)) continue;
                    int tailLen = b.Columns[col].Count - idx;
                    for (int s = 0; s < b.Slots.Count; s++)
                        if (b.CanMoveColumnTailToSlot(col, idx, s)) list.Add(new HintMove(false, col, idx, false, s));
                    for (int t = 0; t < b.Columns.Count; t++)
                    {
                        if (!b.CanMoveColumnTailToColumn(col, idx, t)) continue;
                        // Skip the no-op of dropping a whole column onto an empty one.
                        if (b.Columns[t].Count == 0 && b.Columns[col].Count <= tailLen) continue;
                        list.Add(new HintMove(false, col, idx, true, t));
                    }
                }
            }
            if (b.Waste.Count > 0)
            {
                for (int s = 0; s < b.Slots.Count; s++)
                    if (b.CanMoveWasteToSlot(s)) list.Add(new HintMove(true, -1, -1, false, s));
                for (int t = 0; t < b.Columns.Count; t++)
                    if (b.CanMoveWasteToColumn(t)) list.Add(new HintMove(true, -1, -1, true, t));
            }
            return list;
        }

        private void UpdateHint(float dt)
        {
            if (!_hintActive) return;
            _hintT += dt;
            if (_hintT >= HintDur) _hintActive = false;
        }

        private void DrawHint()
        {
            if (!_hintActive || _board.State != GameState.Playing) return;

            float pulse = 0.5f + 0.5f * (float)Math.Sin(_hintT * 6.5f);
            float fade = Clamp01f((HintDur - _hintT) / 0.45f);
            if (fade <= 0f) return;

            if (_hintDeck) { HighlightRect(StockRect(), pulse, fade, fill: true); return; }
            if (_hintIdx < 0 || _hintIdx >= _hintMoves.Count) return;

            var m = _hintMoves[_hintIdx];

            // Source — highlighted with the same light-yellow overlay as the destination.
            if (m.SrcWaste)
            {
                HighlightRect(WasteRect(), pulse, fade, fill: true);
            }
            else
            {
                int[] ys = ColumnCardYs(m.SrcCol);
                int top = ys[m.SrcIdx];
                int bottom = ys[^1] + Layout.CardH;
                HighlightRect(new Rectangle(Layout.ColX(m.SrcCol), top, Layout.CardW, bottom - top), pulse, fade, fill: true);
            }

            // Target.
            Rectangle tgt;
            if (m.TgtColumn)
            {
                var c = _board.Columns[m.TgtIndex];
                int ty = c.Count == 0 ? ColTop(m.TgtIndex) : ColumnCardYs(m.TgtIndex)[^1];
                tgt = new Rectangle(Layout.ColX(m.TgtIndex), ty, Layout.CardW, Layout.CardH);
            }
            else tgt = SlotRect(m.TgtIndex);
            HighlightRect(tgt, pulse, fade, fill: true);
        }

        /// A pulsing gold highlight: a soft outer glow ring plus a brighter inner ring, over a
        /// faint light-yellow fill so both the source card and the destination read clearly.
        private void HighlightRect(Rectangle r, float pulse, float fade, bool fill)
        {
            int glow = (int)((90 + 70 * pulse) * fade);
            int bright = (int)((150 + 105 * pulse) * fade);
            var outer = new Rectangle(r.X - Sc(4), r.Y - Sc(4), r.Width + Sc(8), r.Height + Sc(8));
            if (fill)
                DrawRoundFill(r, new Color(255, 216, 110, (int)(70 * fade)));
            DrawRoundRing(outer, new Color(255, 210, 90, glow));
            DrawRoundRing(r, new Color(255, 230, 150, bright));
        }
    }
}
