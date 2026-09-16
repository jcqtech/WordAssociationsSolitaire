using System.Collections.Generic;

namespace WordAssociationsSolitaire
{
    public enum GameState { Playing, Won, Lost }

    /// A single playing card. A card belongs to exactly one category and lives in
    /// exactly one container (a column, the stock, the waste, or a slot) at a time.
    public sealed class Card
    {
        public int Id;
        public string Word;
        public string Emoji;  // optional emoji shown alongside / instead of the word ("" if none)
        public bool ShowEmoji; // if true the card displays as JUST the emoji (no word)
        public int CategoryId;
        public bool IsBase;   // only base cards may start (anchor) a slot
        public bool FaceUp;

        public Card(int id, string word, int categoryId, bool isBase, string emoji = "", bool showEmoji = false)
        {
            Id = id;
            Word = word;
            Emoji = emoji ?? "";
            ShowEmoji = showEmoji;
            CategoryId = categoryId;
            IsBase = isBase;
            FaceUp = false;
        }

        public Card Clone() => new Card(Id, Word, CategoryId, IsBase, Emoji, ShowEmoji) { FaceUp = FaceUp };
    }

    /// A word-association group. Size == number of cards (1 base + N related).
    public sealed class Category
    {
        public int Id;
        public string Name;
        public string BaseWord;
        public int Size;
        public int Placed;       // cards currently in this category's slot (0 if not started)
        public bool Completed;

        public Category(int id, string name, string baseWord, int size)
        {
            Id = id;
            Name = name;
            BaseWord = baseWord;
            Size = size;
        }

        public Category Clone() =>
            new Category(Id, Name, BaseWord, Size) { Placed = Placed, Completed = Completed };
    }

    /// A foundation slot at the top of the board. Empty until a base card claims it.
    public sealed class Slot
    {
        public int CategoryId = -1;
        public List<Card> Cards = new();
        public bool IsEmpty => CategoryId < 0;
    }
}
