using System;
using System.Collections.Generic;

namespace FloorplanVectoriser.PostProcessing
{
    /// <summary>
    /// Heatmap peak detection via non-maximum suppression.
    /// Ported from post_processing.py extract_local_max / maximum_suppression.
    /// </summary>
    public static class HeatmapProcessor
    {
        /// <summary>
        /// Extract local maxima from a 2D heatmap using iterative peak finding + flood-fill suppression.
        /// Each returned point is: [x, y, pointType, subIndex, heatmapValue*1000 (as int)].
        /// The heatmapValue is stored scaled to preserve precision in integer arrays.
        /// </summary>
        public static List<int[]> ExtractLocalMax(
            float[,] maskImg, int numPoints, int[] info,
            float threshold = 0.5f, bool closePointSuppression = false, int gap = 10)
        {
            int height = maskImg.GetLength(0);
            int width = maskImg.GetLength(1);

            // Work on a copy
            float[,] mask = (float[,])maskImg.Clone();
            var points = new List<int[]>();

            for (int pointIndex = 0; pointIndex < numPoints; pointIndex++)
            {
                // Find global max
                int bestY = 0, bestX = 0;
                float bestVal = mask[0, 0];
                for (int r = 0; r < height; r++)
                {
                    for (int c = 0; c < width; c++)
                    {
                        if (mask[r, c] > bestVal)
                        {
                            bestVal = mask[r, c];
                            bestY = r;
                            bestX = c;
                        }
                    }
                }

                if (bestVal <= threshold)
                    return points;

                // Store point: [x, y, info[0], info[1], value*1000]
                points.Add(new[] { bestX, bestY, info[0], info[1], (int)(bestVal * 1000) });

                MaximumSuppression(mask, bestX, bestY, threshold);

                if (closePointSuppression)
                {
                    int yStart = Math.Max(bestY - gap, 0);
                    int yEnd = Math.Min(bestY + gap, height - 1);
                    int xStart = Math.Max(bestX - gap, 0);
                    int xEnd = Math.Min(bestX + gap, width - 1);
                    for (int r = yStart; r <= yEnd; r++)
                        for (int c = xStart; c <= xEnd; c++)
                            mask[r, c] = 0;
                }
            }

            return points;
        }

        /// <summary>
        /// Flood-fill based maximum suppression starting from (x, y).
        /// Zeros all connected pixels above threshold that are <= the seed value.
        /// </summary>
        public static void MaximumSuppression(float[,] mask, int x, int y, float threshold)
        {
            int height = mask.GetLength(0);
            int width = mask.GetLength(1);

            var stack = new Stack<(int cx, int cy)>();
            stack.Push((x, y));
            mask[y, x] = -1;

            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                float value = mask[cy, cx] != -1 ? mask[cy, cx] : float.PositiveInfinity;

                ReadOnlySpan<(int dx, int dy)> deltas = stackalloc (int, int)[]
                {
                    (-1, 0), (1, 0), (0, -1), (0, 1)
                };

                foreach (var (dx, dy) in deltas)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                    {
                        float nv = mask[ny, nx];
                        if (nv > threshold && nv <= value)
                        {
                            mask[ny, nx] = -1;
                            stack.Push((nx, ny));
                        }
                    }
                }
            }
        }
    }
}
