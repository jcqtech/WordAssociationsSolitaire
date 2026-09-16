using System.Collections.Generic;

namespace WordAssociationsSolitaire
{
    /// Dev-only helper: builds a fixed board for visual tests. Showcases a
    /// foundation slot whose cards fully cover one another (top card gold-outlined
    /// with the progress number) alongside same-topic runs stacked in columns.
    /// Invoked via `dotnet run -- --demo`.
    public static class DemoScene
    {
        public static GameBoard Build()
        {            int id = 0;
            Card Mk(string w, int cat, bool b) => new Card(id++, w, cat, b);

            var cats = new List<Category>
            {
                new Category(0, "Ocean",  "OCEAN",  3),
                new Category(1, "Space",  "SPACE",  3),
                new Category(2, "Egypt",  "EGYPT",  3),
                new Category(3, "Forest", "FOREST", 2),
                new Category(4, "Music",  "MUSIC",  2),
            };

            // Ocean (slot demo)
            var ocean = Mk("OCEAN", 0, true);
            var shark = Mk("SHARK", 0, false);
            var coral = Mk("CORAL", 0, false);
            // Space (3-card column run)
            var space = Mk("SPACE", 1, true);
            var rocket = Mk("ROCKET", 1, false);
            var comet = Mk("COMET", 1, false);
            // Egypt
            var egypt = Mk("EGYPT", 2, true);
            var mummy = Mk("MUMMY", 2, false);
            var pyramid = Mk("PYRAMID", 2, false);
            // Forest
            var forest = Mk("FOREST", 3, true);
            var owl = Mk("OWL", 3, false);
            // Music
            var guitar = Mk("GUITAR", 4, false);
            var drum = Mk("DRUM", 4, false);

            var col0 = new List<Card> { owl, egypt, mummy };            // EGYPT(base)+MUMMY over hidden OWL
            var col1 = new List<Card> { guitar, space, rocket, comet }; // 3-card run over hidden GUITAR
            var col2 = new List<Card> { drum };
            var col3 = new List<Card> { forest };
            var col4 = new List<Card> { pyramid };
            var columns = new List<List<Card>> { col0, col1, col2, col3, col4 };

            var stock = new List<Card> { coral };

            var board = new GameBoard(cats, columns, stock, Config.SlotCount, Config.Moves);

            // Reveal the same-topic runs we want to showcase.
            egypt.FaceUp = true; mummy.FaceUp = true; owl.FaceUp = false;
            space.FaceUp = true; rocket.FaceUp = true; comet.FaceUp = true; guitar.FaceUp = false;

            // An in-progress slot: OCEAN(base) + SHARK -> top card SHARK, badge 2/3.
            ocean.FaceUp = true; shark.FaceUp = true;
            board.Slots[0].CategoryId = 0;
            board.Slots[0].Cards = new List<Card> { ocean, shark };
            cats[0].Placed = 2;

            return board;
        }
    }
}
