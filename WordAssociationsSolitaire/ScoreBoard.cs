using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WordAssociationsSolitaire
{
    /// A single recorded win: how many moves were used and how long the game took.
    public sealed class ScoreEntry
    {
        public int MovesUsed { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int MovesLeft { get; set; } // Legacy field from the original, non-comparable scoring format.
        public double Seconds { get; set; }
        public string Date { get; set; } = "";
    }

    /// Persists the best five win scores to a JSON file in the user's local app-data folder,
    /// ranked by fewest moves used, then least time. Storage failures are reported to the debug
    /// output without breaking gameplay.
    public static class ScoreBoard
    {
        public const int MaxEntries = 5;

        public static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WordAssociationsSolitaire");
                return Path.Combine(dir, "scores.json");
            }
        }

        public static List<ScoreEntry> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<ScoreEntry>>(File.ReadAllText(FilePath));
                    if (list != null)
                    {
                        // Old scores stored only moves-left and cannot be normalized because their
                        // starting limit was configurable. Keep only comparable move-count scores.
                        int legacyCount = list.RemoveAll(e => e.MovesUsed <= 0);
                        if (legacyCount > 0)
                            Debug.WriteLine($"Ignored {legacyCount} legacy score entries with no moves-used value.");
                        return Sort(list);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Debug.WriteLine($"Could not load scores: {ex.Message}");
            }
            return new List<ScoreEntry>();
        }

        /// Add a new score, keep only the best five, save, and return the trimmed list. `rank` is
        /// the zero-based position of the new score in the returned list, or -1 if it didn't place.
        public static List<ScoreEntry> Record(int movesUsed, double seconds, out int rank)
        {
            var entry = new ScoreEntry
            {
                MovesUsed = movesUsed,
                Seconds = Math.Round(seconds, 1),
                Date = DateTime.Now.ToString("yyyy-MM-dd"),
            };
            var list = Load();
            list.Add(entry);
            list = Sort(list);
            if (list.Count > MaxEntries) list = list.Take(MaxEntries).ToList();
            rank = list.IndexOf(entry);
            Save(list);
            return list;
        }

        private static List<ScoreEntry> Sort(List<ScoreEntry> list) =>
            list.OrderBy(e => e.MovesUsed).ThenBy(e => e.Seconds).ToList();

        private static void Save(List<ScoreEntry> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not save scores: {ex.Message}");
            }
        }
    }
}
