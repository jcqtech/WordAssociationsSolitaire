using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using WinFormsA11y = System.Windows.Forms.Automation;

namespace WordAssociationsSolitaire
{
    /// Accessibility layer: full keyboard navigation (Tab between sections, arrows within a
    /// section, Enter/Space to activate), the `n`/`d` shortcuts, and Windows Narrator output
    /// via the built-in UI Automation notification API (no external dependency).
    ///
    /// Navigation is two-level. Tab / Shift+Tab cycle five SECTIONS; arrow keys move between the
    /// elements inside the focused section; Enter or Space activates the focused element. A card
    /// is "activated" by selecting it as a move source (persistent highlight); activating a
    /// foundation slot or column afterwards attempts the move.
    public partial class Game1
    {
        private enum FocusSection { TopBar, Foundations, Columns, Deck, BottomBar }

        // ---- focus / selection state ----
        private bool _kbActive;                 // becomes true on first Tab/arrow; shows the focus ring
        private FocusSection _focusSection = FocusSection.TopBar;
        private int _focusIndex;

        private bool _hasSel;                   // a move source is currently selected
        private bool _selWaste;                 // the selected source is the waste top
        private int _selCol, _selIdx;           // else a column tail (col, tail-start index)

        // ---- narrator ----
        private System.Windows.Forms.AccessibleObject _accObj;
        private bool _accResolved;
        private string _lastSpoken = "";

        // ---- edge-detection for auto-announcing overlays / modes ----
        private bool _prevHelpOpen, _prevDefineActive, _prevDefineMode, _prevWinShown, _prevLost, _prevWon;
        private bool _prevToolbarOpen;
        private string _hoverKey = "";

        // Focus/selection colours — a clear BLUE, deliberately distinct from the gold hint.
        private static readonly Color FocusRingOuter = new(70, 150, 255, 220);
        private static readonly Color FocusRingInner = new(160, 205, 255, 255);
        private static readonly Color SelectFill = new(90, 170, 255, 70);
        private static readonly Color SelectRing = new(120, 195, 255, 235);

        private void ResetAccessibility()
        {
            _kbActive = false;
            _focusSection = FocusSection.TopBar;
            _focusIndex = 0;
            _hasSel = false;
            _hoverKey = "";
            _lastSpoken = "";
            _prevHelpOpen = _prevDefineActive = _prevDefineMode = _prevWinShown = _prevLost = _prevWon = false;
            _prevToolbarOpen = false;
        }

        // ------------------------------------------------------------- narrator

        private System.Windows.Forms.AccessibleObject AccObj()
        {
            if (!_accResolved)
            {
                _accResolved = true;
                if (System.Windows.Forms.Control.FromHandle(Window.Handle) is System.Windows.Forms.Form f)
                    _accObj = f.AccessibilityObject;
            }
            return _accObj;
        }

        /// Speak text through Narrator (a UIA notification event). `interrupt` replaces any pending
        /// announcement (focus/hover); pass false to let longer popup text queue and finish. Repeated
        /// identical text is suppressed unless `force` (so re-activating an element still re-reads it).
        private void Speak(string text, bool interrupt = true, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!force && text == _lastSpoken) return;
            _lastSpoken = text;
            try
            {
                AccObj()?.RaiseAutomationNotification(
                    WinFormsA11y.AutomationNotificationKind.Other,
                    interrupt ? WinFormsA11y.AutomationNotificationProcessing.MostRecent
                              : WinFormsA11y.AutomationNotificationProcessing.All,
                    text);
            }
            catch { /* no UIA client / not supported -> silently ignore */ }
        }

        // ------------------------------------------------------------- input

        /// Handle the accessibility keyboard verbs. Called from Update with the current keyboard
        /// state (edge-detected against _prevKeyboard, which Update updates just afterwards).
        private void UpdateAccessibilityInput(KeyboardState ks)
        {
            var prev = _prevKeyboard;
            bool shift = ks.IsKeyDown(Keys.LeftShift) || ks.IsKeyDown(Keys.RightShift);
            bool P(Keys k) => ks.IsKeyDown(k) && !prev.IsKeyDown(k);

            // Settings owns the keyboard while open. Escape is handled by Update before this
            // method runs, so the popup can still be dismissed without touching the board.
            if (_settingsOpen) return;

            // New Game works from anywhere.
            if (P(Keys.N)) { NewGame(); Speak("New game.", true, true); return; }

            bool modalText = _helpOpen || _defineActive;
            // Enter/Space/Escape closes an open text popup.
            if (modalText && (P(Keys.Enter) || P(Keys.Space)))
            {
                if (_helpOpen) _helpOpen = false; else _defineActive = false;
                return;
            }

            // The deal animation stores the original column indices. Board mutations must wait
            // until it has finished so those indices remain valid while it renders.
            if (Dealing) return;

            // Define-mode toggle (announced by the edge detector).
            if (P(Keys.D) && _board.State == GameState.Playing && !modalText)
            {
                if (_defineMode) ExitDefineMode(); else _defineMode = true;
                return;
            }

            if (_board.State != GameState.Playing || modalText) return;

            // While a hint is showing, Enter/Space performs the hinted action (takes priority):
            // a deck-hint draws from the deck; a move-hint plays the highlighted move.
            if ((P(Keys.Enter) || P(Keys.Space)) && _hintActive)
            {
                if (_hintDeck) { _hintActive = false; ActivateDeck(); return; }
                if (_hintIdx >= 0 && _hintIdx < _hintMoves.Count) { PerformHintMove(); return; }
            }

            if (P(Keys.Tab)) { _kbActive = true; MoveSection(shift ? -1 : 1); return; }

            bool anyNav = P(Keys.Left) || P(Keys.Right) || P(Keys.Up) || P(Keys.Down) || P(Keys.Enter) || P(Keys.Space);
            if (!_kbActive)
            {
                if (anyNav) { _kbActive = true; _focusSection = FocusSection.TopBar; _focusIndex = 0; AnnounceFocus(); }
                return;
            }

            if (P(Keys.Left) || P(Keys.Up)) { MoveElement(-1); return; }
            if (P(Keys.Right) || P(Keys.Down)) { MoveElement(1); return; }
            if (P(Keys.Enter) || P(Keys.Space)) { ActivateFocused(); return; }
        }

        private void MoveSection(int dir)
        {
            const int n = 5;
            _focusSection = (FocusSection)((((int)_focusSection + dir) % n + n) % n);
            _focusIndex = 0;
            AnnounceFocus();
        }

        private void MoveElement(int dir)
        {
            int c = Math.Max(1, SectionCount(_focusSection));
            _focusIndex = ((_focusIndex + dir) % c + c) % c;
            AnnounceFocus();
        }

        private int SectionCount(FocusSection s) => s switch
        {
            FocusSection.TopBar => 2,
            FocusSection.Foundations => _board.Slots.Count,
            FocusSection.Columns => _board.Columns.Count,
            FocusSection.Deck => 2,
            FocusSection.BottomBar => _toolbarOpen ? 2 + ToolNames.Length : 2,
            _ => 1,
        };

        private void AnnounceFocus()
        {
            int idx = Math.Min(_focusIndex, SectionCount(_focusSection) - 1);
            _focusIndex = idx;
            // In define mode, focusing a definable card announces it under the define prefix.
            if (_defineMode)
            {
                string w = FocusedDefinableWord();
                if (w != null) { Speak($"Define mode: {w}.", true, true); return; }
            }
            Speak(FocusLabel(_focusSection, idx), true, true);
        }

        // ------------------------------------------------------------- activation

        private void ActivateFocused()
        {
            int idx = Math.Min(_focusIndex, SectionCount(_focusSection) - 1);
            switch (_focusSection)
            {
                case FocusSection.TopBar:
                    if (idx == 0) { NewGame(); Speak("New game.", true, true); }
                    else Speak(SolvedNarration(), true, true);
                    break;

                case FocusSection.Foundations:
                    if (_defineMode) { DefineFocused(); break; }
                    if (_hasSel) TryKeyboardMove(false, idx);
                    else Speak("Nothing to select here.", true, true);
                    break;

                case FocusSection.Columns:
                    if (_defineMode) { DefineFocused(); break; }
                    if (_hasSel) TryKeyboardMove(true, idx);
                    else SelectColumnSource(idx);
                    break;

                case FocusSection.Deck:
                    if (idx == 0) ActivateDrawPile();
                    else ActivateDeck();
                    break;

                case FocusSection.BottomBar:
                    ActivateBottomBar(idx);
                    break;
            }
        }

        private void ActivateDrawPile()
        {
            if (Dealing) return;
            if (_defineMode) { DefineFocused(); return; }
            if (_hasSel && _selWaste) { _hasSel = false; Speak("Selection cleared.", true, true); return; }
            if (_hasSel) { Speak("Can't move there.", true, true); return; }
            if (_board.Waste.Count == 0) { Speak("Draw pile empty.", true, true); return; }
            _hasSel = true; _selWaste = true;
            PlaySound("take-card");
            Speak($"Selected {Display(_board.Waste[^1].Word)}.", true, true);
        }

        private void ActivateDeck()
        {
            if (Dealing) return;
            if (!_board.CanDraw()) { Speak("Deck can't be drawn.", true, true); return; }
            bool recycling = _board.Stock.Count == 0 && _board.Waste.Count > 0;
            var oldWaste = recycling ? new List<Card>(_board.Waste) : null;
            var wastePos = recycling ? CaptureWasteFanPositions() : null;
            var snap = _board.Clone();
            if (_board.Draw())
            {
                PushUndo(snap);
                _hasSel = false;
                if (recycling) { StartRecycleAnim(oldWaste, wastePos); PlaySound("shuffling"); Speak("Recycled the deck.", true, true); }
                else
                {
                    StartDrawFlip();
                    StartWasteShuffle();
                    PlaySound("take-card");
                    Speak($"Card drawn: {Display(_board.Waste[^1].Word)}.", true, true);
                }
            }
        }

        private void ActivateBottomBar(int idx)
        {
            if (idx == 0) { _toolbarOpen = !_toolbarOpen; Speak(MenuNarration(), true, true); return; }
            if (_toolbarOpen && idx >= 1 && idx <= ToolNames.Length)
            {
                int tool = idx - 1;
                Speak($"{ToolNames[tool]}.", true, true);
                ActivateTool(tool);
                return;
            }
            // Last element = Moves-left.
            Speak(MovesNarration(), true, true);
        }

        /// Select the maximal valid, face-up, same-category tail reaching the front of a column.
        private void SelectColumnSource(int col)
        {
            var c = _board.Columns[col];
            if (c.Count == 0 || !c[^1].FaceUp) { Speak("Nothing to select in this stack.", true, true); return; }
            int front = c.Count - 1;
            int cat = c[front].CategoryId;
            int idx = front;
            while (idx - 1 >= 0 && c[idx - 1].FaceUp && c[idx - 1].CategoryId == cat) idx--;
            if (!_board.IsValidTail(col, idx)) { Speak("Nothing to select in this stack.", true, true); return; }
            _hasSel = true; _selWaste = false; _selCol = col; _selIdx = idx;
            int n = c.Count - idx;
            string more = n > 1 ? $" and {n - 1} more" : "";
            PlaySound("take-card");
            Speak($"Selected {Display(c[front].Word)}{more}.", true, true);
        }

        /// Attempt to move the selected source onto a foundation slot (toColumn=false) or a column
        /// (toColumn=true), reusing the exact move + undo + animation path used by mouse drops.
        private bool TryKeyboardMove(bool toColumn, int dst)
        {
            if (Dealing || !_hasSel) return false;

            var snap = _board.Clone();
            List<Card> moving;
            Vector2 origin;
            bool ok;
            if (_selWaste)
            {
                if (_board.Waste.Count == 0) { _hasSel = false; return false; }
                moving = new List<Card> { _board.Waste[^1] };
                origin = new Vector2(Layout.WasteX, Layout.WasteY);
                ok = toColumn ? _board.MoveWasteToColumn(dst) : _board.MoveWasteToSlot(dst);
            }
            else
            {
                var col = _board.Columns[_selCol];
                if (_selIdx < 0 || _selIdx >= col.Count) { _hasSel = false; return false; }
                moving = col.GetRange(_selIdx, col.Count - _selIdx);
                int[] ys = ColumnCardYs(_selCol);
                origin = new Vector2(Layout.ColX(_selCol), ys[_selIdx]);
                ok = toColumn ? _board.MoveColumnTailToColumn(_selCol, _selIdx, dst)
                              : _board.MoveColumnTailToSlot(_selCol, _selIdx, dst);
            }

            if (!ok) { _hasSel = false; PlaySound("deny"); Speak("Can't move there.", true, true); return false; }

            PushUndo(snap);
            _wasteShift = 0f;
            if (!toColumn && _board.LastCompletion != null)
            {
                StartSlotCompletion(_board.LastCompletion);
                _board.LastCompletion = null;
                PlaySound("success");
                Speak("Set complete!", true, true);
            }
            else if (!toColumn)
            {
                StartSlideCollapse(moving, origin, new Vector2(Layout.SlotX(dst), Layout.SlotY(dst)), toSlot: true);
                PlaySound("place-card");
                Speak("Moved to foundation.", true, true);
            }
            else
            {
                StartSlideToColumn(moving, origin, dst);
                PlaySound("place-card");
                Speak("Moved.", true, true);
            }
            _hasSel = false;
            return true;
        }

        // ------------------------------------------------------------- define via keyboard

        /// The Display word of the currently focused card if it's definable (face-up), else null.
        private string FocusedDefinableWord()
        {
            var card = FocusedCard();
            return (card != null && card.FaceUp) ? Display(card.Word) : null;
        }

        /// The card under the current focus (column front, slot top, or waste top) or null.
        private Card FocusedCard()
        {
            switch (_focusSection)
            {
                case FocusSection.Foundations:
                    var s = _board.Slots[Math.Min(_focusIndex, _board.Slots.Count - 1)];
                    return s.Cards.Count > 0 ? s.Cards[^1] : null;
                case FocusSection.Columns:
                    var c = _board.Columns[Math.Min(_focusIndex, _board.Columns.Count - 1)];
                    return c.Count > 0 ? c[^1] : null;
                case FocusSection.Deck:
                    return _focusIndex == 0 && _board.Waste.Count > 0 ? _board.Waste[^1] : null;
                default:
                    return null;
            }
        }

        private void DefineFocused()
        {
            var card = FocusedCard();
            if (card == null || !card.FaceUp) { Speak("Nothing to define here.", true, true); return; }
            _defineWord = Display(card.Word);
            _defineText = WordData.DefinitionFor(card.Word);
            if (string.IsNullOrWhiteSpace(_defineText)) _defineText = "No definition available.";
            _defineActive = true;
            _defineT = 0f;
        }

        // ------------------------------------------------------------- hint move

        /// Perform the currently highlighted hint move (bound to Enter while a hint is showing).
        private void PerformHintMove()
        {
            if (!_hintActive || _hintDeck || _hintIdx < 0 || _hintIdx >= _hintMoves.Count) return;
            var m = _hintMoves[_hintIdx];
            _hasSel = true;
            _selWaste = m.SrcWaste;
            _selCol = m.SrcCol;
            _selIdx = m.SrcIdx;
            _hintActive = false;
            _hintIdx = -1;
            TryKeyboardMove(m.TgtColumn, m.TgtIndex);
        }
    }
}
