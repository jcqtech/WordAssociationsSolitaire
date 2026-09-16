using System;
using System.Collections.Generic;
using System.Linq;

namespace WordAssociationsSolitaire
{
    /// Headless validation of the game logic (run with: dotnet run -- --selftest).
    public static class SelfTest
    {
        public static int Run()
        {
            var cfg = Config.Active; // default game shape
            Console.WriteLine("=== Word Associations Solitaire self-test ===");

            // 1) Every word across all sets must be globally unique.
            var dupes = WordData.DuplicateWords();
            Console.WriteLine($"Sets loaded: {WordData.DefinitionCount}, sizes {WordData.MinSupportedSetSize}-{WordData.MaxSupportedSetSize}, words {WordData.Sets.Sum(s => s.Size)}");
            Console.WriteLine(dupes.Count == 0
                ? "Word uniqueness: OK"
                : $"Word uniqueness: FAIL -> duplicates: {string.Join(", ", dupes)}");

            // 1b) Size distribution (target: 1000 sets, ~164-168 per size 3..8) + emoji coverage.
            var hist = WordData.SizeHistogram();
            Console.WriteLine($"Set size distribution: {string.Join(", ", hist.Select(kv => $"{kv.Key}:{kv.Value}"))}");
            int emojiCardCount = WordData.Sets.Sum(s => (s.Base.AsEmoji ? 1 : 0)
                                                 + s.Related.Count(r => r.AsEmoji));
            Console.WriteLine($"Emoji cards: {emojiCardCount} (most cards are single words now)");

            // 1c) Every NON-EMPTY emoji in the data must rasterize to a real (non-tofu) glyph.
            var distinctEmojis = new HashSet<string>();
            foreach (var s in WordData.Sets)
            {
                if (!string.IsNullOrEmpty(s.Base.Emoji)) distinctEmojis.Add(s.Base.Emoji);
                foreach (var r in s.Related)
                    if (!string.IsNullOrEmpty(r.Emoji)) distinctEmojis.Add(r.Emoji);
            }
            var tofuList = distinctEmojis.Where(e => !EmojiRenderer.CanRender(e)).ToList();
            Console.WriteLine($"Distinct emojis: {distinctEmojis.Count}, unrenderable (tofu): {tofuList.Count}"
                              + (tofuList.Count > 0 ? " -> " + string.Join(" ", tofuList.Take(20)) : ""));

            // 1d) Sets must be a MIX of word and emoji cards. The base MAY now be an emoji card.
            int emojiCards = 0, wordCards = 0;
            foreach (var s in WordData.Sets)
            {
                if (s.Base.AsEmoji) emojiCards++; else wordCards++;
                foreach (var r in s.Related)
                {
                    if (r.AsEmoji) emojiCards++;
                    else wordCards++;
                }
            }
            double emojiPct = 100.0 * emojiCards / (emojiCards + wordCards);
            Console.WriteLine($"Display mix: {emojiCards} emoji cards / {wordCards} word cards ({emojiPct:F0}% emoji)");

            // 1e) Every word must carry a definition (used by the Define tool).
            int noDef = 0; string firstNoDef = "";
            foreach (var s in WordData.Sets)
                foreach (var w in new[] { s.Base.Word }.Concat(s.Related.Select(r => r.Word)))
                    if (string.IsNullOrWhiteSpace(WordData.DefinitionFor(w))) { noDef++; if (firstNoDef == "") firstNoDef = w; }
            Console.WriteLine($"Word definitions: {(noDef == 0 ? "OK" : $"FAIL ({noDef} missing, e.g. {firstNoDef})")}");

            // 2) The deck dealt for the active config must total exactly TotalCards, built
            //    from whole sets (each set's size within the configured bounds).
            int badTotals = 0;
            var deckRng = new Random(7);
            for (int i = 0; i < 200; i++)
            {
                var (cats, deck) = WordData.Build(cfg, deckRng);
                if (deck.Count != cfg.TotalCards) badTotals++;
                foreach (var c in cats)
                    if (c.Size < cfg.MinSetSize || c.Size > cfg.MaxSetSize) badTotals++;
            }
            Console.WriteLine($"Deck-size / set-size correctness (of 200): {(badTotals == 0 ? "OK" : $"FAIL ({badTotals})")}");

            // 2b) No game may ever contain two CONFLICTING categories (a card would be
            //     ambiguous). Check both the random dealer and the constructive dealer.
            bool conflictFree = ConflictFreeDeals(cfg, out string conflictDetail);
            Console.WriteLine(conflictFree ? "Conflict-free deals: OK" : $"Conflict-free deals: FAIL -> {conflictDetail}");

            // 3) Greedy winnability on random deals (measures difficulty / budget head-room).
            const int n = 1000;
            int winnableFirstTry = 0, totalMovesUsed = 0, minMoves = int.MaxValue, maxMoves = 0;
            var rng = new Random(12345);
            for (int i = 0; i < n; i++)
            {
                var board = DealFrom(cfg, rng);
                if (Solver.IsWinnable(board, out int used))
                {
                    winnableFirstTry++;
                    totalMovesUsed += used;
                    minMoves = Math.Min(minMoves, used);
                    maxMoves = Math.Max(maxMoves, used);
                }
            }

            // 4) NewGame must always return a winnable board.
            int failures = 0;
            var ng = new Random(999);
            for (int i = 0; i < 100; i++)
                if (!Solver.IsWinnable(WordData.NewGame(ng, cfg.Clone()))) failures++;

            // 5) The constructive guaranteed-winnable dealer must ALWAYS be winnable.
            int builtFailures = 0, builtNull = 0, builtMin = int.MaxValue, builtMax = 0;
            var br = new Random(2024);
            const int builtN = 500;
            for (int i = 0; i < builtN; i++)
            {
                var b = WordData.DealWinnable(cfg, br);
                if (b == null) { builtNull++; continue; }
                if (Solver.IsWinnable(b, out int used))
                {
                    builtMin = Math.Min(builtMin, used);
                    builtMax = Math.Max(builtMax, used);
                }
                else builtFailures++;
            }
            if (builtMin == int.MaxValue) builtMin = 0;

            double pct = 100.0 * winnableFirstTry / n;
            double avg = winnableFirstTry > 0 ? (double)totalMovesUsed / winnableFirstTry : 0;
            if (minMoves == int.MaxValue) minMoves = 0;

            Console.WriteLine($"Config: {cfg.TotalCards} cards, sets {cfg.MinSetSize}-{cfg.MaxSetSize}, " +
                              $"slots {cfg.SlotCount}, columns [{string.Join(",", cfg.ColumnSizes)}], budget {cfg.Moves}");
            Console.WriteLine($"Random deals winnable on first try: {winnableFirstTry}/{n} ({pct:F1}%)");
            Console.WriteLine($"Greedy moves used  min/avg/max: {minMoves}/{avg:F1}/{maxMoves}");
            Console.WriteLine($"NewGame() winnability failures (of 100): {failures}");
            Console.WriteLine($"Constructed deals winnable (of {builtN}): {builtN - builtFailures - builtNull}/{builtN} " +
                              $"(null {builtNull}, moves {builtMin}-{builtMax})");

            // A constructive deal must reject, rather than violate, set-size constraints that
            // cannot exactly fill a configured segment (the default four-card column vs min 5).
            var incompatible = cfg.Clone();
            incompatible.MinSetSize = 5;
            incompatible.MaxSetSize = 8;
            bool partitionBoundsOk = WordData.DealWinnable(incompatible, new Random(17)) == null;
            Console.WriteLine(partitionBoundsOk
                ? "Constructive set-size bounds: OK"
                : "Constructive set-size bounds: FAIL");

            // 6) Base-stacking rule: a base may never be placed onto a non-empty column.
            bool ruleOk = BaseStackingRuleOk(out string ruleDetail);
            Console.WriteLine(ruleOk ? "Base-stacking rule: OK" : $"Base-stacking rule: FAIL -> {ruleDetail}");

            // 6a) Move accounting remains accurate in limited and unlimited modes.
            bool moveAccountingOk = MoveAccountingOk(out string moveAccountingDetail);
            Console.WriteLine(moveAccountingOk ? "Move accounting: OK" : $"Move accounting: FAIL -> {moveAccountingDetail}");

            // 6b) Dead-end detection: Lost when no card (board or deck) can be placed anywhere.
            bool deadEndOk = DeadEndDetectionOk(out string deadEndDetail);
            Console.WriteLine(deadEndOk ? "Dead-end detection: OK" : $"Dead-end detection: FAIL -> {deadEndDetail}");

            // 6c) Accessibility narration: the exact spoken wording for each board element.
            bool narrOk = AccessibilityNarrationOk(out string narrDetail);
            Console.WriteLine(narrOk ? "Accessibility narration: OK" : $"Accessibility narration: FAIL -> {narrDetail}");

            // 7) Data distribution: 1000 sets, evenly split across sizes 3..8, mostly single words.
            bool distOk = WordData.DefinitionCount == 1000 && tofuList.Count == 0
                          && emojiCards > 0 && wordCards > 0
                          && Enumerable.Range(3, 6).All(sz => hist.TryGetValue(sz, out int c) && c >= 160);
            Console.WriteLine(distOk ? "Data distribution: OK" : "Data distribution: FAIL");

            bool ok = dupes.Count == 0 && badTotals == 0 && failures == 0 && winnableFirstTry > 0
                      && builtFailures == 0 && ruleOk && deadEndOk && narrOk && distOk && noDef == 0
                      && conflictFree && partitionBoundsOk && moveAccountingOk;
            Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
            return ok ? 0 : 1;
        }

        /// Deals many games with both dealers and verifies that no game ever contains two
        /// categories that conflict (a shared/ambiguous word would otherwise be possible).
        private static bool ConflictFreeDeals(GameConfig cfg, out string detail)
        {
            detail = "";
            var rng = new Random(4242);
            for (int i = 0; i < 400; i++)
            {
                List<Category> cats;
                if (i % 2 == 0)
                {
                    cats = WordData.Build(cfg, rng).categories;
                }
                else
                {
                    var b = WordData.DealWinnable(cfg, rng);
                    if (b == null) continue;
                    cats = b.Categories;
                }
                for (int a = 0; a < cats.Count; a++)
                    for (int c = a + 1; c < cats.Count; c++)
                        if (WordData.AreConflicting(cats[a].Name, cats[c].Name))
                        {
                            detail = $"deal {i}: '{cats[a].Name}' + '{cats[c].Name}'";
                            return false;
                        }
            }
            return true;
        }

        /// Verifies the core base-stacking invariant: a base card may only ever be the
        /// first card of a column stack, never placed on top of another card.
        private static bool BaseStackingRuleOk(out string detail)
        {
            detail = "";
            int id = 0;
            // Two distinct categories (size 3 each).
            var cats = new List<Category>
            {
                new Category(0, "A", "ABASE", 3),
                new Category(1, "B", "BBASE", 3),
            };
            var aBase = new Card(id++, "ABASE", 0, isBase: true);
            var aR1 = new Card(id++, "A1", 0, isBase: false);
            var aR2 = new Card(id++, "A2", 0, isBase: false);
            var bBase = new Card(id++, "BBASE", 1, isBase: true);
            var bR1 = new Card(id++, "B1", 1, isBase: false);

            // col0=[aBase] (movable base), col1=[bBase] (non-empty), col2=[] (empty),
            // col3=[aR1] (movable non-base), col4=[aR2] (same-category, non-empty target).
            var columns = new List<List<Card>>
            {
                new List<Card> { aBase },
                new List<Card> { bBase },
                new List<Card>(),
                new List<Card> { aR1 },
                new List<Card> { aR2 },
            };
            var board = new GameBoard(cats, columns, new List<Card>(), 2, 50);

            string firstFail = "";
            bool Expect(bool actual, bool want, string what)
            {
                if (actual != want) { if (firstFail == "") firstFail = $"{what} expected {want} got {actual}"; return false; }
                return true;
            }

            bool pass = true;
            // A base may not move onto a non-empty column...
            pass &= Expect(board.CanMoveColumnTailToColumn(0, 0, 1), false, "base->non-empty column");
            // ...but may start a new (empty) column stack.
            pass &= Expect(board.CanMoveColumnTailToColumn(0, 0, 2), true, "base->empty column");
            // A non-base same-category run still stacks onto a matching front.
            pass &= Expect(board.CanMoveColumnTailToColumn(3, 0, 4), true, "non-base->same-category column");

            // Waste base may not land on a non-empty column, even a same-category one.
            board.Waste.Add(new Card(id++, "BBASE2", 1, isBase: true));
            pass &= Expect(board.CanMoveWasteToColumn(1), false, "waste base->non-empty column");
            pass &= Expect(board.CanMoveWasteToColumn(2), true, "waste base->empty column");
            board.Waste.Clear();
            // Waste non-base still stacks onto a same-category front.
            board.Waste.Add(new Card(id++, "B2", 1, isBase: false));
            pass &= Expect(board.CanMoveWasteToColumn(1), true, "waste non-base->same-category column");

            detail = firstFail;
            return pass;
        }

        private static bool MoveAccountingOk(out string detail)
        {
            detail = "";
            var cats = new List<Category> { new Category(0, "A", "ABASE", 2) };
            var columns = new List<List<Card>> { new List<Card>() };
            var stock = new List<Card>
            {
                new Card(0, "A1", 0, false),
                new Card(1, "ABASE", 0, true),
            };
            var board = new GameBoard(cats, columns, stock, 1, 3);

            if (!board.Draw() || board.MovesMade != 1 || board.MovesLeft != 2)
            {
                detail = $"limited draw produced made={board.MovesMade}, left={board.MovesLeft}";
                return false;
            }

            board.Unlimited = true;
            if (!board.Draw() || board.MovesMade != 2 || board.MovesLeft != 2)
            {
                detail = $"unlimited draw produced made={board.MovesMade}, left={board.MovesLeft}";
                return false;
            }

            var clone = board.Clone();
            if (clone.MovesMade != board.MovesMade)
            {
                detail = $"clone lost move count {board.MovesMade}->{clone.MovesMade}";
                return false;
            }
            return true;
        }

        /// Verifies dead-end detection: a game is Lost the instant no card on the board OR in
        /// the deck can be legally placed anywhere (a draw can never change that), even while the
        /// stock/waste still holds cards — and stays Playing whenever some placement remains.
        private static bool DeadEndDetectionOk(out string detail)
        {
            detail = "";

            // Single slot occupied by category A at 1/2 (its second card buried face-down under a
            // category-B card that can move nowhere); the stock holds only B's base, which can't
            // claim a slot (none free) nor stack on a column. With no empty column either, the
            // whole position is frozen: drawing forever can never make a card placeable.
            GameBoard Build(int slotCount, bool extraEmptyColumn)
            {
                var cats = new List<Category>
                {
                    new Category(0, "A", "ABASE", 2),
                    new Category(1, "B", "BBASE", 2),
                };
                var aBase = new Card(0, "ABASE", 0, isBase: true);
                var aR    = new Card(1, "A1", 0, isBase: false);
                var bBase = new Card(2, "BBASE", 1, isBase: true);
                var bR    = new Card(3, "B1", 1, isBase: false);
                var columns = new List<List<Card>> { new List<Card> { aR, bR } };
                if (extraEmptyColumn) columns.Add(new List<Card>());
                var board = new GameBoard(cats, columns, new List<Card> { bBase }, slotCount, 20);
                aBase.FaceUp = true;
                board.Slots[0].CategoryId = 0;
                board.Slots[0].Cards.Add(aBase);
                cats[0].Placed = 1;
                board.UpdateState();
                return board;
            }

            string firstFail = "";
            void Expect(GameState actual, GameState want, string what)
            {
                if (actual != want && firstFail == "") firstFail = $"{what} expected {want} got {actual}";
            }

            // True dead end -> Lost even though the stock still holds a card.
            var frozen = Build(1, false);
            Expect(frozen.State, GameState.Lost, "frozen board");
            if (frozen.LossReason != LossReason.NoLegalMoves && firstFail == "")
                firstFail = $"frozen board reason expected {LossReason.NoLegalMoves} got {frozen.LossReason}";
            string frozenText = Narrate.Loss(frozen);
            if (!frozenText.StartsWith("No legal moves remain.") && firstFail == "")
                firstFail = $"frozen board narration was \"{frozenText}\"";
            // A free slot lets B's base be played -> still Playing.
            Expect(Build(2, false).State, GameState.Playing, "free slot");
            // An empty column lets any card relocate -> still Playing.
            Expect(Build(1, true).State, GameState.Playing, "empty column");
            // Running out of moves is a loss regardless of placements.
            var budget = Build(2, false);
            budget.MovesLeft = 0;
            budget.UpdateState();
            Expect(budget.State, GameState.Lost, "no moves left");
            if (budget.LossReason != LossReason.OutOfMoves && firstFail == "")
                firstFail = $"budget reason expected {LossReason.OutOfMoves} got {budget.LossReason}";
            string budgetText = Narrate.Loss(budget);
            if (!budgetText.StartsWith("Out of moves.") && firstFail == "")
                firstFail = $"budget narration was \"{budgetText}\"";

            detail = firstFail;
            return firstFail == "";
        }

        /// Locks the exact Narrator wording for each board element (Narrate.*), so future edits
        /// can't silently change what a screen-reader user hears.
        private static bool AccessibilityNarrationOk(out string detail)
        {
            detail = "";
            var cats = new List<Category>
            {
                new Category(0, "A", "APPLE", 3),
                new Category(1, "B", "BREAD", 2),
            };
            // Column 0: [SEED (face down), BREAD base (face up), TOAST (face up)].
            var seed = new Card(0, "SEED", 1, false);
            var bread = new Card(1, "BREAD", 1, true);
            var toast = new Card(2, "TOAST", 1, false);
            var columns = new List<List<Card>> { new List<Card> { seed, bread, toast } };
            var stock = new List<Card>
            {
                new Card(3, "S1", 0, false), new Card(4, "S2", 0, false), new Card(5, "S3", 0, false),
            };
            var board = new GameBoard(cats, columns, stock, 2, 5);
            seed.FaceUp = false; bread.FaceUp = true; toast.FaceUp = true; // ctrl only exposes the front card

            // Slot 0: category A with APPLE (base) + RED APPLE = 2 of 3.
            board.Slots[0].CategoryId = 0;
            board.Slots[0].Cards.Add(new Card(6, "APPLE", 0, true) { FaceUp = true });
            board.Slots[0].Cards.Add(new Card(7, "RED APPLE", 0, false) { FaceUp = true });
            cats[0].Placed = 2;

            // Waste (bottom -> top): OLD TWO, OLD ONE, TOP HAT.
            board.Waste.Add(new Card(8, "OLD TWO", 0, false) { FaceUp = true });
            board.Waste.Add(new Card(9, "OLD ONE", 0, false) { FaceUp = true });
            board.Waste.Add(new Card(10, "TOP HAT", 0, false) { FaceUp = true });

            string firstFail = "";
            void Eq(string actual, string want, string what)
            {
                if (actual != want && firstFail == "") firstFail = $"{what}: got \"{actual}\" want \"{want}\"";
            }

            Eq(Narrate.Slot(board, 0), "Foundation slot one. Base word: Apple. Top word Red Apple. 2 out of 3.", "slot");
            Eq(Narrate.Slot(board, 1), "Foundation slot two. Empty.", "empty slot");
            Eq(Narrate.Stack(board, 0),
               "Card stack one. 1 face down card, 2 face up cards. Face up cards are Bread, Toast. Base word: Bread. 2 cards expected.",
               "stack");
            Eq(Narrate.DrawPile(board), "Draw pile. Top card: Top Hat. Bottom two cards: Old One, Old Two.", "draw pile");
            Eq(Narrate.Deck(board), "Deck. 3 cards.", "deck");
            Eq(Narrate.Moves(board), "5 moves left.", "moves");
            Eq(Narrate.Solved(board), "Solved 0 of 2 sets.", "solved");
            Eq(Narrate.Menu(false), "Menu. Hidden.", "menu");

            // Singular face-up wording: a stack with exactly one face-up card says "card is".
            var solo = new GameBoard(
                new List<Category> { new Category(0, "A", "APPLE", 3) },
                new List<List<Card>> { new List<Card> { new Card(0, "MOON", 0, false), new Card(1, "STAR", 0, false) } },
                new List<Card>(), 1, 5);
            Eq(Narrate.Stack(solo, 0), "Card stack one. 1 face down card, 1 face up card. Face up card is Star.", "singular stack");

            detail = firstFail;
            return firstFail == "";
        }

        private static GameBoard DealFrom(GameConfig cfg, Random rng)
        {
            var (categories, deck) = WordData.Build(cfg, rng);
            for (int i = deck.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }

            var columns = new List<List<Card>>();
            int p = 0;
            foreach (int sz in cfg.ColumnSizes)
            {
                var col = new List<Card>();
                for (int k = 0; k < sz && p < deck.Count; k++) col.Add(deck[p++]);
                columns.Add(col);
            }
            var stock = new List<Card>();
            for (; p < deck.Count; p++) stock.Add(deck[p]);

            return new GameBoard(categories, columns, stock, cfg.SlotCount, cfg.Moves);
        }
    }
}
