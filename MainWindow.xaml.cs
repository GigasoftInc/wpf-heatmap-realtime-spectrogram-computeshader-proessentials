using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Globalization;
using Gigasoft.ProEssentials;
using Gigasoft.ProEssentials.Enums;
using System.Windows.Media;

namespace RealtimeHeatmap
{
    /// <summary>
    /// ProEssentials WPF Realtime Heatmap — Spectrogram — 2D Contour
    ///
    /// Demonstrates a realtime heatmap/spectrogram that replaces the entire
    /// 93,696-value surface every 25ms using a tiled data pool and Array.Copy.
    ///
    /// Architecture:
    ///   fZDataPool[281,088]  — Heatmap.txt Z values tiled 3× in memory.
    ///                          Any random offset + FRAME_SIZE never exceeds bounds.
    ///   fZDataToChart[93,696]— Fixed buffer passed to UseDataAtLocation once at init.
    ///                          The chart reads from this fixed address every frame.
    ///                          Each tick copies a new slice from fZDataPool here.
    ///   fXDataPool[512]      — X axis values, set once via UseDataAtLocation.
    ///   fYDataPool[183]      — Y axis values, set once via UseDataAtLocation.
    ///
    /// UseDataAtLocation is set once at init — the pointer never changes.
    /// Each tick copies 93,696 floats (~366KB) from a random offset in fZDataPool
    /// into fZDataToChart. The chart reads the new contents from the same
    /// fixed address. The GPU ComputeShader does all contour rendering work.
    ///
    /// At ~366KB, fZDataToChart is well above the 85KB threshold below which
    /// arrays need to be pinned to prevent GC relocation.
    ///
    /// The random offset advances by a small step each tick producing organic,
    /// breathing movement across the whole heatmap surface simultaneously —
    /// like a live spectrum analyzer, not a strip chart.
    ///
    /// Data file:
    ///   Heatmap.txt — 93,696 lines, tab-delimited X/Y/Z
    ///   183 subsets × 512 points
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int SUBSETS    = 183;
        private const int POINTS     = 512;
        private const int FRAME_SIZE = SUBSETS * POINTS; // 93,696 values per frame

        // fZDataPool: Heatmap.txt Z values tiled 3× — source pool for random slices
        public static float[] fZDataPool    = new float[FRAME_SIZE * 3];
        // fZDataToChart: fixed chart buffer — UseDataAtLocation points here permanently
        // Array.Copy writes new data here each tick; chart reads from same fixed address
        public static float[] fZDataToChart = new float[FRAME_SIZE];
        public static float[] fXDataPool    = new float[POINTS];
        public static float[] fYDataPool    = new float[SUBSETS];

        private int      m_nZOffset  = 0;
        private int      _frameCount = 0;
        private DateTime _lastFpsTime = DateTime.Now;
        private static Random Rand_Num = new Random(unchecked((int)DateTime.Now.Ticks));
        private DispatcherTimer _timer;

        public MainWindow()
        {
            InitializeComponent();
        }

        // -----------------------------------------------------------------------
        // Pesgo1_Loaded — one-time initialization
        // -----------------------------------------------------------------------
        void Pesgo1_Loaded(object sender, RoutedEventArgs e)
        {
            // =======================================================================
            // Step 1 — Load Heatmap.txt and tile 3× into fZDataPool
            //
            // Load Z values into the first FRAME_SIZE slots, then copy that block
            // twice more. Any offset from 0 to FRAME_SIZE-1 can safely read
            // FRAME_SIZE values without bounds checking.
            // X and Y axis values go into their own small pools.
            // =======================================================================
            string[] fileArray = { "", "" };
            try
            {
                fileArray = File.ReadAllLines("Heatmap.txt");
            }
            catch
            {
                MessageBox.Show(
                    "Heatmap.txt not found.\n\nMake sure Heatmap.txt is in the same folder as the executable.",
                    "File Not Found", MessageBoxButton.OK);
                Application.Current.Shutdown();
                return;
            }

            int nSubsetCount = 0;
            int nPointCount  = 0;

            for (int i = 0; i < fileArray.Length; i++)
            {
                string line = fileArray[i];
                if (line.Length < 3) continue;

                var   columns = line.Split('\t');
                float fX = float.Parse(columns[0], CultureInfo.InvariantCulture.NumberFormat);
                float fY = float.Parse(columns[1], CultureInfo.InvariantCulture.NumberFormat);
                float fZ = float.Parse(columns[2], CultureInfo.InvariantCulture.NumberFormat);

                if (nSubsetCount == 0)
                    fXDataPool[nPointCount] = fX + 20.0F;

                if (nPointCount == 0)
                    fYDataPool[nSubsetCount] = fY * (i + 1000) / 100.0F;

                fZDataPool[nSubsetCount * POINTS + nPointCount] = fZ;

                nPointCount++;
                if (nPointCount >= POINTS)
                {
                    nPointCount = 0;
                    nSubsetCount++;
                }
            }

            // Tile Z data twice more — safe random reads anywhere in first FRAME_SIZE
            Array.Copy(fZDataPool, 0, fZDataPool, FRAME_SIZE,     FRAME_SIZE);
            Array.Copy(fZDataPool, 0, fZDataPool, FRAME_SIZE * 2, FRAME_SIZE);

            // Prime fZDataToChart with the initial slice
            Array.Copy(fZDataPool, 0, fZDataToChart, 0, FRAME_SIZE);

            // =======================================================================
            // Step 2 — Data dimensions and DuplicateData
            // =======================================================================
            Pesgo1.PeData.Subsets = SUBSETS;
            Pesgo1.PeData.Points  = POINTS;

            // Only 512 X values and 183 Y values stored; chart duplicates internally
            Pesgo1.PeData.DuplicateDataX = DuplicateData.PointIncrement;
            Pesgo1.PeData.DuplicateDataY = DuplicateData.SubsetIncrement;

            // =======================================================================
            // Step 3 — Data transfer
            //
            // X and Y: FastCopyFrom — these are small arrays (512 and 183 floats),
            // well under the 85KB threshold below which UseDataAtLocation requires
            // the array to be pinned to prevent GC relocation. FastCopyFrom is the
            // safe approach for small arrays set once at init.
            //
            // Z: UseDataAtLocation — fZDataToChart is ~366KB, safely above the
            // 85KB threshold, no pinning needed. The chart holds a permanent pointer
            // to fZDataToChart. Each tick, Array.Copy writes new Z data into this
            // fixed buffer and the chart reads the updated contents from the same
            // address without any internal copy.
            // =======================================================================
            Pesgo1.PeData.X.FastCopyFrom(fXDataPool, POINTS);           // 512 X values, set once
            Pesgo1.PeData.Y.FastCopyFrom(fYDataPool, SUBSETS);          // 183 Y values, set once
            Pesgo1.PeData.Z.UseDataAtLocation(fZDataToChart, FRAME_SIZE); // Z: fixed pointer, updated each tick

            // =======================================================================
            // Step 4 — Log Y axis scale
            // =======================================================================
            Pesgo1.PeGrid.Configure.YAxisScaleControl = ScaleControl.Log;

            // =======================================================================
            // Step 5 — Contour color plotting
            // ContourColorBlends must always be set BEFORE ContourColorSet
            // =======================================================================
            Pesgo1.PePlot.Allow.ContourColors        = true;
            Pesgo1.PePlot.Allow.ContourColorsShadows = true;
            Pesgo1.PeColor.ContourColorBlends        = 10;
            Pesgo1.PeColor.ContourColorSet           = ContourColorSet.BlueCyanGreenYellowBrownWhite;
            Pesgo1.PeLegend.ContourLegendPrecision   = ContourLegendPrecision.ZeroDecimals;
            Pesgo1.PeLegend.ContourStyle             = true;
            Pesgo1.PePlot.Method                     = SGraphPlottingMethod.ContourColors;
            Pesgo1.PeUserInterface.Menu.DataShadow   = MenuControl.Hide;

            // =======================================================================
            // Step 6 — Zoom and interaction
            // =======================================================================
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomFactor     = 1.4F;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomSmoothness = 2;
            Pesgo1.PeGrid.GridBands                                    = false;
            Pesgo1.PeUserInterface.Allow.ZoomStyle                     = ZoomStyle.Ro2Not;
            Pesgo1.PeUserInterface.Allow.Zooming                       = AllowZooming.HorzAndVert;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelFunction        = MouseWheelFunction.HorizontalVerticalZoom;
            Pesgo1.PeUserInterface.Scrollbar.ScrollingVertZoom         = true;
            Pesgo1.PeUserInterface.Scrollbar.ScrollingHorzZoom         = true;

            // =======================================================================
            // Step 7 — Legend and grid
            // =======================================================================
            Pesgo1.PeLegend.Location  = LegendLocation.Left;
            Pesgo1.PeGrid.InFront     = true;
            Pesgo1.PeGrid.LineControl = GridLineControl.Both;
            Pesgo1.PeGrid.Style       = GridStyle.Dot;

            // Disable non-contour plot methods from the right-click menu
            Pesgo1.PePlot.Allow.Line             = false;
            Pesgo1.PePlot.Allow.Point            = false;
            Pesgo1.PePlot.Allow.Bar              = false;
            Pesgo1.PePlot.Allow.Area             = false;
            Pesgo1.PePlot.Allow.Spline           = false;
            Pesgo1.PePlot.Allow.SplineArea       = false;
            Pesgo1.PePlot.Allow.PointsPlusLine   = false;
            Pesgo1.PePlot.Allow.PointsPlusSpline = false;
            Pesgo1.PePlot.Allow.BestFitCurve     = false;
            Pesgo1.PePlot.Allow.BestFitLine      = false;
            Pesgo1.PePlot.Allow.Stick            = false;

            // =======================================================================
            // Step 8 — Titles and fonts
            // =======================================================================
            Pesgo1.PeString.MainTitle = "Realtime Heatmap — Spectrogram — 2D Contour";
            Pesgo1.PeString.SubTitle  = "";
            Pesgo1.PeGrid.Configure.AutoMinMaxPadding = 0;
            Pesgo1.PeFont.FontSize = Gigasoft.ProEssentials.Enums.FontSize.Large;
            Pesgo1.PeFont.Fixed    = true;

            Pesgo1.PeUserInterface.Dialog.Axis    = false;
            Pesgo1.PeUserInterface.Dialog.Style   = false;
            Pesgo1.PeUserInterface.Dialog.Subsets = false;

            Pesgo1.PeConfigure.TextShadows = TextShadows.BoldText;
            Pesgo1.PeFont.MainTitle.Bold   = true;
            Pesgo1.PeFont.SubTitle.Bold    = true;
            Pesgo1.PeFont.Label.Bold       = true;

            // =======================================================================
            // Step 9 — Styling
            // =======================================================================
            Pesgo1.PeColor.BitmapGradientMode = true;
            Pesgo1.PeColor.QuickStyle         = QuickStyle.DarkNoBorder;
            Pesgo1.PeColor.GridBold           = true;

            // =======================================================================
            // Step 10 — Export defaults
            // =======================================================================
            Pesgo1.PeSpecial.DpiX = 600;
            Pesgo1.PeSpecial.DpiY = 600;
            Pesgo1.PeUserInterface.Dialog.AllowEmfExport  = false;
            Pesgo1.PeUserInterface.Dialog.AllowWmfExport  = false;
            Pesgo1.PeUserInterface.Dialog.ExportSizeDef  = ExportSizeDef.NoSizeOrPixel;
            Pesgo1.PeUserInterface.Dialog.ExportTypeDef  = ExportTypeDef.Png;
            Pesgo1.PeUserInterface.Dialog.ExportDestDef  = ExportDestDef.Clipboard;
            Pesgo1.PeUserInterface.Dialog.ExportUnitXDef = "1280";
            Pesgo1.PeUserInterface.Dialog.ExportUnitYDef = "768";
            Pesgo1.PeUserInterface.Dialog.ExportImageDpi = 300;

            // =======================================================================
            // Step 11 — Rendering engine + ComputeShader
            //
            // Composite2D3D.Foreground: GPU renders contour fill, 2D axis/labels
            // composited on top — best combination of GPU throughput and crisp text.
            //
            // StagingBufferY/Z: required staging buffers for ComputeShader +
            // UseDataAtLocation.
            // =======================================================================
            Pesgo1.PeConfigure.Composite2D3D = Composite2D3D.Foreground;
            Pesgo1.PeConfigure.RenderEngine  = RenderEngine.Direct3D;
            // ComputeShader accelerates contour color interpolation on the GPU.
            // No staging buffers needed for 2D contour — only Z uses UseDataAtLocation
            // and fZDataToChart is above the 85KB safe threshold.
            Pesgo1.PeData.ComputeShader = true;

            // =======================================================================
            // Step 12 — XYZ cursor prompt
            // Note: enabling PromptTracking on a realtime chart freezes the timer
            // as ProEssentials processes hit testing on every mouse move event.
            // Commented out for smooth realtime performance. Re-enable for static use.
            // =======================================================================
            //Pesgo1.PeUserInterface.Cursor.PromptTracking     = true;
            //Pesgo1.PeUserInterface.Cursor.PromptStyle        = CursorPromptStyle.XYZValues;
            //Pesgo1.PeUserInterface.Cursor.PromptLocation     = CursorPromptLocation.Text;
            Pesgo1.PeUserInterface.Cursor.HourGlassThreshold = 9999999;

            Pesgo1.PeFunction.Force3dxNewColors      = true;
            Pesgo1.PeFunction.Force3dxVerticeRebuild = true;

            Pesgo1.PeFunction.ReinitializeResetImage();
            Pesgo1.Invalidate();

            // =======================================================================
            // Step 13 — Start timer
            // =======================================================================
            _timer          = new DispatcherTimer(DispatcherPriority.Input);
            _timer.Interval = TimeSpan.FromMilliseconds(25);
            _timer.Tick    += Timer1_Tick;
            _timer.Start();
        }

        // -----------------------------------------------------------------------
        // Timer1_Tick — realtime heatmap update, horizontal shift only
        //
        // Each tick advances m_nColOffset within each row independently.
        // Every subset (Y row) reads from its own fixed row in fZDataPool
        // but at a shifted column position — so the pattern moves left/right
        // across the heatmap without any vertical drift.
        //
        // Per-row copy with wrap:
        //   Right portion: fZDataPool[row + colOffset .. row + POINTS-1]
        //   Left  portion: fZDataPool[row + 0 .. row + colOffset-1]  (wrap)
        //
        // Each row stays in its correct Y frequency band — only the X
        // position within the row advances each tick.
        //
        // Step size tuning:
        //   Rand_Num.Next(1,  10)   — slow smooth horizontal drift
        //   Rand_Num.Next(50, 200)  — natural organic movement  ← default
        //   Rand_Num.Next(500,2000) — dramatic rapid shifting
        // -----------------------------------------------------------------------
        void Timer1_Tick(object sender, EventArgs e)
        {
            _timer.Stop();

            // FPS counter
            _frameCount++;
            var elapsed = (DateTime.Now - _lastFpsTime).TotalSeconds;
            if (elapsed >= 1.0)
            {
                Title        = $"ProEssentials Realtime Heatmap — {_frameCount} FPS";
                _frameCount  = 0;
                _lastFpsTime = DateTime.Now;
            }

            // Advance column offset — wraps within POINTS
            m_nZOffset += Rand_Num.Next(50, 200);
            if (m_nZOffset >= POINTS) { m_nZOffset = 0; }

            // Rebuild Z: each row shifts horizontally by m_nZOffset,
            // wrapping at the row boundary — no vertical drift
            int rightLen = POINTS - m_nZOffset;
            for (int s = 0; s < SUBSETS; s++)
            {
                int srcRow = s * POINTS;
                int dstRow = s * POINTS;

                // Right portion of row — from colOffset to end
                Array.Copy(fZDataPool, srcRow + m_nZOffset, fZDataToChart, dstRow,            rightLen);
                // Left portion of row — wrap from start of row
                Array.Copy(fZDataPool, srcRow,              fZDataToChart, dstRow + rightLen, m_nZOffset);
            }

            // ReuseDataX / ReuseDataY: X and Y data never change each tick.
            // Even without staging buffers, these flags prevent ProEssentials
            // from re-transferring X and Y data CPU→GPU on every frame —
            // only the new Z data gets processed.
            Pesgo1.PeData.ReuseDataX = true;
            Pesgo1.PeData.ReuseDataY = true;

            // Force GPU to process new Z data and rebuild contour
            Pesgo1.PeFunction.Force3dxNewColors      = true;
            Pesgo1.PeFunction.Force3dxVerticeRebuild = true;

            Pesgo1.Invalidate();

            _timer.Start();
        }

        // -----------------------------------------------------------------------
        // Window_Closing
        // -----------------------------------------------------------------------
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer?.Stop();
        }
    }
}
