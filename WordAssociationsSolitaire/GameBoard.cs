using System;
using System.Collections.Generic;
using System.Linq;

namespace WordAssociationsSolitaire
{
    /// Pure game logic for Word Associations Solitaire (no rendering / input).
    ///
    /// Rules:
    ///  - Only base cards may claim an empty slot. Once claimed, only same-category
    ///    cards may be added to that slot. Filling a slot to its size clears it.
    ///  - Same-category cards may be stacked together in the tableau columns. Any
    ///    contiguous tail of a column's (same-category) face-up run may be moved as a
    ///    unit to a slot or onto another column (same-category front, or empty column).
    ///  - A base card may NEVER be stacked on top of another card in a column; it can
    ///    only ever be the first (bottom) card of a column stack. So a tail containing
    ///    a base may only move onto an EMPTY column, and only when the base is the
    ///    tail's own bottom card. Bases still go to foundation slots freely.
    ///  - The stock can be drawn into the waste; when the stock is empty a draw instead
    ///    recycles the whole waste pile face-down back into the stock (no card is dealt),
    ///    leaving the deck fully overturned and the waste empty.
    ///  - Every successful move (to a slot, to a column, or a draw) costs one move.
    ///    Clear every category to win. You lose if you run out of moves, or if you reach a
    ///    dead end where no card left on the board OR in the deck can be legally placed
    ///    anywhere (drawing can never change that, so the game can no longer be finished).
    public sealed class GameBoard
    {
        public List<Category> Categories;
        public List<List<Card>> Columns; // each column: index 0 = back, last = exposed/front
        public List<Slot> Slots;
        public List<Card> Stock; // top = last (face down)
        public List<Card> Waste; // top = last (face up, only top is movable)

        public int MovesLeft;
        public int InitialMoves;
        public int MovesMade;
        /// When true the move budget is ignored: moves are never spent and the game can never
        /// be lost for running out of moves (a true dead-end can still end it). The UI shows an
        /// infinity marker in place of the counter.
        public bool Unlimited;
        public GameState State = GameState.Playing;
        public LossReason LossReason = LossReason.None;
        public int ClearedCategories;

        /// Set the moment a category is completed (its slot filled to size), so the
        /// renderer can play a celebratory shine + fly-off before the slot frees. The
        /// UI consumes (reads then nulls) this; pure game logic ignores it.
        public CompletionEvent LastCompletion;

        public int TotalCategories => Categories.Count;

        public GameBoard(List<Category> categories, List<List<Card>> columns, List<Card> stock,
                         int slotCount, int moves, bool unlimited = false)
        {
            Categories = categories;
            Columns = columns;
            Stock = stock;
            Waste = new List<Card>();
            Slots = new List<Slot>();
            for (int i = 0; i < slotCount; i++) Slots.Add(new Slot());

            MovesLeft = moves;
            InitialMoves = moves;
            MovesMade = 0;
            Unlimited = unlimited;

            foreach (var col in Columns)
            {
                foreach (var c in col) c.FaceUp = false;
                if (col.Count > 0) col[^1].FaceUp = true; // expose the front card
            }
            foreach (var c in Stock) c.FaceUp = false;

            UpdateState();
        }

        private Category Cat(int id) => Categories[id];

        // ----------------------------------------------------------------- tails

        /// Index where the contiguous face-up run at the front of a column begins,
        /// or -1 if the column is empty / its front card is face down.
        public int FaceUpRunStart(int col)
        {
            var c = Columns[col];
            int i = c.Count - 1;
            if (i < 0 || !c[i].FaceUp) return -1;
            while (i - 1 >= 0 && c[i - 1].FaceUp) i--;
            return i;
        }

        /// A valid movable tail is a contiguous, face-up, single-category run reaching
        /// the front of the column.
        public bool IsValidTail(int col, int idx)
        {
            var c = Columns[col];
            if (idx < 0 || idx >= c.Count || !c[idx].FaceUp) return false;
            int catId = c[idx].CategoryId;
            for (int i = idx; i < c.Count; i++)
                if (!c[i].FaceUp || c[i].CategoryId != catId) return false;
            return true;
        }

        private List<Card> Tail(int col, int idx) => Columns[col].GetRange(idx, Columns[col].Count - idx);

        /// True if any card ABOVE the tail's bottom card (indices idx+1..front) is a
        /// base. Such a base could never remain "first in the stack", so the tail may
        /// not be placed in a column at all (not even an empty one).
        private bool TailHasBaseAboveBottom(int col, int idx)
        {
            var c = Columns[col];
            for (int i = idx + 1; i < c.Count; i++)
                if (c[i].IsBase) return true;
            return false;
        }

        /// True if any card in the tail (indices idx..front) is a base.
        private bool TailHasAnyBase(int col, int idx)
        {
            var c = Columns[col];
            for (int i = idx; i < c.Count; i++)
                if (c[i].IsBase) return true;
            return false;
        }

        // ----------------------------------------------------------------- slots

        private bool CanPlaceCardsToSlot(IReadOnlyList<Card> cards, int slot)
        {
            if (cards.Count == 0) return false;
            int catId = cards[0].CategoryId;
            for (int i = 1; i < cards.Count; i++)
                if (cards[i].CategoryId != catId) return false;

            var s = Slots[slot];
            var cat = Cat(catId);

            if (s.IsEmpty)
            {
                if (!cards.Any(c => c.IsBase)) return false; // must start with a base card
                return cards.Count <= cat.Size;
            }

            if (s.CategoryId != catId) return false;
            return s.Cards.Count + cards.Count <= cat.Size;
        }

        private void DoPlaceToSlot(List<Card> cards, int slot)
        {
            var s = Slots[slot];
            var cat = Cat(cards[0].CategoryId);
            if (s.IsEmpty) s.CategoryId = cat.Id;

            foreach (var c in cards)
            {
                c.FaceUp = true;
                s.Cards.Add(c);
            }
            cat.Placed = s.Cards.Count;

            if (s.Cards.Count >= cat.Size)
            {
                cat.Completed = true;
                cat.Placed = cat.Size;
                LastCompletion = new CompletionEvent
                {
                    Slot = slot,
                    CategoryId = cat.Id,
                    Cards = new List<Card>(s.Cards)
                };
                s.Cards.Clear();
                s.CategoryId = -1;
                ClearedCategories++;
            }
        }

        // -------------------------------------------------------------- API moves

        public bool CanMoveColumnTailToSlot(int col, int idx, int slot)
        {
            if (State != GameState.Playing || (!Unlimited && MovesLeft <= 0)) return false;
            if (!IsValidTail(col, idx)) return false;
            return CanPlaceCardsToSlot(Tail(col, idx), slot);
        }

        public bool MoveColumnTailToSlot(int col, int idx, int slot)
        {
            if (!CanMoveColumnTailToSlot(col, idx, slot)) return false;
            var cards = Tail(col, idx);
            Columns[col].RemoveRange(idx, Columns[col].Count - idx);
            FlipExposed(col);
            DoPlaceToSlot(cards, slot);
            SpendAndUpdate();
            return true;
        }

        public bool CanMoveColumnTailToColumn(int col, int idx, int toCol)
        {
            if (State != GameState.Playing || (!Unlimited && MovesLeft <= 0)) return false;
            if (col == toCol) return false;
            if (!IsValidTail(col, idx)) return false;
            var tgt = Columns[toCol];
            if (tgt.Count == 0)
                // Empty column: allowed, but a base may only be the tail's bottom card
                // (so it lands as the new stack's first card, never atop another card).
                return !TailHasBaseAboveBottom(col, idx);
            // Onto a non-empty column the whole tail sits atop existing cards, so it may
            // not contain a base at all; the fronts must also share a category.
            if (TailHasAnyBase(col, idx)) return false;
            var front = tgt[^1];
            return front.FaceUp && front.CategoryId == Columns[col][idx].CategoryId;
        }

        public bool MoveColumnTailToColumn(int col, int idx, int toCol)
        {
            if (!CanMoveColumnTailToColumn(col, idx, toCol)) return false;
            var cards = Tail(col, idx);
            Columns[col].RemoveRange(idx, Columns[col].Count - idx);
            FlipExposed(col);
            foreach (var c in cards) { c.FaceUp = true; Columns[toCol].Add(c); }
            SpendAndUpdate();
            return true;
        }

        public bool CanMoveWasteToSlot(int slot)
        {
            if (State != GameState.Playing || (!Unlimited && MovesLeft <= 0) || Waste.Count == 0) return false;
            return CanPlaceCardsToSlot(new[] { Waste[^1] }, slot);
        }

        public bool MoveWasteToSlot(int slot)
        {
            if (!CanMoveWasteToSlot(slot)) return false;
            var c = Waste[^1];
            Waste.RemoveAt(Waste.Count - 1);
            DoPlaceToSlot(new List<Card> { c }, slot);
            SpendAndUpdate();
            return true;
        }

        public bool CanMoveWasteToColumn(int toCol)
        {
            if (State != GameState.Playing || (!Unlimited && MovesLeft <= 0) || Waste.Count == 0) return false;
            var card = Waste[^1];
            var tgt = Columns[toCol];
            if (tgt.Count == 0) return true; // empty column: base allowed as the first card
            if (card.IsBase) return false;   // a base may never be stacked on another card
            return tgt[^1].FaceUp && tgt[^1].CategoryId == card.CategoryId;
        }

        public bool MoveWasteToColumn(int toCol)
        {
            if (!CanMoveWasteToColumn(toCol)) return false;
            var c = Waste[^1];
            Waste.RemoveAt(Waste.Count - 1);
            c.FaceUp = true;
            Columns[toCol].Add(c);
            SpendAndUpdate();
            return true;
        }

        public bool CanDraw()
        {
            if (State != GameState.Playing || (!Unlimited && MovesLeft <= 0)) return false;
            return Stock.Count > 0 || Waste.Count > 0;
        }

        public bool Draw()
        {
            if (!CanDraw()) return false;
            if (Stock.Count == 0)
            {
                // Recycle: turn the whole waste pile face-down back into the stock and
                // leave it there. No card is dealt, so the deck ends up fully overturned
                // and the waste empty — the player must draw again to reveal a card.
                for (int i = Waste.Count - 1; i >= 0; i--)
                {
                    var c = Waste[i];
                    c.FaceUp = false;
                    Stock.Add(c);
                }
                Waste.Clear();
                SpendAndUpdate();
                return true;
            }
            var top = Stock[^1];
            Stock.RemoveAt(Stock.Count - 1);
            top.FaceUp = true;
            Waste.Add(top);
            SpendAndUpdate();
            return true;
        }

        private void FlipExposed(int col)
        {
            var c = Columns[col];
            if (c.Count > 0 && !c[^1].FaceUp) c[^1].FaceUp = true;
        }

        private void SpendAndUpdate()
        {
            MovesMade++;
            if (!Unlimited) MovesLeft--;
            UpdateState();
        }

        public void UpdateState()
        {
            if (ClearedCategories >= TotalCategories)
            {
                State = GameState.Won;
                LossReason = LossReason.None;
                return;
            }
            if (!Unlimited && MovesLeft <= 0)
            {
                State = GameState.Lost;
                LossReason = LossReason.OutOfMoves;
                return;
            }
            if (!HasAnyLegalMove())
            {
                State = GameState.Lost;
                LossReason = LossReason.NoLegalMoves;
                return;
            }
            State = GameState.Playing;
            LossReason = LossReason.None;
        }

        public bool HasAnyLegalMove()
        {
            if (!Unlimited && MovesLeft <= 0) return false;

            // (a) Any loose stock/waste card that could be legally placed (once cycled to the
            // waste top). A draw only reorders the stock/waste; it never changes the slots or
            // columns, so if NO remaining deck card can be placed right now, no amount of
            // drawing will ever make one placeable and the game is a dead end even though the
            // stock/waste isn't empty. Checked first because it is the cheap, common case.
            if (AnyDeckCardPlaceable()) return true;

            // (b) Any tableau tail move (column -> slot, or column -> another column)?
            for (int col = 0; col < Columns.Count; col++)
            {
                int start = FaceUpRunStart(col);
                if (start < 0) continue;
                for (int idx = start; idx < Columns[col].Count; idx++)
                {
                    if (!IsValidTail(col, idx)) continue;
                    var tail = Tail(col, idx);
                    for (int s = 0; s < Slots.Count; s++)
                        if (CanPlaceCardsToSlot(tail, s)) return true;
                    for (int t = 0; t < Columns.Count; t++)
                    {
                        if (t == col) continue;
                        var tgt = Columns[t];
                        if (tgt.Count == 0)
                        {
                            if (Columns[col].Count > tail.Count && !TailHasBaseAboveBottom(col, idx)) return true;
                        }
                        else if (tgt[^1].FaceUp && tgt[^1].CategoryId == tail[0].CategoryId
                                 && !TailHasAnyBase(col, idx)) return true;
                    }
                }
            }
            return false;
        }

        /// True if any card still sitting in the stock or waste could be legally placed onto
        /// some slot or column right now, as if it were the movable waste-top card. Because
        /// every stock/waste card can be brought to the waste top through draw/recycle cycles
        /// (which never touch the slots or columns), this is exactly "is there a deck card the
        /// player could ever play" — the key to spotting a game that can no longer be finished.
        private bool AnyDeckCardPlaceable()
        {
            if (Stock.Count == 0 && Waste.Count == 0) return false;

            // A lone card can always be dropped onto an empty column.
            for (int t = 0; t < Columns.Count; t++)
                if (Columns[t].Count == 0) return true;

            bool anyEmptySlot = false;
            for (int s = 0; s < Slots.Count; s++)
                if (Slots[s].IsEmpty) { anyEmptySlot = true; break; }

            bool Placeable(Card c)
            {
                // A base card can claim a fresh empty slot.
                if (c.IsBase && anyEmptySlot) return true;
                // Any card can join its own already-open, not-yet-full slot.
                for (int s = 0; s < Slots.Count; s++)
                {
                    var slot = Slots[s];
                    if (!slot.IsEmpty && slot.CategoryId == c.CategoryId
                        && slot.Cards.Count < Cat(c.CategoryId).Size) return true;
                }
                // A non-base card can land on a same-category, face-up column front.
                if (!c.IsBase)
                    for (int t = 0; t < Columns.Count; t++)
                    {
                        var col = Columns[t];
                        if (col.Count > 0 && col[^1].FaceUp && col[^1].CategoryId == c.CategoryId) return true;
                    }
                return false;
            }

            foreach (var c in Stock) if (Placeable(c)) return true;
            foreach (var c in Waste) if (Placeable(c)) return true;
            return false;
        }

        public GameBoard Clone()
        {
            var b = (GameBoard)MemberwiseClone();
            b.LastCompletion = null;
            b.Categories = Categories.Select(c => c.Clone()).ToList();
            b.Columns = Columns.Select(col => col.Select(c => c.Clone()).ToList()).ToList();
            b.Stock = Stock.Select(c => c.Clone()).ToList();
            b.Waste = Waste.Select(c => c.Clone()).ToList();
            b.Slots = Slots.Select(s => new Slot
            {
                CategoryId = s.CategoryId,
                Cards = s.Cards.Select(c => c.Clone()).ToList()
            }).ToList();
            return b;
        }
    }

    /// A just-completed category, captured so the renderer can animate the slot's
    /// cards shining and flying away before the slot is reused.
    public sealed class CompletionEvent
    {
        public int Slot;
        public int CategoryId;
        public List<Card> Cards; // bottom .. top, in placement order
    }
}
