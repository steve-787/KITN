using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static void PorkchopAxisChecks()
    {
        const double day = 86400, year = 365.25 * day;
        var pageType = Mod.GetType("KITN.TransferPlannerPage");
        string Format(double seconds) => (string)pageType.GetMethod("AxisDuration").Invoke(null, new object[] { seconds });
        Check(Format(45 * 60) == "45min" && Format(2 * 3600) == "2h" && Format(20 * day) == "20d", "Short transfers do not retain compact minute/hour/day units");
        Check(Format(249.34 * day) == "8.2mo" && Format(1994.75 * day) == "5y 6mo", "Outer-planet axis durations do not switch to months and years");
        Check(Format(year) == "1y" && Format(23.6 * year / 12) == "2y", "Year/month rounding produces an invalid 12-month remainder");
        Check(Format(-5.5 * year) == "−5y 6mo" && Format(0) == "0min", "Signed and zero durations format incorrectly");
        Check(Format(double.NaN) == "—" && Format(double.PositiveInfinity) == "—" && Format(double.MaxValue).Length < 16,
            "Invalid or very large axis values produce overflowing labels");
        Check((string)pageType.GetMethod("Duration").Invoke(null, new object[] { 1994.75 * day }) == "5y 6mo",
            "Long route readouts do not switch to years and months");

        object planner = New("ManeuverPlanner");
        var target = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
        typeof(Astronomical).GetProperty("Id").SetValue(target, "Jupiter");
        Set(planner, "Target", target); Set(planner, "UnavailableReason", null); Set(planner, "OriginName", "Earth");
        Set(Window, "Planner", planner); Set(Window, "_homeOpen", false); Set(Window, "_tab", 5);
        foreach (string locale in new[] { "en-US", "de-DE" })
        {
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            if (locale == "de-DE") Check(Format(249.34 * day) == "8,2M" && Format(1994.75 * day) == "5J 6M", "Compact axis units do not follow the game locale");
            foreach (double maximum in new[] { 1994.75 * day, 60 * year, 999 * year })
            {
                object request = New("TransferRequest");
                Set(request, "Earliest", 1.46 * 3600); Set(request, "Latest", 997.43 * day);
                Set(request, "MinimumDuration", 249.34 * day); Set(request, "MaximumDuration", maximum);
                Set(request, "Width", 8); Set(request, "Height", 6);
                object grid = New("TransferGrid", request);
                var cells = (Array)grid.GetType().GetField("Cells").GetValue(grid);
                for (int index = 0; index < cells.Length; index++)
                {
                    double duration = 249.34 * day + (maximum - 249.34 * day) * (index / 8) / 5;
                    double departure = 1.46 * 3600 + (997.43 * day - 1.46 * 3600) * (index % 8) / 7;
                    cells.SetValue(New("TransferSolution", departure, duration, New("LandingVector", 7e6, 0.0, 0.0),
                        New("LandingVector", 24000.0 + index * 700, 0.0, 0.0), New("LandingVector", 1.0, 0.0, 0.0), 0.0, false), index);
                }
                Set(planner, "Grid", grid); Set(planner, "SelectedIndex", 19);
                for (int frame = 0; frame < 5; frame++) Frame(true, true);
                var plot = PlotRectangle();
                float scale = ConsoleStyle.EffectiveInterfaceScale;
                int labelVertices = 0;
                foreach (var list in DrawLists("/kitn-card-transfer-plot"))
                foreach (var command in list.CmdBuffer.Span)
                {
                    if (Math.Abs(command.ClipRect.Z - (plot.min.X - 6 * scale)) > 0.1f) continue;
                    Check(command.ClipRect.Z < plot.min.X, "Y-axis text clipping overlaps the heatmap");
                    for (int index = (int)command.IdxOffset; index < command.IdxOffset + command.ElemCount; index++)
                    {
                        var vertex = list.VtxBuffer.Span[(int)command.VtxOffset + list.IdxBuffer.Span[index]];
                        Check(vertex.pos.X <= command.ClipRect.Z && vertex.pos.X >= command.ClipRect.X,
                            "A complete Y-axis label does not fit within its reserved gutter");
                        labelVertices++;
                    }
                }
                Check(labelVertices > 30, "Y-axis labels are missing instead of fitting beside the heatmap");
                Save($"porkchop-axis-{maximum / year:0.#}y-{locale}.png", 580, 600, 12, 12);
                // Clicking the center still selects the corresponding cell after gutter sizing.
                var point = new float2((plot.min.X + plot.max.X) / 2, (plot.min.Y + plot.max.Y) / 2);
                Click(point.X, point.Y, "autopilot");
                int selected = (int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner);
                Check(selected / 8 is 2 or 3 && selected % 8 is 3 or 4, "Axis gutter changes break the plot's click coordinates");
            }
        }
        ((IDisposable)planner).Dispose();
        Console.WriteLine("PASS porkchop axes and route readouts: adaptive units, rounding, locale, complete labels outside the heatmap and accurate clicks at minimum size");
    }
}
