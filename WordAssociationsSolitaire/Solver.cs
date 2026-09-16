using System.Collections.Generic;
using System.Linq;

namespace WordAssociationsSolitaire
{
    /// A greedy auto-player used to validate that a freshly dealt game is winnable
    /// within its move budget. If the greedy strategy can win, a human certainly can.
    public static class Solver
    {
        public static bool IsWinnable(GameBoard board, out int movesUsed)
        {
            var b = board.Clone();
            bool won = Greedy(b);
            movesUsed = b.InitialMoves - b.MovesLeft;
            return won;
        }

        public static bool IsWinnable(GameBoard board) => IsWinnable(board, out _);

        private static bool Greedy(GameBoard b)
        {
            int guard = 0;
            while (b.State == GameState.Playing && guard++ < 5000)
            {
                if (TryComplete(b)) continue;        // finish a category
                if (TryAddToExisting(b)) continue;   // progress an open category
                if (TryOpenBase(b)) continue;        // claim an empty slot with a base
                if (TryRevealFlip(b)) continue;      // shuffle a run to flip a face-down card
                if (b.CanDraw() && b.Draw()) continue;
                break;
            }
            return b.State == GameState.Won;
        }

        // Finish a category in a single move where possible.
        private static bool TryComplete(GameBoard b)
        {
            for (int col = 0; col < b.Columns.Count; col++)
            {
                int start = b.FaceUpRunStart(col);
                if (start < 0) continue;
                for (int idx = start; idx < b.Columns[col].Count; idx++)
                {
                    if (!b.IsValidTail(col, idx)) continue;
                    int len = b.Columns[col].Count - idx;
                    int catId = b.Columns[col][idx].CategoryId;
                    bool hasBase = false;
                    for (int i = idx; i < b.Columns[col].Count; i++)
                        if (b.Columns[col][i].IsBase) { hasBase = true; break; }
                    for (int s = 0; s < b.Slots.Count; s++)
                        if (CompletesSlot(b.Slots[s], b.Categories[catId], catId, len, hasBase)
                            && b.MoveColumnTailToSlot(col, idx, s))
                            return true;
                }
            }
            if (b.Waste.Count > 0)
            {
                var w = b.Waste[^1];
                for (int s = 0; s < b.Slots.Count; s++)
                    if (CompletesSlot(b.Slots[s], b.Categories[w.CategoryId], w.CategoryId, 1, w.IsBase)
                        && b.MoveWasteToSlot(s))
                        return true;
            }
            return false;
        }

        private static bool CompletesSlot(Slot s, Category cat, int catId, int len, bool tailHasBase)
        {
            if (s.IsEmpty) return tailHasBase && len == cat.Size;
            return s.CategoryId == catId && s.Cards.Count + len == cat.Size;
        }

        // Add a card / run to an already-open slot.
        private static bool TryAddToExisting(GameBoard b)
        {
            for (int col = 0; col < b.Columns.Count; col++)
            {
                int start = b.FaceUpRunStart(col);
                if (start < 0) continue;
                for (int idx = start; idx < b.Columns[col].Count; idx++)
                {
                    if (!b.IsValidTail(col, idx)) continue;
                    for (int s = 0; s < b.Slots.Count; s++)
                        if (!b.Slots[s].IsEmpty && b.MoveColumnTailToSlot(col, idx, s))
                            return true;
                }
            }
            if (b.Waste.Count > 0)
                for (int s = 0; s < b.Slots.Count; s++)
                    if (!b.Slots[s].IsEmpty && b.MoveWasteToSlot(s))
                        return true;
            return false;
        }

        // Claim an empty slot using an exposed base card (prefer larger runs).
        private static bool TryOpenBase(GameBoard b)
        {
            if (!b.Slots.Any(s => s.IsEmpty)) return false;
            int emptySlot = b.Slots.FindIndex(s => s.IsEmpty);

            for (int col = 0; col < b.Columns.Count; col++)
            {
                int start = b.FaceUpRunStart(col);
                if (start < 0) continue;
                for (int idx = start; idx < b.Columns[col].Count; idx++)
                {
                    if (!b.IsValidTail(col, idx)) continue;
                    bool hasBase = false;
                    for (int i = idx; i < b.Columns[col].Count; i++)
                        if (b.Columns[col][i].IsBase) { hasBase = true; break; }
                    if (hasBase && b.MoveColumnTailToSlot(col, idx, emptySlot)) return true;
                }
            }
            if (b.Waste.Count > 0 && b.Waste[^1].IsBase && b.MoveWasteToSlot(emptySlot))
                return true;
            return false;
        }

        // Move a full face-up run onto another column to flip a hidden card (real progress).
        private static bool TryRevealFlip(GameBoard b)
        {
            for (int col = 0; col < b.Columns.Count; col++)
            {
                int start = b.FaceUpRunStart(col);
                if (start <= 0) continue; // need a face-down card beneath the run to reveal
                if (!b.IsValidTail(col, start)) continue;
                for (int t = 0; t < b.Columns.Count; t++)
                {
                    if (t == col) continue;
                    if (b.Columns[t].Count == 0) continue; // don't waste an empty column with no reveal benefit elsewhere
                    if (b.CanMoveColumnTailToColumn(col, start, t) && b.MoveColumnTailToColumn(col, start, t))
                        return true;
                }
            }
            return false;
        }
    }
}
