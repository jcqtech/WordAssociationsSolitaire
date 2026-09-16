using System;
using System.Linq;

namespace WordAssociationsSolitaire
{
    /// All tunable parameters for a single game. Construct one and pass it to
    /// WordData.NewGame (or assign Config.Active) to form games of different shapes
    /// and difficulty. Every number the design calls out is exposed here.
    public sealed class GameConfig
    {
        // --- Foundation -------------------------------------------------------
        public int SlotCount = 8;            // number of foundation slots (top row)

        // --- Tableau columns --------------------------------------------------
        public int ColumnCount = 7;          // number of columns
        public int SmallestColumnSize = 4;   // cards in the smallest column
        public int ColumnSizeStep = 1;       // each successive column grows by this many
        public bool SmallestColumnOnLeft = true;

        // --- Deck -------------------------------------------------------------
        public int TotalCards = 100;         // total cards dealt across columns + stock
        public int MinSetSize = 3;           // cards per word-association set (inclusive)
        public int MaxSetSize = 8;           // cards per word-association set (inclusive)

        // --- Difficulty -------------------------------------------------------
        public int Moves = 280;              // move budget; every move / draw costs one
        public bool UnlimitedMoves = false;  // when true the move budget is ignored (no move-based loss)

        /// Column sizes laid out left -> right. Smallest end is placed on the left
        /// when SmallestColumnOnLeft is true (the default), otherwise on the right.
        public int[] ColumnSizes
        {
            get
            {
                var sizes = new int[ColumnCount];
                for (int i = 0; i < ColumnCount; i++)
                    sizes[i] = SmallestColumnSize + i * ColumnSizeStep;
                if (!SmallestColumnOnLeft) Array.Reverse(sizes);
                return sizes;
            }
        }

        /// Total number of cards that start in the tableau columns.
        public int ColumnCardTotal => ColumnSizes.Sum();

        public GameConfig Clone() => (GameConfig)MemberwiseClone();
    }

    /// Global active configuration. The static layout and deal helpers read from
    /// here, so a game's parameters can be swapped wholesale before a deal:
    ///   Config.Active = new GameConfig { TotalCards = 60, ColumnCount = 4, ... };
    public static class Config
    {
        public static GameConfig Active = new GameConfig();

        // Convenience accessors so existing Config.X references keep working.
        public static int SlotCount => Active.SlotCount;
        public static int[] ColumnSizes => Active.ColumnSizes;
        public static int Moves => Active.Moves;
    }
}
