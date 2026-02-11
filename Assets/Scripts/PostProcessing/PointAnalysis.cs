using System;
using System.Collections.Generic;

namespace FloorplanVectoriser.PostProcessing
{
    /// <summary>
    /// Point connectivity and orientation analysis.
    /// Ported from post_processing.py calc_point_info (L964-1047).
    /// </summary>
    public static class PointAnalysis
    {
        /// <summary>
        /// Determine which point pairs form lines based on orientation compatibility
        /// and spatial proximity.
        /// </summary>
        /// <returns>
        /// lines: pairs of point indices forming line segments.
        /// orientationMap: per-point dictionary of orientation → list of line indices.
        /// neighbors: per-point list of neighboring point indices.
        /// </returns>
        public static (
            List<(int, int)> lines,
            List<Dictionary<int, List<int>>> orientationMap,
            List<List<int>> neighbors
        ) CalcPointInfo(
            List<int[]> points, int gap,
            int[][][] pointOrientations, int[][] orientationRanges,
            int height, int width,
            bool minDistanceOnly = false, bool doubleDirection = false)
        {
            var lines = new List<(int, int)>();
            var orientationMap = new List<Dictionary<int, List<int>>>();
            var neighbors = new List<List<int>>();

            // Initialize per-point orientation maps and neighbor lists
            for (int i = 0; i < points.Count; i++)
            {
                int pointType = points[i][2];
                int subIndex = points[i][3];
                int[] orientations = pointOrientations[pointType][subIndex];
                var dict = new Dictionary<int, List<int>>();
                foreach (int o in orientations)
                    dict[o] = new List<int>();
                orientationMap.Add(dict);
                neighbors.Add(new List<int>());
            }

            // Find connected point pairs
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                int[] point = points[pointIndex];
                int pointType = point[2];
                int subIndex = point[3];
                int[] orientations = pointOrientations[pointType][subIndex];

                foreach (int orientation in orientations)
                {
                    int oppositeOrientation = (orientation + 2) % 4;
                    int lineDim = (orientation == 0 || orientation == 2) ? 1 : 0;

                    // Copy ranges and apply gap
                    int[] ranges = new int[4];
                    Array.Copy(orientationRanges[orientation], ranges, 4);

                    int[] deltas = { 0, 0 };
                    if (lineDim == 1)
                        deltas[0] = gap;
                    else
                        deltas[1] = gap;

                    for (int c = 0; c < 2; c++)
                    {
                        ranges[c] = Math.Min(ranges[c], point[c] - deltas[c]);
                        ranges[c + 2] = Math.Max(ranges[c + 2], point[c] + deltas[c]);
                    }

                    var neighborPoints = new List<int>();
                    int minDistance = Math.Max(width, height);
                    int minDistanceNeighbor = -1;

                    for (int ni = 0; ni < points.Count; ni++)
                    {
                        if ((!doubleDirection && ni <= pointIndex) || ni == pointIndex)
                            continue;

                        int[] neighbor = points[ni];
                        int nType = neighbor[2];
                        int nSub = neighbor[3];
                        int[] nOrientations = pointOrientations[nType][nSub];

                        bool hasOpposite = false;
                        foreach (int no in nOrientations)
                        {
                            if (no == oppositeOrientation) { hasOpposite = true; break; }
                        }
                        if (!hasOpposite) continue;

                        bool inRange = true;
                        for (int c = 0; c < 2; c++)
                        {
                            if (neighbor[c] < ranges[c] || neighbor[c] > ranges[c + 2])
                            { inRange = false; break; }
                        }
                        if (!inRange) continue;

                        int absDimDist = Math.Abs(neighbor[lineDim] - point[lineDim]);
                        int absOtherDist = Math.Abs(neighbor[1 - lineDim] - point[1 - lineDim]);
                        if (absDimDist < Math.Max(absOtherDist, 1))
                            continue;

                        if (minDistanceOnly)
                        {
                            int distance = absDimDist;
                            if (distance < minDistance)
                            {
                                minDistance = distance;
                                minDistanceNeighbor = ni;
                            }
                        }
                        else
                        {
                            neighborPoints.Add(ni);
                        }
                    }

                    if (minDistanceOnly && minDistanceNeighbor >= 0)
                        neighborPoints.Add(minDistanceNeighbor);

                    foreach (int ni in neighborPoints)
                    {
                        if (doubleDirection)
                        {
                            bool exists = false;
                            foreach (var l in lines)
                            {
                                if ((l.Item1 == pointIndex && l.Item2 == ni) ||
                                    (l.Item1 == ni && l.Item2 == pointIndex))
                                { exists = true; break; }
                            }
                            if (exists) continue;
                        }

                        int lineIndex = lines.Count;
                        orientationMap[pointIndex][orientation].Add(lineIndex);
                        if (orientationMap[ni].ContainsKey(oppositeOrientation))
                            orientationMap[ni][oppositeOrientation].Add(lineIndex);

                        neighbors[pointIndex].Add(ni);
                        neighbors[ni].Add(pointIndex);

                        int sumP = points[pointIndex][0] + points[pointIndex][1];
                        int sumN = points[ni][0] + points[ni][1];
                        if (sumP < sumN)
                            lines.Add((pointIndex, ni));
                        else
                            lines.Add((ni, pointIndex));
                    }
                }
            }

            return (lines, orientationMap, neighbors);
        }
    }
}
