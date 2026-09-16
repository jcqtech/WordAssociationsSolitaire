using System;
using System.IO;
using System.Text.Json;

namespace WordAssociationsSolitaire
{
    /// Persisted player preferences (move budget, unlimited-moves toggle, sound volume), stored
    /// as JSON in the user's local app-data folder next to the high-score file. Failures are
    /// swallowed so a missing/locked/corrupt file never breaks the game.
    public sealed class UserSettings
    {
        public int MoveLimit { get; set; } = 280;
        public bool UnlimitedMoves { get; set; } = false;
        public float SoundVolume { get; set; } = 0.3f;

        public static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WordAssociationsSolitaire");
                return Path.Combine(dir, "settings.json");
            }
        }

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { /* ignore a missing/corrupt/locked file */ }
            return new UserSettings();
        }

        public static void Save(UserSettings s)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* ignore write failures */ }
        }
    }
}
