using System;
using System.Collections.Generic;

namespace FloorplanVectoriser.PostProcessing
{
    /// <summary>
    /// Pure geometry helpers ported from floortrans/post_processing.py.
    /// All methods are static and have no Unity dependencies.
    /// </summary>
    public static class GeometryUtils
    {
        /// <summary>Bresenham line rasterisation. Returns list of (row, col) pairs.</summary>
        public static List<(int row, int col)> BresenhamLine(int x0, int y0, int x1, int y1)
        {
            int dx = x1 - x0;
            int dy = y1 - y0;
            int xsign = dx > 0 ? 1 : -1;
            int ysign = dy > 0 ? 1 : -1;
            dx = Math.Abs(dx);
            dy = Math.Abs(dy);

            int xx, xy, yx, yy;
            if (dx > dy)
            {
                xx = xsign; xy = 0; yx = 0; yy = ysign;
            }
            else
            {
                int tmp = dx; dx = dy; dy = tmp;
                xx = 0; xy = ysign; yx = xsign; yy = 0;
            }

            int D = 2 * dy - dx;
            int y = 0;
            var result = new List<(int, int)>(dx + 1);
            for (int x = 0; x <= dx; x++)
            {
                result.Add((y0 + x * xy + y * yy, x0 + x * xx + y * yx));
                if (D >= 0)
                {
                    y += 1;
                    D -= 2 * dx;
                }
                D += 2 * dy;
            }
            return result;
        }

        /// <summary>Returns 0 for horizontal line, 1 for vertical.</summary>
        public static int CalcLineDim(List<int[]> points, int idx1, int idx2)
        {
            int[] p1 = points[idx1];
            int[] p2 = points[idx2];
            return (p2[0] - p1[0] > p2[1] - p1[1]) ? 0 : 1;
        }

        /// <summary>Overload accepting a line tuple (idx1, idx2, wallType).</summary>
        public static int CalcLineDim(List<int[]> points, (int, int, int) line)
        {
            return CalcLineDim(points, line.Item1, line.Item2);
        }

        /// <summary>Overload for 2-tuple lines.</summary>
        public static int CalcLineDim(List<int[]> points, (int, int) line)
        {
            return CalcLineDim(points, line.Item1, line.Item2);
        }

        /// <summary>Returns 0 for horizontal polygon, 1 for vertical.</summary>
        public static int CalcPolygonDim(int[,] polygon)
        {
            int x1 = polygon[0, 0];
            int x2 = polygon[1, 0];
            int y1 = polygon[0, 1];
            int y2 = polygon[2, 1];
            return (Math.Abs(x2 - x1) > Math.Abs(y2 - y1)) ? 0 : 1;
        }

        /// <summary>Intersection distance of two axis-aligned bounding boxes.</summary>
        public static float PolygonIntersection(
            int xMin, int xMax, int yMin, int yMax,
            int xMinLabel, int xMaxLabel, int yMinLabel, int yMaxLabel)
        {
            if (xMax > xMinLabel && xMaxLabel > xMin &&
                yMax > yMinLabel && yMaxLabel > yMin)
            {
                int xMinn = Math.Max(xMin, xMinLabel);
                int xMaxx = Math.Min(xMax, xMaxLabel);
                int yMinn = Math.Max(yMin, yMinLabel);
                int yMaxx = Math.Min(yMax, yMaxLabel);
                return (float)Math.Sqrt((xMaxx - xMinn) * (xMaxx - xMinn) +
                                        (yMaxx - yMinn) * (yMaxx - yMinn));
            }
            return 0f;
        }

        /// <summary>Line-line intersection point.</summary>
        public static int[] GetIntersect(int[] p11, int[] p12, int[] p21, int[] p22)
        {
            if (p21[0] == p22[0] && p21[1] == p22[1])
                return new[] { p21[0], p21[1] };

            float x1 = p11[0], y1 = p11[1];
            float x2 = p12[0], y2 = p12[1];
            float x3 = p21[0], y3 = p21[1];
            float x4 = p22[0], y4 = p22[1];

            float a = x1 * y2 - y1 * x2;
            float b = x3 * y4 - y3 * x4;
            float c = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);

            if (Math.Abs(c) < 1e-10f)
                return new[] { (int)((p11[0] + p21[0]) / 2f), (int)((p11[1] + p21[1]) / 2f) };

            int px = (int)Math.Round((a * (x3 - x4) - (x1 - x2) * b) / c);
            int py = (int)Math.Round((a * (y3 - y4) - (y1 - y2) * b) / c);
            return new[] { px, py };
        }

        /// <summary>Check if both points are inside an axis-aligned polygon.</summary>
        public static bool PointsInPolygon(int[] p1, int[] p2, int[,] polygon)
        {
            return PointInsidePolygon(p1, polygon) && PointInsidePolygon(p2, polygon);
        }

        /// <summary>Axis-aligned rectangle containment test.</summary>
        public static bool PointInsidePolygon(int[] p, int[,] polygon)
        {
            int x = p[0], y = p[1];
            return x >= polygon[0, 0] && x >= polygon[3, 0] &&
                   x <= polygon[1, 0] && x <= polygon[2, 0] &&
                   y >= polygon[0, 1] && y >= polygon[1, 1] &&
                   y <= polygon[2, 1] && y <= polygon[3, 1];
        }

        public static bool RectanglesOverlap(int[,] r1, int[,] r2)
        {
            return RangeOverlap(MinCol(r1, 0), MaxCol(r1, 0), MinCol(r2, 0), MaxCol(r2, 0)) &&
                   RangeOverlap(MinCol(r1, 1), MaxCol(r1, 1), MinCol(r2, 1), MaxCol(r2, 1));
        }

        public static bool RangeOverlap(int aMin, int aMax, int bMin, int bMax)
        {
            return (aMin <= bMax) && (bMin <= aMax);
        }

        public static int RectangleSize(int[,] r)
        {
            int xRange = MaxCol(r, 0) - MinCol(r, 0);
            int yRange = MaxCol(r, 1) - MinCol(r, 1);
            return xRange * yRange;
        }

        public static float GetWallLength(List<int[]> wallPoints, int idx1, int idx2)
        {
            int[] p1 = wallPoints[idx1];
            int[] p2 = wallPoints[idx2];
            float dx = p1[0] - p2[0];
            float dy = p1[1] - p2[1];
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Argmax along channel axis at a single pixel location.</summary>
        public static int GetPxlClass(int x, int y, float[,,] segmentation)
        {
            int channels = segmentation.GetLength(0);
            int best = 0;
            float bestVal = segmentation[0, y, x];
            for (int c = 1; c < channels; c++)
            {
                if (segmentation[c, y, x] > bestVal)
                {
                    bestVal = segmentation[c, y, x];
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Statistical mode: most frequent integer value in an array.</summary>
        public static float StatsMode(float[] values)
        {
            if (values.Length == 0) return 0;
            int[] intVals = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
                intVals[i] = (int)Math.Round(values[i]);
            Array.Sort(intVals);

            int bestVal = intVals[0], bestCount = 1;
            int curVal = intVals[0], curCount = 1;
            for (int i = 1; i < intVals.Length; i++)
            {
                if (intVals[i] == curVal)
                {
                    curCount++;
                }
                else
                {
                    if (curCount > bestCount) { bestCount = curCount; bestVal = curVal; }
                    curVal = intVals[i];
                    curCount = 1;
                }
            }
            if (curCount > bestCount) bestVal = curVal;
            return bestVal;
        }

        // --- Helpers for min/max of polygon columns ---

        public static int MinCol(int[,] polygon, int col)
        {
            int rows = polygon.GetLength(0);
            int min = polygon[0, col];
            for (int r = 1; r < rows; r++)
                if (polygon[r, col] < min) min = polygon[r, col];
            return min;
        }

        public static int MaxCol(int[,] polygon, int col)
        {
            int rows = polygon.GetLength(0);
            int max = polygon[0, col];
            for (int r = 1; r < rows; r++)
                if (polygon[r, col] > max) max = polygon[r, col];
            return max;
        }
    }
}
