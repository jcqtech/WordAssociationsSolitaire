using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;

namespace WordAssociationsSolitaire
{
    /// Narrator text builders, focus/hover geometry, and the on-screen focus/selection drawing
    /// for the accessibility layer.
    public partial class Game1
    {
        private static string Ordinal(int n) => Narrate.Ordinal(n);

        // ---------------------------------------------------------- element text

        private string SlotNarration(int i) => Narrate.Slot(_board, i);
        private string ColumnNarration(int c) => Narrate.Stack(_board, c);
        private string DrawPileNarration() => Narrate.DrawPile(_board);
        private string DeckNarration() => Narrate.Deck(_board);
        private string MenuNarration() => Narrate.Menu(_toolbarOpen);
        private string MovesNarration() => Narrate.Moves(_board);
        private string SolvedNarration() => Narrate.Solved(_board);

        private string HintNarration()
        {
            if (_hintDeck || _hintIdx < 0 || _hintIdx >= _hintMoves.Count)
                return "Hint: draw a card from the deck.";
            var m = _hintMoves[_hintIdx];
            string src = m.SrcWaste
                ? $"the draw pile card {Display(_board.Waste[^1].Word)}"
                : $"card stack {Ordinal(m.SrcCol + 1)} card {Display(_board.Columns[m.SrcCol][m.SrcIdx].Word)}";
            string tgt = m.TgtColumn
                ? $"card stack {Ordinal(m.TgtIndex + 1)}"
                : $"foundation slot {Ordinal(m.TgtIndex + 1)}";
            return $"Hint: move {src} to {tgt}.";
        }

        private string HelpNarration()
        {
            var sb = new StringBuilder("How to play.");
            foreach (var (head, body) in HelpItems) sb.Append($" {head}. {body}");
            return sb.ToString();
        }

        private string DefinitionNarration() => $"{_defineWord}. {_defineText}";

        private string WinNarration()
        {
            var sb = new StringBuilder(
                $"You win! Cleared all {_board.TotalCategories} sets with {_winMovesLeft} moves left in {FormatTime(_winSeconds)}.");
            if (_topScores.Count > 0)
            {
                sb.Append(" Best scores:");
                for (int i = 0; i < _topScores.Count; i++)
                    sb.Append($" Rank {i + 1}, {_topScores[i].MovesLeft} moves left, time {FormatTime(_topScores[i].Seconds)}.");
            }
            return sb.ToString();
        }

        private string LoseNarration() =>
            $"Out of moves. Solved {_board.ClearedCategories} of {_board.TotalCategories} sets. " +
            "Take back your last move to keep trying, or start a new game.";

        // ---------------------------------------------------------- focus labels + rects

        private string FocusLabel(FocusSection s, int i) => s switch
        {
            FocusSection.TopBar => i == 0 ? "New Game button." : SolvedNarration(),
            FocusSection.Foundations => SlotNarration(i),
            FocusSection.Columns => ColumnNarration(i),
            FocusSection.Deck => i == 0 ? DrawPileNarration() : DeckNarration(),
            FocusSection.BottomBar => BottomBarLabel(i),
            _ => "",
        };

        private string BottomBarLabel(int i)
        {
            if (i == 0) return MenuNarration();
            if (_toolbarOpen && i >= 1 && i <= ToolNames.Length) return $"{ToolNames[i - 1]} button.";
            return MovesNarration();
        }

        private Rectangle FocusRect(FocusSection s, int i)
        {
            switch (s)
            {
                case FocusSection.TopBar:
                    return i == 0 ? _newGameButton : SolvedPillRect();
                case FocusSection.Foundations:
                    return SlotRect(Math.Min(i, _board.Slots.Count - 1));
                case FocusSection.Columns:
                    return ColumnRect(Math.Min(i, _board.Columns.Count - 1));
                case FocusSection.Deck:
                    return i == 0 ? WasteRect() : StockRect();
                case FocusSection.BottomBar:
                    if (i == 0) return FabRect();
                    if (_toolbarOpen && i >= 1 && i <= ToolNames.Length) return OptPillRect(i - 1, _toolbarT);
                    return MovesRibbonRect();
            }
            return Rectangle.Empty;
        }

        private Rectangle SolvedPillRect()
        {
            string s = $"Solved  {_board.ClearedCategories}/{_board.TotalCategories}";
            int w = (int)(_font.MeasureString(s).X * (0.72f * S)) + Sc(28);
            return new Rectangle(Sc(24), Sc(18), w, Sc(34));
        }

        private Rectangle MovesRibbonRect()
        {
            int visible = Sc(156);
            return new Rectangle(Sc(26), Layout.ScreenH - visible, Sc(118), visible);
        }

        private Rectangle ColumnRect(int col)
        {
            var c = _board.Columns[col];
            if (c.Count == 0) return new Rectangle(Layout.ColX(col), ColTop(col), Layout.CardW, Layout.CardH);
            int[] ys = ColumnCardYs(col);
            int top = ColTop(col);
            int bottom = ys[^1] + Layout.CardH;
            return new Rectangle(Layout.ColX(col), top, Layout.CardW, bottom - top);
        }

        private Rectangle SelectedRect()
        {
            if (_selWaste) return WasteRect();
            var c = _board.Columns[_selCol];
            int[] ys = ColumnCardYs(_selCol);
            int top = ys[Math.Min(_selIdx, ys.Length - 1)];
            int bottom = ys[^1] + Layout.CardH;
            return new Rectangle(Layout.ColX(_selCol), top, Layout.CardW, bottom - top);
        }

        // ---------------------------------------------------------- hover narration

        private void UpdateHoverNarration(bool mouseMoved)
        {
            if (!mouseMoved) return;
            if (_board.State != GameState.Playing) return;
            if (_dragKind != DragKind.None) return;
            if (_helpOpen || _defineActive || _settingsOpen) return;

            var (key, label) = HoverAt(_mouse.ToPoint());
            if (key == _hoverKey) return;
            _hoverKey = key;
            if (label != null) Speak(label, true);
        }

        private (string key, string label) HoverAt(Point pt)
        {
            // In define mode, hovering a definable card announces it under the define prefix.
            if (_defineMode)
            {
                var (dc, di) = HitColumnCard(pt);
                Card dcard = null; string dk = null;
                if (dc >= 0) { dcard = _board.Columns[dc][di]; dk = $"col{dc}:{di}"; }
                else
                {
                    int sl = SlotAt(pt);
                    if (sl >= 0 && _board.Slots[sl].Cards.Count > 0) { dcard = _board.Slots[sl].Cards[^1]; dk = $"slot{sl}"; }
                    else if (_board.Waste.Count > 0 && WasteRect().Contains(pt)) { dcard = _board.Waste[^1]; dk = "waste"; }
                }
                if (dcard != null && dcard.FaceUp) return (dk, $"Define mode: {Display(dcard.Word)}.");
            }

            if (_settingsButton.Contains(pt)) return ("settings", "Settings button.");
            if (_newGameButton.Contains(pt)) return ("new", "New Game button.");
            if (SolvedPillRect().Contains(pt)) return ("solved", SolvedNarration());
            if (FabRect().Contains(pt)) return ("fab", MenuNarration());
            if (_toolbarOpen)
                for (int i = 0; i < ToolNames.Length; i++)
                    if (OptPillRect(i, _toolbarT).Contains(pt)) return ($"opt{i}", $"{ToolNames[i]} button.");
            if (MovesRibbonRect().Contains(pt)) return ("moves", MovesNarration());

            for (int i = 0; i < _board.Slots.Count; i++)
                if (SlotRect(i).Contains(pt)) return ($"slot{i}", SlotNarration(i));
            if (_board.Waste.Count > 0 && WasteRect().Contains(pt)) return ("waste", DrawPileNarration());
            if (StockRect().Contains(pt)) return ("stock", DeckNarration());

            var (col, _) = HitColumnCard(pt);
            if (col >= 0) return ($"col{col}", ColumnNarration(col));

            return ("", null);
        }

        // ---------------------------------------------------------- auto-announcements

        /// Rising-edge auto-announcements for mode toggles and overlays (help, definition, win,
        /// lose). Called every frame from Update.
        private void UpdateAccessibilityAnnouncements()
        {
            // Menu open/close cue (covers the FAB tap, keyboard toggle, and Escape). Gated to
            // play only during play, so the win/lose auto-retract of the menu stays silent.
            if (_board.State == GameState.Playing && _toolbarOpen != _prevToolbarOpen)
                PlaySound(_toolbarOpen ? "open-menu" : "close-menu");
            _prevToolbarOpen = _toolbarOpen;

            if (_defineMode && !_prevDefineMode) Speak("Define mode on. Select a card to hear its meaning.", true, true);
            if (!_defineMode && _prevDefineMode) Speak("Define mode off.", true, true);

            if (_helpOpen && !_prevHelpOpen) Speak(HelpNarration(), false, true);
            if (_defineActive && !_prevDefineActive) { PlaySound("define"); Speak(DefinitionNarration(), false, true); }

            bool won = _board.State == GameState.Won;
            if (won && !_prevWon) Speak("You win!", true, true);
            if (_winPopupShown && !_prevWinShown) Speak(WinNarration(), false, true);

            bool lost = _board.State == GameState.Lost;
            if (lost && !_prevLost) Speak(LoseNarration(), false, true);

            _prevDefineMode = _defineMode;
            _prevHelpOpen = _helpOpen;
            _prevDefineActive = _defineActive;
            _prevWon = won;
            _prevWinShown = _winPopupShown;
            _prevLost = lost;
        }

        // ---------------------------------------------------------- drawing

        /// Blue keyboard focus ring + persistent selected-source highlight, both deliberately
        /// distinct from the gold hint. Drawn over the board/toolbar, under any modal overlay.
        private void DrawKeyboardFocus()
        {
            if (_board.State != GameState.Playing || _helpOpen || _defineActive) return;

            if (_hasSel)
            {
                var sr = SelectedRect();
                DrawRoundFill(sr, SelectFill);
                DrawRoundRing(sr, SelectRing);
            }

            if (!_kbActive) return;
            var r = FocusRect(_focusSection, Math.Min(_focusIndex, SectionCount(_focusSection) - 1));
            if (r.Width <= 0 || r.Height <= 0) return;

            var outer = new Rectangle(r.X - Sc(5), r.Y - Sc(5), r.Width + Sc(10), r.Height + Sc(10));
            DrawRoundRing(outer, FocusRingOuter);
            var inner = new Rectangle(r.X - Sc(2), r.Y - Sc(2), r.Width + Sc(4), r.Height + Sc(4));
            DrawRoundRing(inner, FocusRingInner);
        }
    }
}
