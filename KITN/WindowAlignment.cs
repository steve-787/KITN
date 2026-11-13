using System;

namespace KITN
{
    internal readonly struct AlignmentResult
    {
        public readonly float X, Y;
        public readonly float? GuideX, GuideY;
        public AlignmentResult(float x, float y, float? guideX, float? guideY)
        { X = x; Y = y; GuideX = guideX; GuideY = guideY; }
    }

    /// <summary>Find the nearest center or matching edge, in screen pixels.</summary>
    internal static class WindowAlignment
    {
        public static AlignmentResult Snap(UiWindowBounds moving, UiWindowBounds viewport, UiWindowBounds peer, float threshold)
        {
            if (moving?.IsValid != true || viewport?.IsValid != true || !float.IsFinite(threshold) || threshold < 0)
                return new AlignmentResult(moving?.X ?? 0, moving?.Y ?? 0, null, null);
            float dx = threshold + 1, dy = threshold + 1;
            float? gx = null, gy = null;
            Match(moving.X + moving.Width / 2, viewport.X + viewport.Width / 2, threshold, ref dx, ref gx);
            Match(moving.Y + moving.Height / 2, viewport.Y + viewport.Height / 2, threshold, ref dy, ref gy);
            if (peer?.IsValid == true)
            {
                Match(moving.X + moving.Width / 2, peer.X + peer.Width / 2, threshold, ref dx, ref gx);
                Match(moving.Y + moving.Height / 2, peer.Y + peer.Height / 2, threshold, ref dy, ref gy);
                Match(moving.X, peer.X, threshold, ref dx, ref gx);
                Match(moving.X + moving.Width, peer.X + peer.Width, threshold, ref dx, ref gx);
                Match(moving.Y, peer.Y, threshold, ref dy, ref gy);
                Match(moving.Y + moving.Height, peer.Y + peer.Height, threshold, ref dy, ref gy);
            }
            // Keep snapped windows fully reachable, even when the peer is partly off screen.
            float x = Math.Clamp(moving.X + (gx.HasValue ? dx : 0), viewport.X, viewport.X + Math.Max(0, viewport.Width - moving.Width));
            float y = Math.Clamp(moving.Y + (gy.HasValue ? dy : 0), viewport.Y, viewport.Y + Math.Max(0, viewport.Height - moving.Height));
            if (gx.HasValue && Math.Abs(x - moving.X - dx) > 0.1f) gx = null;
            if (gy.HasValue && Math.Abs(y - moving.Y - dy) > 0.1f) gy = null;
            return new AlignmentResult(x, y, gx, gy);
        }

        private static void Match(float source, float target, float threshold, ref float delta, ref float? guide)
        {
            float candidate = target - source;
            if (Math.Abs(candidate) > threshold || Math.Abs(candidate) >= Math.Abs(delta)) return;
            delta = candidate; guide = target;
        }
    }
}
