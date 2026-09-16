using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WordAssociationsSolitaire
{
    /// One word on a card: the word itself plus an optional emoji. When AsEmoji is true the
    /// card displays as JUST the emoji (no word) — sets are a mix of word and emoji cards.
    public sealed class WordEntry
    {
        public string Word { get; set; } = "";
        public string Emoji { get; set; } = "";
        public bool AsEmoji { get; set; }
        public string Definition { get; set; } = "";
    }

    /// A themed word-association set: a base (anchor) word + N related words. The set's
    /// SIZE is fixed by the data (Size == 1 + Related.Count); the dealer combines whole
    /// sets of various sizes to build a game.
    public sealed class WordSet
    {
        public string Name { get; set; } = "";
        public WordEntry Base { get; set; } = new();
        public List<WordEntry> Related { get; set; } = new();
        public int Size => 1 + Related.Count;

        /// Names of categories this set may NOT be dealt alongside: a word in this set
        /// could plausibly belong to the other category (or vice versa), so putting both
        /// in one game would make some card ambiguous. The relation is symmetric.
        public List<string> Conflicts { get; set; } = new();
    }

    /// Loads the themed word sets from the external words.json file (shipped next to the
    /// executable) and deals games from them. Every word across all sets is globally
    /// unique. The dealer selects WHOLE fixed-size sets that sum to the configured card
    /// count, so each game is a fresh mix of differently sized sets.
    public static class WordData
    {
        private static List<WordSet> _sets;
        private static Dictionary<string, string> _defs;
        private static Dictionary<string, HashSet<string>> _conflictMap;
        private static readonly object _loadLock = new();

        /// The file (beside the exe) that holds all word sets. Override via env var for tests.
        public static string DataFilePath =>
            Environment.GetEnvironmentVariable("WORDS_JSON")
            ?? Path.Combine(AppContext.BaseDirectory, "words.json");

        public static IReadOnlyList<WordSet> Sets
        {
            get { EnsureLoaded(); return _sets; }
        }

        public static int DefinitionCount => Sets.Count;

        /// The definition of a card's word (empty string if none is known). Every word is
        /// globally unique, so a card's word alone identifies its definition.
        public static string DefinitionFor(string word)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(word)) return "";
            if (_defs == null)
            {
                lock (_loadLock)
                {
                    if (_defs == null)
                    {
                        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var s in _sets)
                        {
                            if (!string.IsNullOrEmpty(s.Base.Word)) m[s.Base.Word] = s.Base.Definition;
                            foreach (var r in s.Related)
                                if (!string.IsNullOrEmpty(r.Word)) m[r.Word] = r.Definition;
                        }
                        _defs = m;
                    }
                }
            }
            return _defs.TryGetValue(word, out var d) ? d : "";
        }

        /// True if the two named categories may NOT appear in the same game (a word in one
        /// could plausibly belong to the other). Symmetric.
        public static bool AreConflicting(string nameA, string nameB)
        {
            EnsureLoaded();
            EnsureConflictMap();
            return _conflictMap.TryGetValue(nameA ?? "", out var set) && set.Contains(nameB ?? "");
        }

        private static void EnsureConflictMap()
        {
            if (_conflictMap != null) return;
            lock (_loadLock)
            {
                if (_conflictMap != null) return;
                var m = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in _sets)
                {
                    if (!m.TryGetValue(s.Name, out var set))
                        m[s.Name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in s.Conflicts)
                    {
                        set.Add(c);
                        // enforce symmetry even if the data only listed one direction
                        if (!m.TryGetValue(c, out var back))
                            m[c] = back = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        back.Add(s.Name);
                    }
                }
                _conflictMap = m;
            }
        }

        /// Largest set size present in the data.
        public static int MaxSupportedSetSize => Sets.Count == 0 ? 0 : Sets.Max(s => s.Size);
        /// Smallest set size present in the data.
        public static int MinSupportedSetSize => Sets.Count == 0 ? 0 : Sets.Min(s => s.Size);

        // --------------------------------------------------------------- loading

        private sealed class FileDto
        {
            [JsonPropertyName("sets")] public List<SetDto> Sets { get; set; } = new();
        }
        private sealed class SetDto
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("base")] public WordEntry Base { get; set; } = new();
            [JsonPropertyName("related")] public List<WordEntry> Related { get; set; } = new();
            [JsonPropertyName("conflicts")] public List<string> Conflicts { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static void EnsureLoaded()
        {
            if (_sets != null) return;
            lock (_loadLock)
            {
                if (_sets != null) return;
                _sets = LoadFrom(DataFilePath);
            }
        }

        /// Force a (re)load from a specific file — used by tooling / tests.
        public static void Load(string path)
        {
            lock (_loadLock) { _sets = LoadFrom(path); }
        }

        private static List<WordSet> LoadFrom(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Word data file not found: {path}. It should be copied next to the executable " +
                    "(see WordAssociationsSolitaire.csproj <Content Include=\"words.json\">).", path);

            var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), JsonOpts)
                      ?? new FileDto();
            var list = new List<WordSet>(dto.Sets.Count);
            foreach (var s in dto.Sets)
            {
                var set = new WordSet
                {
                    Name = s.Name,
                    Base = new WordEntry { Word = s.Base.Word, Emoji = s.Base.Emoji ?? "", AsEmoji = s.Base.AsEmoji, Definition = s.Base.Definition ?? "" },
                    Related = s.Related.Select(r => new WordEntry
                    {
                        Word = r.Word, Emoji = r.Emoji ?? "", AsEmoji = r.AsEmoji, Definition = r.Definition ?? ""
                    }).ToList(),
                    Conflicts = (s.Conflicts ?? new List<string>()).ToList(),
                };
                list.Add(set);
            }
            _defs = null; // rebuilt lazily from the freshly loaded sets
            _conflictMap = null; // rebuilt lazily from the freshly loaded sets
            return list;
        }

        // ------------------------------------------------------------- new game

        /// Deal a new game. Prefers a varied random deal the greedy solver certifies
        /// winnable; otherwise falls back to a deal winnable BY CONSTRUCTION (DealWinnable),
        /// so NewGame is guaranteed to return a winnable board. The supplied config also
        /// becomes Config.Active so the layout matches the deal.
        public static GameBoard NewGame(Random rng = null, GameConfig cfg = null)
        {
            rng ??= new Random();
            cfg ??= Config.Active;
            Config.Active = cfg;
            EnsureLoaded();

            for (int attempt = 0; attempt < 600; attempt++)
            {
                var board = Deal(cfg, rng);
                if (board != null && Solver.IsWinnable(board)) return board;
            }
            for (int attempt = 0; attempt < 200; attempt++)
            {
                var built = DealWinnable(cfg, rng);
                if (built != null && Solver.IsWinnable(built)) return built;
                var board = Deal(cfg, rng);
                if (board != null && Solver.IsWinnable(board)) return board;
            }
            return DealWinnable(cfg, rng) ?? Deal(cfg, rng);
        }

        // --------------------------------------------------------------- building

        /// Build a fresh set of categories and a matching deck by selecting WHOLE word sets
        /// whose sizes sum to cfg.TotalCards. The chosen sets (and the order of related
        /// words within each) vary per deal, so every game is different.
        public static (List<Category> categories, List<Card> deck) Build(GameConfig cfg, Random rng)
        {
            EnsureLoaded();
            int hi = Math.Min(cfg.MaxSetSize, MaxSupportedSetSize);
            int lo = Math.Max(1, Math.Min(cfg.MinSetSize, hi == 0 ? cfg.MinSetSize : hi));

            var chosen = SelectSetsSummingTo(cfg.TotalCards, lo, hi, rng)
                         ?? throw new InvalidOperationException(
                             $"Could not select word sets summing to {cfg.TotalCards} cards from the data.");
            return Materialize(chosen, rng);
        }

        /// Turn chosen word sets into categories + a flat deck (base first, then related in
        /// random order), assigning sequential card ids.
        private static (List<Category>, List<Card>) Materialize(List<WordSet> sets, Random rng)
        {
            var categories = new List<Category>();
            var deck = new List<Card>();
            int cardId = 0;
            foreach (var s in sets)
            {
                int catId = categories.Count;
                categories.Add(new Category(catId, s.Name, s.Base.Word, s.Size));
                deck.Add(new Card(cardId++, s.Base.Word, catId, isBase: true, s.Base.Emoji, s.Base.AsEmoji));
                var rel = s.Related.ToList();
                ShuffleList(rel, rng);
                foreach (var e in rel)
                    deck.Add(new Card(cardId++, e.Word, catId, isBase: false, e.Emoji, e.AsEmoji));
            }
            return (categories, deck);
        }

        /// Pick distinct whole sets (sizes within [lo, hi]) summing to exactly `total`,
        /// never choosing two categories that CONFLICT (see WordSet.Conflicts) and never
        /// leaving an illegal remainder (1..lo-1). Returns null if it can't.
        private static List<WordSet> SelectSetsSummingTo(int total, int lo, int hi, Random rng)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                var bySize = GroupBySizeShuffled(rng);
                var chosen = new List<WordSet>();
                var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int remaining = total;
                bool stuck = false;

                while (remaining > 0)
                {
                    // Sizes that still fit AND have at least one non-conflicting set left.
                    var cands = new List<int>();
                    foreach (var kv in bySize)
                    {
                        int s = kv.Key;
                        if (s < lo || s > hi || s > remaining) continue;
                        int rem = remaining - s;
                        if (!(rem == 0 || rem >= lo)) continue;
                        if (kv.Value.Any(ws => !blocked.Contains(ws.Name))) cands.Add(s);
                    }
                    if (cands.Count == 0) { stuck = true; break; }
                    int pick = cands[rng.Next(cands.Count)];
                    var ws2 = TakeNonConflicting(bySize[pick], blocked);
                    if (ws2 == null) { stuck = true; break; }
                    chosen.Add(ws2);
                    remaining -= pick;
                }
                if (!stuck && remaining == 0) return chosen;
            }
            return null;
        }

        /// Remove and return the first set in `list` whose name is not blocked, marking it and
        /// all of its conflicting categories as blocked so they can't be chosen afterwards.
        private static WordSet TakeNonConflicting(List<WordSet> list, HashSet<string> blocked)
        {
            int idx = list.FindIndex(ws => !blocked.Contains(ws.Name));
            if (idx < 0) return null;
            var ws = list[idx];
            list.RemoveAt(idx);
            blocked.Add(ws.Name);
            foreach (var c in ws.Conflicts) blocked.Add(c);
            return ws;
        }

        private static Dictionary<int, List<WordSet>> GroupBySizeShuffled(Random rng)
        {
            var res = new Dictionary<int, List<WordSet>>();
            foreach (var s in _sets)
            {
                if (!res.TryGetValue(s.Size, out var l)) { l = new List<WordSet>(); res[s.Size] = l; }
                l.Add(s);
            }
            foreach (var kv in res) ShuffleList(kv.Value, rng);
            return res;
        }

        // --------------------------------------------------------------- dealing

        private static GameBoard Deal(GameConfig cfg, Random rng)
        {
            var built = Build(cfg, rng);
            if (built.deck.Count != cfg.TotalCards) return null;
            var deck = built.deck;
            ShuffleList(deck, rng);

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

            return new GameBoard(built.categories, columns, stock, cfg.SlotCount, cfg.Moves, cfg.UnlimitedMoves);
        }

        // ----------------------------------------------------- guaranteed winnable

        /// Build a deal the greedy solver is GUARANTEED to win, honouring the config's column
        /// sizes and total card count so the layout still matches. Every column is filled with
        /// whole sets stacked back-to-front, each set ordered [related..., base] so the base
        /// sits at the FRONT (face-up) of its segment; the stock holds the remaining whole sets
        /// ordered [base, related...]. To win you peel each column / draw each set from the
        /// front: the exposed base claims a slot, its related cards are added, the set completes
        /// and frees the slot, the next base appears. At most one slot is ever open at a time.
        /// Returns null if the data lacks enough sets of the required sizes.
        public static GameBoard DealWinnable(GameConfig cfg, Random rng)
        {
            EnsureLoaded();
            int hi = Math.Min(cfg.MaxSetSize, MaxSupportedSetSize);
            int lo = Math.Max(1, Math.Min(cfg.MinSetSize, hi == 0 ? cfg.MinSetSize : hi));

            int[] colSizes = cfg.ColumnSizes;
            int colTotal = colSizes.Sum();
            int stockTotal = Math.Max(0, cfg.TotalCards - colTotal);

            // Decide the set sizes that exactly fill each column and the stock.
            var colSetSizes = new List<int>[colSizes.Length];
            for (int j = 0; j < colSizes.Length; j++)
                colSetSizes[j] = PartitionSizes(colSizes[j], lo, hi, rng);
            var stockSetSizes = PartitionSizes(stockTotal, lo, hi, rng);

            // Reserve actual unused sets of each requested size from one shared pool, never
            // taking two categories that conflict (so the whole deal is conflict-free).
            var bySize = GroupBySizeShuffled(rng);
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WordSet Take(int size)
            {
                return bySize.TryGetValue(size, out var list) ? TakeNonConflicting(list, blocked) : null;
            }

            var categories = new List<Category>();
            int cardId = 0;

            (Card baseCard, List<Card> related) MakeCat(WordSet s)
            {
                int catId = categories.Count;
                categories.Add(new Category(catId, s.Name, s.Base.Word, s.Size));
                var baseCard = new Card(cardId++, s.Base.Word, catId, isBase: true, s.Base.Emoji, s.Base.AsEmoji);
                var rel = s.Related.ToList();
                ShuffleList(rel, rng);
                var related = rel.Select(e => new Card(cardId++, e.Word, catId, isBase: false, e.Emoji, e.AsEmoji)).ToList();
                return (baseCard, related);
            }

            var columns = new List<List<Card>>();
            foreach (var seg in colSetSizes)
            {
                var col = new List<Card>();
                for (int i = seg.Count - 1; i >= 0; i--)
                {
                    var ws = Take(seg[i]);
                    if (ws == null) return null;
                    var (baseCard, related) = MakeCat(ws);
                    col.AddRange(related);
                    col.Add(baseCard);
                }
                columns.Add(col);
            }

            var drawOrder = new List<Card>();
            foreach (int size in stockSetSizes)
            {
                var ws = Take(size);
                if (ws == null) return null;
                var (baseCard, related) = MakeCat(ws);
                drawOrder.Add(baseCard);
                drawOrder.AddRange(related);
            }
            var stock = new List<Card>();
            for (int i = drawOrder.Count - 1; i >= 0; i--) stock.Add(drawOrder[i]);

            return new GameBoard(categories, columns, stock, cfg.SlotCount, cfg.Moves, cfg.UnlimitedMoves);
        }

        /// Split `total` into whole-set sizes, each within [lo, hi], summing to exactly total.
        private static List<int> PartitionSizes(int total, int lo, int hi, Random rng)
        {
            var parts = new List<int>();
            if (total <= 0) return parts;
            lo = Math.Max(1, lo);
            hi = Math.Max(lo, hi);

            int remaining = total;
            while (remaining > hi)
            {
                int maxP = Math.Min(hi, remaining - lo);
                if (maxP < lo) maxP = lo;
                int p = rng.Next(lo, maxP + 1);
                parts.Add(p);
                remaining -= p;
            }
            parts.Add(remaining);
            return parts;
        }

        // ----------------------------------------------------------------- checks

        /// Returns words that appear in more than one set / more than once (should be empty).
        public static List<string> DuplicateWords()
        {
            EnsureLoaded();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _sets)
            {
                Count(seen, s.Base.Word);
                foreach (var r in s.Related) Count(seen, r.Word);
            }
            return seen.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();

            static void Count(Dictionary<string, int> map, string w) =>
                map[w] = map.TryGetValue(w, out int c) ? c + 1 : 1;
        }

        /// Count of sets per size (for the data-distribution self-test).
        public static SortedDictionary<int, int> SizeHistogram()
        {
            EnsureLoaded();
            var h = new SortedDictionary<int, int>();
            foreach (var s in _sets) h[s.Size] = h.TryGetValue(s.Size, out int c) ? c + 1 : 1;
            return h;
        }

        private static void ShuffleList<T>(List<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
