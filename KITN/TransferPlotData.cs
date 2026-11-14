using System;
using System.Collections.Generic;

namespace KITN
{
    internal readonly struct TransferContour
    {
        public readonly double X1, Y1, X2, Y2;
        public readonly int Level;
        public TransferContour(double x1, double y1, double x2, double y2, int level)
        { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Level = level; }
    }

    /// <summary>Continuous display interpolation is separate from selectable, validated routes.</summary>
    internal sealed class TransferPlotData
    {
        public readonly float[] Values;
        public readonly float[] DisplayValues;
        public readonly List<TransferContour> Contours = new List<TransferContour>();
        public readonly double Minimum, Maximum;
        public TransferPlotData(TransferGrid grid, bool capture)
        {
            Values = new float[grid.Cells.Length];
            double minimum = double.PositiveInfinity, maximum = 0;
            foreach (var cell in Enumerate(grid))
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
            DisplayValues = FillDisplay(Values, width, height);
            Span<double> xs = stackalloc double[4], ys = stackalloc double[4];
            for (int levelIndex = 1; levelIndex < 9; levelIndex++)
            {
                double level = levelIndex / 9.0;
                for (int y = 0; y < height - 1; y++)
                    for (int x = 0; x < width - 1; x++)
                    {
                        double a = DisplayValues[y * width + x], b = DisplayValues[y * width + x + 1];
                        double c = DisplayValues[(y + 1) * width + x + 1], d = DisplayValues[(y + 1) * width + x];
                        if (!double.IsFinite(a + b + c + d)) continue;
                        int count = 0;
                        if ((a < level) != (b < level)) { xs[count] = x + (level - a) / (b - a); ys[count++] = y; }
                        if ((b < level) != (c < level)) { xs[count] = x + 1; ys[count++] = y + (level - b) / (c - b); }
                        if ((c < level) != (d < level)) { xs[count] = x + 1 - (level - c) / (d - c); ys[count++] = y + 1; }
                        if ((d < level) != (a < level)) { xs[count] = x; ys[count++] = y + 1 - (level - d) / (a - d); }
                        if (count == 2) Add(xs[0], ys[0], xs[1], ys[1], width, height, levelIndex);
                        else if (count == 4)
                        {
                            if ((a < level) == ((a + b + c + d) / 4 < level))
                            { Add(xs[0], ys[0], xs[1], ys[1], width, height, levelIndex); Add(xs[2], ys[2], xs[3], ys[3], width, height, levelIndex); }
                            else
                            { Add(xs[0], ys[0], xs[3], ys[3], width, height, levelIndex); Add(xs[1], ys[1], xs[2], ys[2], width, height, levelIndex); }
                        }
                    }
            }
        }
        private static IEnumerable<TransferSolution> Enumerate(TransferGrid grid)
        {
            foreach (var cell in grid.Cells) yield return cell;
            foreach (var candidate in grid.Candidates) yield return candidate;
        }

        private static float[] FillDisplay(float[] source, int width, int height)
        {
            var display = (float[])source.Clone();
            var queue = new Queue<int>();
            for (int i = 0; i < display.Length; i++) if (float.IsFinite(display[i])) queue.Enqueue(i);
            if (queue.Count == 0) { Array.Fill(display, 1); return display; }
            // Seed missing samples from their nearest valid cost, gently increasing toward distant regions.
            while (queue.Count > 0)
            {
                int index = queue.Dequeue(), x = index % width, y = index / width;
                void Fill(int neighbor, float step)
                {
                    if (float.IsFinite(display[neighbor])) return;
                    display[neighbor] = Math.Min(1, display[index] + step); queue.Enqueue(neighbor);
                }
                if (x > 0) Fill(index - 1, 2f / width);
                if (x + 1 < width) Fill(index + 1, 2f / width);
                if (y > 0) Fill(index - width, 2f / height);
                if (y + 1 < height) Fill(index + width, 2f / height);
            }
            // Relax only display samples; real costs and route validity never change.
            var next = (float[])display.Clone();
            for (int pass = 0; pass < 64; pass++)
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x;
                        if (float.IsFinite(source[i])) continue;
                        float sum = 0; int count = 0;
                        if (x > 0) { sum += display[i - 1]; count++; }
                        if (x + 1 < width) { sum += display[i + 1]; count++; }
                        if (y > 0) { sum += display[i - width]; count++; }
                        if (y + 1 < height) { sum += display[i + width]; count++; }
                        next[i] = sum / count;
                    }
                (display, next) = (next, display);
            }
            return display;
        }
        private void Add(double x1, double y1, double x2, double y2, int width, int height, int level)
            => Contours.Add(new TransferContour(x1 / (width - 1), y1 / (height - 1), x2 / (width - 1), y2 / (height - 1), level));
    }
}
