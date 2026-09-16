using System.Collections.Generic;
using System.Text;

namespace WordAssociationsSolitaire
{
    /// Pure, side-effect-free Narrator text builders for board elements. Kept static (taking a
    /// GameBoard) so the exact spoken wording can be locked down by SelfTest without a running
    /// Game1 / graphics device. Game1's accessibility layer delegates to these.
    public static class Narrate
    {
        private static readonly string[] OrdinalWords =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight",
            "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
        };

        public static string Ordinal(int n) => (n >= 0 && n < OrdinalWords.Length) ? OrdinalWords[n] : n.ToString();
        public static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

        /// Title-case a stored UPPERCASE word ("EGYPT" -> "Egypt", "CARD CATALOG" -> "Card Catalog").
        public static string Word(string w)
        {
            if (string.IsNullOrEmpty(w)) return w;
            var parts = w.Split(' ');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
            return string.Join(' ', parts);
        }

        public static string Slot(GameBoard b, int i)
        {
            var s = b.Slots[i];
            string head = $"Foundation slot {Ordinal(i + 1)}.";
            if (s.IsEmpty) return head + " Empty.";
            var cat = b.Categories[s.CategoryId];
            return $"{head} Base word: {Word(cat.BaseWord)}. Top word {Word(s.Cards[^1].Word)}. " +
                   $"{s.Cards.Count} out of {cat.Size}.";
        }

        public static string Stack(GameBoard b, int c)
        {
            var col = b.Columns[c];
            string head = $"Card stack {Ordinal(c + 1)}.";
            if (col.Count == 0) return head + " Empty.";

            int down = 0, up = 0;
            var faceUp = new List<string>();
            Card baseCard = null;
            foreach (var card in col)
            {
                if (card.FaceUp)
                {
                    up++;
                    faceUp.Add(Word(card.Word));
                    if (card.IsBase) baseCard = card;
                }
                else down++;
            }

            var sb = new StringBuilder(head);
            sb.Append($" {Plural(down, "face down card")}, {Plural(up, "face up card")}.");
            if (up == 1) sb.Append(" Face up card is " + faceUp[0] + ".");
            else if (up > 1) sb.Append(" Face up cards are " + string.Join(", ", faceUp) + ".");
            if (baseCard != null)
                sb.Append($" Base word: {Word(baseCard.Word)}. {b.Categories[baseCard.CategoryId].Size} cards expected.");
            return sb.ToString();
        }

        public static string DrawPile(GameBoard b)
        {
            if (b.Waste.Count == 0) return "Draw pile. Empty.";
            var top = b.Waste[^1];
            var sb = new StringBuilder("Draw pile. Top card: " + Word(top.Word));
            if (top.IsBase) sb.Append(", base word");
            sb.Append('.');
            var below = new List<string>();
            for (int i = b.Waste.Count - 2; i >= 0 && below.Count < 2; i--)
                below.Add(Word(b.Waste[i].Word));
            if (below.Count > 0) sb.Append(" Bottom two cards: " + string.Join(", ", below) + ".");
            return sb.ToString();
        }

        public static string Deck(GameBoard b)
        {
            int n = b.Stock.Count;
            return n == 0 ? "Deck. Empty." : $"Deck. {Plural(n, "card")}.";
        }

        public static string Menu(bool open) => "Menu. " + (open ? "Open." : "Hidden.");
        public static string Moves(GameBoard b) =>
            b.Unlimited ? "Unlimited moves." : $"{Plural(b.MovesLeft, "move")} left.";
        public static string Solved(GameBoard b) => $"Solved {b.ClearedCategories} of {b.TotalCategories} sets.";
        public static string Loss(GameBoard b) =>
            $"{(b.LossReason == LossReason.OutOfMoves ? "Out of moves." : "No legal moves remain.")} " +
            $"Solved {b.ClearedCategories} of {b.TotalCategories} sets. " +
            "Take back your last move to keep trying, or start a new game.";
    }
}
