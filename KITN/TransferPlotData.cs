using System;
using System.Collections.Generic;

namespace KITN
{
    internal readonly struct TransferContour
    {
        public readonly double X1, Y1, X2, Y2;
        public TransferContour(double x1, double y1, double x2, double y2)
        { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
    }

    /// <summary>Contours interpolate only valid transfer costs; invalid routes remain masked.</summary>
    internal sealed class TransferPlotData
    {
        public readonly float[] Values;
        public readonly List<TransferContour> Contours = new List<TransferContour>();
        public readonly double Minimum, Maximum;
        public TransferPlotData(TransferGrid grid, bool capture)
        {
            Values = new float[grid.Cells.Length];
            double minimum = double.PositiveInfinity, maximum = 0;
            foreach (var cell in grid.Cells)
            {
                double cost = cell.Cost(capture);
                if (!double.IsFinite(cost)) continue;
                minimum = Math.Min(minimum, cost); maximum = Math.Max(maximum, cost);
            }
            Minimum = minimum;
            Maximum = double.IsFinite(minimum) ? Math.Max(minimum + 1, Math.Min(maximum, minimum * 3)) : 0;
            double range = Math.Log(Math.Max(1.001, Maximum / Math.Max(1, Minimum)));
            for (int i = 0; i < Values.Length; i++)
            {
                double cost = grid.Cells[i].Cost(capture);
                Values[i] = double.IsFinite(cost) && double.IsFinite(minimum)
                    ? (float)Math.Clamp(Math.Log(Math.Max(1, cost) / Math.Max(1, minimum)) / range, 0, 1) : float.NaN;
            }
            int width = grid.Request.Width, height = grid.Request.Height;
            Span<double> xs = stackalloc double[4], ys = stackalloc double[4];
            for (int levelIndex = 1; levelIndex < 9; levelIndex++)
            {
                double level = levelIndex / 9.0;
                for (int y = 0; y < height - 1; y++)
                    for (int x = 0; x < width - 1; x++)
                    {
                        double a = Values[y * width + x], b = Values[y * width + x + 1];
                        double c = Values[(y + 1) * width + x + 1], d = Values[(y + 1) * width + x];
                        if (!double.IsFinite(a + b + c + d)) continue;
                        int count = 0;
                        if ((a < level) != (b < level)) { xs[count] = x + (level - a) / (b - a); ys[count++] = y; }
                        if ((b < level) != (c < level)) { xs[count] = x + 1; ys[count++] = y + (level - b) / (c - b); }
                        if ((c < level) != (d < level)) { xs[count] = x + 1 - (level - c) / (d - c); ys[count++] = y + 1; }
                        if ((d < level) != (a < level)) { xs[count] = x; ys[count++] = y + 1 - (level - d) / (a - d); }
                        if (count == 2) Add(xs[0], ys[0], xs[1], ys[1], width, height);
                        else if (count == 4)
                        {
                            if ((a < level) == ((a + b + c + d) / 4 < level))
                            { Add(xs[0], ys[0], xs[1], ys[1], width, height); Add(xs[2], ys[2], xs[3], ys[3], width, height); }
                            else
                            { Add(xs[0], ys[0], xs[3], ys[3], width, height); Add(xs[1], ys[1], xs[2], ys[2], width, height); }
                        }
                    }
            }
        }
        private void Add(double x1, double y1, double x2, double y2, int width, int height)
            => Contours.Add(new TransferContour(x1 / (width - 1), y1 / (height - 1), x2 / (width - 1), y2 / (height - 1)));
    }
}
