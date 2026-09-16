using System;
using System.Linq;

namespace WordAssociationsSolitaire
{
    /// Responsive layout. All positions / sizes are recomputed from the current
    /// window size (Layout.Update) so the board genuinely adapts to any window —
    /// cards grow or shrink to fit and the rows re-center — instead of uniformly
    /// scaling a fixed canvas. The renderer and hit-testing read these fields.
    ///
    /// When the window becomes too narrow to show the slots / columns in a single
    /// row at a comfortable size, the board automatically WRAPS each of them into
    /// two stacked rows (which lets the cards grow back). The choice is made by
    /// picking whichever layout (1 row or 2 rows) yields the larger card.
    public static class Layout
    {
        // Initial window + reference card size (also the texture / Scale baseline).
        public const int BaseW = 1280;
        public const int BaseH = 820;
        public const int BaseCardW = 96;
        public const int BaseCardH = 128;
        public const int CornerRadius = 10;

        // Proportions tuned so the default 1280x820 window reproduces the original
        // hand-tuned layout (CardW ~= 96), then everything scales from there.
        private const float Aspect = BaseCardH / (float)BaseCardW; // 4:3
        private const float GapR = 0.40f;       // card-to-card gap   / CardW
        private const float SideFrac = 0.05f;   // min horizontal margin / width
        private const float HeaderR = 0.80f;    // band above the slots / CardH
        private const float GapSCR = 0.26f;     // slots -> columns gap / CardH
        private const float GapCDR = 0.55f;     // columns -> deck/stock gap / CardH
        private const float GapCDR2 = 0.70f;    // columns -> deck/stock gap when wrapped to 2 rows
        private const float FaceDownR = 0.125f; // face-down peek strip / CardH
        private const float FaceUpR = 0.235f;   // face-up peek strip   / CardH
        private const float FooterR = 0.50f;    // space below the stock / CardH
        private const float SlotRowGapR = 0.30f; // gap between wrapped slot rows / CardH
        private const float ColRowGapR = 0.34f;  // gap between wrapped column rows / CardH
        private const float WrapBias = 1.06f;    // wrap only if it makes cards >=6% bigger
        private const int MinCardW = 38;
        private const int MaxCardW = 168;

        public static int ScreenW { get; private set; } = BaseW;
        public static int ScreenH { get; private set; } = BaseH;

        public static int CardW { get; private set; } = BaseCardW;
        public static int CardH { get; private set; } = BaseCardH;
        public static float Scale { get; private set; } = 1f; // CardW / BaseCardW

        public static int SlotGap { get; private set; } = 40;
        public static int ColGap { get; private set; } = 40;
        public static int FaceDownOffset { get; private set; } = 16;
        public static int FaceUpOffset { get; private set; } = 30;
        public static int StockX { get; private set; }
        public static int StockY { get; private set; }
        public static int WasteX { get; private set; }
        public static int WasteY { get; private set; }
        public static int WastePeek { get; private set; } = 25; // right-fan peek strip width

        // Number of stacked rows the slots / columns are currently laid out in (1 or 2).
        public static int SlotRows { get; private set; } = 1;
        public static int ColRows { get; private set; } = 1;

        // How many columns sit in each wrapped row, and the vertical gap reserved
        // between the two column rows (used by the responsive bottom-row "scoot").
        public static int ColsPerRow { get; private set; } = 1;
        public static int ColRowGap { get; private set; }

        // Per-item positions (filled in Update). Indexed by slot / column index.
        private static int[] _slotX = Array.Empty<int>();
        private static int[] _slotY = Array.Empty<int>();
        private static int[] _colX = Array.Empty<int>();
        private static int[] _colTopY = Array.Empty<int>();

        public static int SlotX(int i) => _slotX[i];
        public static int SlotY(int i) => _slotY[i];
        public static int ColX(int i) => _colX[i];
        public static int ColTopY(int i) => _colTopY[i];

        /// Recompute the whole layout for a window of the given size.
        public static void Update(int w, int h)
        {
            ScreenW = Math.Max(1, w);
            ScreenH = Math.Max(1, h);

            int slots = Math.Max(1, Config.SlotCount);
            int[] colSizes = Config.ColumnSizes;
            int cols = Math.Max(1, colSizes.Length);

            // Pick the row count that grows the cards the most. Two rows only wins when
            // the window is narrow enough that one row would crush the cards.
            float cw1 = FitCardW(1, slots, colSizes);
            float cw2 = FitCardW(2, slots, colSizes);
            int rows = (slots >= 2 && cols >= 2 && cw2 > cw1 * WrapBias) ? 2 : 1;

            float cwf = rows == 2 ? cw2 : cw1;
            int cw = Math.Clamp((int)Math.Floor(cwf), MinCardW, MaxCardW);
            CardW = cw;
            CardH = (int)Math.Round(cw * Aspect);
            Scale = CardW / (float)BaseCardW;

            SlotGap = Math.Max(6, (int)Math.Round(GapR * CardW));
            ColGap = SlotGap;
            FaceDownOffset = Math.Max(6, (int)Math.Round(FaceDownR * CardH));
            FaceUpOffset = Math.Max(12, (int)Math.Round(FaceUpR * CardH));

            ApplyConfig(rows, slots, colSizes);

            StockX = ScreenW - (int)Math.Round(0.42f * CardW) - CardW;
            StockY = ScreenH - (int)Math.Round(FooterR * CardH) - CardH;
            // The waste is a right-opening fan: the top card sits at WasteX and up to two
            // older cards peek out to its right. Reserve room for both peek strips plus a
            // visual gap so the fan never collides with the stock to its right.
            WastePeek = Math.Max(8, (int)Math.Round(0.26f * CardW));
            WasteX = StockX - CardW - 2 * WastePeek - (int)Math.Round(0.34f * CardW);
            WasteY = StockY;
        }

        /// Largest card width (unclamped, in px) that fits both horizontally and
        /// vertically when the slots / columns are wrapped into `rows` stacked rows.
        private static float FitCardW(int rows, int slots, int[] colSizes)
        {
            int cols = colSizes.Length;
            int slotAcross = CeilDiv(slots, rows);
            int colAcross = CeilDiv(cols, rows);
            int maxAcross = Math.Max(slotAcross, colAcross);

            float hCardW = ScreenW * (1f - 2f * SideFrac) / (maxAcross + (maxAcross - 1) * GapR);

            float slotBlock = rows + (rows - 1) * SlotRowGapR;     // CardH units
            float colBlock = ColBlockHeight(rows, colSizes);       // CardH units
            float gapCD = rows == 2 ? GapCDR2 : GapCDR;            // roomier deck gap when wrapped
            float vR = HeaderR + slotBlock + GapSCR + colBlock + gapCD + 1f + FooterR;
            float vCardW = (ScreenH / vR) / Aspect;

            return Math.Min(hCardW, vCardW);
        }

        /// Combined height (in CardH units) of the column block when split into `rows`
        /// contiguous groups: the sum of each group's tallest column plus inter-row gaps.
        private static float ColBlockHeight(int rows, int[] colSizes)
        {
            int cols = colSizes.Length;
            int per = CeilDiv(cols, rows);
            float total = 0f;
            int groups = 0;
            for (int g = 0; g < rows; g++)
            {
                int start = g * per;
                if (start >= cols) break;
                int end = Math.Min(start + per, cols);
                int maxN = 0;
                for (int i = start; i < end; i++) maxN = Math.Max(maxN, colSizes[i]);
                total += 1f + FaceDownR * (maxN - 1);
                groups++;
            }
            if (groups > 1) total += (groups - 1) * ColRowGapR;
            return total;
        }

        /// Lay out the slot row(s) and column row(s) for the chosen row count and
        /// fill the per-item position arrays. Each row is centred horizontally on its own.
        private static void ApplyConfig(int rows, int slots, int[] colSizes)
        {
            SlotRows = rows;
            ColRows = rows;
            int cols = colSizes.Length;

            EnsureSize(ref _slotX, slots);
            EnsureSize(ref _slotY, slots);
            EnsureSize(ref _colX, cols);
            EnsureSize(ref _colTopY, cols);

            int slotRowGap = Math.Max((int)Math.Round(SlotRowGapR * CardH), (int)Math.Round(28 * Scale));
            int colRowGap = Math.Max((int)Math.Round(ColRowGapR * CardH), (int)Math.Round(20 * Scale));
            ColRowGap = colRowGap;

            // --- slots ---
            int slotPer = CeilDiv(slots, rows);
            int slotsTopY = (int)Math.Round(HeaderR * CardH);
            for (int i = 0; i < slots; i++)
            {
                int g = i / slotPer;
                int idx = i - g * slotPer;
                int rowCount = Math.Min(slotPer, slots - g * slotPer);
                int rowStartX = (ScreenW - (rowCount * CardW + (rowCount - 1) * SlotGap)) / 2;
                _slotX[i] = rowStartX + idx * (CardW + SlotGap);
                _slotY[i] = slotsTopY + g * (CardH + slotRowGap);
            }

            // --- columns ---
            int slotsBottomY = slotsTopY + rows * CardH + (rows - 1) * slotRowGap;
            int colsTopY = slotsBottomY + (int)Math.Round(GapSCR * CardH);
            int colPer = CeilDiv(cols, rows);
            ColsPerRow = colPer;

            // Top of each column group: group 0 starts at colsTopY, group 1 below the
            // tallest column of group 0 plus an inter-row gap.
            int group0MaxN = 0;
            for (int i = 0; i < Math.Min(colPer, cols); i++) group0MaxN = Math.Max(group0MaxN, colSizes[i]);
            int group0Height = CardH + FaceDownOffset * (group0MaxN - 1);
            int group1Top = colsTopY + group0Height + colRowGap;

            for (int col = 0; col < cols; col++)
            {
                int g = col / colPer;
                int idx = col - g * colPer;
                int rowCount = Math.Min(colPer, cols - g * colPer);
                int rowStartX = (ScreenW - (rowCount * CardW + (rowCount - 1) * ColGap)) / 2;
                _colX[col] = rowStartX + idx * (CardW + ColGap);
                _colTopY[col] = g == 0 ? colsTopY : group1Top;
            }
        }

        private static int CeilDiv(int a, int b) => (a + b - 1) / b;

        private static void EnsureSize(ref int[] arr, int n)
        {
            if (arr.Length != n) arr = new int[n];
        }
    }
}
