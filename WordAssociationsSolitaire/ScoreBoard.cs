using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WordAssociationsSolitaire
{
    /// A single recorded win: how many moves were left and how long the game took.
    public sealed class ScoreEntry
    {
        public int MovesLeft { get; set; }
        public double Seconds { get; set; }
        public string Date { get; set; } = "";
    }

    /// Persists the best five win scores to a JSON file in the user's local app-data folder,
    /// ranked by most moves remaining, then least time. Failures are swallowed so a bad/locked
    /// file never breaks the game.
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
                    if (list != null) return Sort(list);
                }
            }
            catch { /* ignore a missing/corrupt/locked file */ }
            return new List<ScoreEntry>();
        }

        /// Add a new score, keep only the best five, save, and return the trimmed list. `rank` is
        /// the zero-based position of the new score in the returned list, or -1 if it didn't place.
        public static List<ScoreEntry> Record(int movesLeft, double seconds, out int rank)
        {
            var entry = new ScoreEntry
            {
                MovesLeft = movesLeft,
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
            list.OrderByDescending(e => e.MovesLeft).ThenBy(e => e.Seconds).ToList();

        private static void Save(List<ScoreEntry> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* ignore write failures */ }
        }
    }
}
