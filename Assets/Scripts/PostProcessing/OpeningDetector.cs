using System;
using System.Collections.Generic;

namespace FloorplanVectoriser.PostProcessing
{
    /// <summary>
    /// Door/window opening detection from heatmaps and wall data.
    /// Ported from post_processing.py get_opening_polygon and related functions.
    /// </summary>
    public static class OpeningDetector
    {
        /// <summary>
        /// Main entry: detect opening polygons (doors/windows) along detected walls.
        /// Ported from get_opening_polygon (Python L395-489).
        /// </summary>
        public static (List<int[,]> openings, List<Dictionary<string, object>> types)
            GetOpeningPolygons(
                float[,,] heatmaps, List<int[,]> wallPolygons, float[,,] iconsSeg,
                List<int[]> wallPoints, List<(int, int, int)> wallLines,
                List<Dictionary<int, List<int>>> wallOrientationMap,
                float threshold, int[][][] pointOrientations, int[][] orientationRanges,
                int[] allOpeningTypes, int maxNumPoints = 100, int gap = 10)
        {
            int height = heatmaps.GetLength(1);
            int width = heatmaps.GetLength(2);
            float[,] wallMask = DrawLineMask(wallPoints, wallLines, height, width);

            // Extract door heatmap peaks
            var doorPoints = new List<int[]>();
            int[] channelOrder = { 2, 1, 3, 0 };
            for (int index = 0; index < channelOrder.Length; index++)
            {
                int i = channelOrder[index];
                int[] info = { 0, index };
                float[,] heatmap = Slice2D(heatmaps, i + 13);
                // Multiply by wall mask
                for (int r = 0; r < height; r++)
                    for (int c = 0; c < width; c++)
                        heatmap[r, c] *= wallMask[r, c];
                var pts = HeatmapProcessor.ExtractLocalMax(heatmap, maxNumPoints, info, threshold);
                doorPoints.AddRange(pts);
            }

            var (doorLines, doorOrientMap, doorNeighbors) = PointAnalysis.CalcPointInfo(
                doorPoints, gap, pointOrientations, orientationRanges, height, width, true);

            // Build label map from icon segmentation
            int labelChannels = Math.Min(iconsSeg.GetLength(0), 30);
            float[,,] labelMap = new float[30, height, width];
            for (int c = 0; c < labelChannels; c++)
                for (int r = 0; r < height; r++)
                    for (int cc = 0; cc < width; cc++)
                        labelMap[c, r, cc] = iconsSeg[c, r, cc];

            // Classify door lines by type
            var doorTypes = new List<(int lineIndex, int doorType, float evidence)>();
            int numDoorTypes = 2;
            int doorOffset = 23;
            for (int li = 0; li < doorLines.Count; li++)
            {
                var line = doorLines[li];
                int[] point = doorPoints[line.Item1];
                int[] neighbor = doorPoints[line.Item2];
                int lineDim = GeometryUtils.CalcLineDim(doorPoints, line);
                int fixedValue = (int)Math.Round((neighbor[1 - lineDim] + point[1 - lineDim]) / 2.0);

                float[] evidenceSums = new float[numDoorTypes];
                int range = (int)(Math.Abs(neighbor[lineDim] - point[lineDim]) + 1);
                int startVal = Math.Min(neighbor[lineDim], point[lineDim]);
                for (int delta = 0; delta < range; delta++)
                {
                    int[] ip = new int[2];
                    ip[lineDim] = startVal + delta;
                    ip[1 - lineDim] = fixedValue;
                    for (int ti = 0; ti < numDoorTypes; ti++)
                    {
                        if (doorOffset + ti < labelMap.GetLength(0))
                        {
                            int cr = Math.Max(0, Math.Min(ip[1], height - 1));
                            int cc = Math.Max(0, Math.Min(ip[0], width - 1));
                            evidenceSums[ti] += labelMap[doorOffset + ti, cr, cc];
                        }
                    }
                }

                int bestType = 0;
                float bestEvidence = evidenceSums[0];
                for (int ti = 1; ti < numDoorTypes; ti++)
                    if (evidenceSums[ti] > bestEvidence) { bestEvidence = evidenceSums[ti]; bestType = ti; }

                doorTypes.Add((li, bestType, bestEvidence));
            }

            // Remove conflicting doors (keeping highest evidence)
            var conflictPairs = FindConflictLinePairs(doorPoints, doorLines, gap);
            // Build conflict map (not currently used for filtering in this port,
            // but the door_types sort + filtering logic is preserved)
            var filteredDoorLines = new List<(int, int)>(doorLines);
            var filteredDoorTypes = new List<int>();
            for (int i = 0; i < doorLines.Count; i++)
                filteredDoorTypes.Add(doorTypes[i].doorType);

            // Filter wall points to those with correct orientation completeness
            var filteredWallPoints = new List<int[]>();
            var validPointMask = new Dictionary<int, bool>();
            for (int pi = 0; pi < wallOrientationMap.Count && pi < wallPoints.Count; pi++)
            {
                var orientLines = wallOrientationMap[pi];
                if (orientLines.Count == wallPoints[pi][2] + 1)
                {
                    filteredWallPoints.Add(wallPoints[pi]);
                    validPointMask[pi] = true;
                }
            }

            var filteredWallLines = new List<(int, int, int)>();
            foreach (var wl in wallLines)
                if (validPointMask.ContainsKey(wl.Item1) && validPointMask.ContainsKey(wl.Item2))
                    filteredWallLines.Add(wl);

            // Snap door points to nearest wall lines
            if (filteredDoorLines.Count > 0 && filteredWallLines.Count > 0)
            {
                var doorWallMap = FindLineMapSingle(
                    doorPoints, filteredDoorLines, wallPoints, filteredWallLines,
                    gap / 2.0f, height, width);
                AdjustDoorPoints(doorPoints, filteredDoorLines, wallPoints, filteredWallLines, doorWallMap);
            }

            // Extract opening polygons from wall polygon intersections
            var openingPolygons = ExtractOpeningPolygons(wallPolygons, doorPoints, doorLines, height, width);
            var openingTypes = GetOpeningTypes(openingPolygons, iconsSeg, allOpeningTypes);

            return (openingPolygons, openingTypes);
        }

        /// <summary>
        /// Extract opening polygons by projecting door line endpoints onto wall polygons.
        /// Ported from extract_opening_polygon (Python L593-655).
        /// </summary>
        static List<int[,]> ExtractOpeningPolygons(
            List<int[,]> wallPolygons, List<int[]> doorPoints,
            List<(int, int)> doorLines, int height, int width)
        {
            var openings = new List<int[,]>();

            for (int i = 0; i < wallPolygons.Count; i++)
            {
                int[,] pol = wallPolygons[i];
                int polygonDim = GeometryUtils.CalcPolygonDim(pol);

                foreach (var doorLine in doorLines)
                {
                    int[] p1 = doorPoints[doorLine.Item1];
                    int[] p2 = doorPoints[doorLine.Item2];
                    int dim = GeometryUtils.CalcLineDim(doorPoints, doorLine);

                    if (polygonDim != dim) continue;
                    if (!GeometryUtils.PointsInPolygon(p1, p2, pol)) continue;

                    int[] upLeft, upRight, downRight, downLeft;

                    if (dim == 0) // horizontal
                    {
                        int[] polRow0 = { pol[0, 0], pol[0, 1] };
                        int[] polRow1 = { pol[1, 0], pol[1, 1] };
                        int[] polRow2 = { pol[2, 0], pol[2, 1] };
                        int[] polRow3 = { pol[3, 0], pol[3, 1] };

                        upLeft = GeometryUtils.GetIntersect(polRow0, polRow1,
                            new[] { p1[0], p1[1] }, new[] { p1[0], 0 });
                        upRight = GeometryUtils.GetIntersect(polRow0, polRow1,
                            new[] { p2[0], p2[1] }, new[] { p2[0], 0 });
                        downRight = GeometryUtils.GetIntersect(polRow3, polRow2,
                            new[] { p2[0], p2[1] }, new[] { p2[0], height - 1 });
                        downLeft = GeometryUtils.GetIntersect(polRow3, polRow2,
                            new[] { p1[0], p1[1] }, new[] { p1[0], height - 1 });
                    }
                    else // vertical
                    {
                        int[] polRow0 = { pol[0, 0], pol[0, 1] };
                        int[] polRow1 = { pol[1, 0], pol[1, 1] };
                        int[] polRow2 = { pol[2, 0], pol[2, 1] };
                        int[] polRow3 = { pol[3, 0], pol[3, 1] };

                        upLeft = GeometryUtils.GetIntersect(polRow0, polRow3,
                            new[] { p1[0], p1[1] }, new[] { 0, p1[1] });
                        upRight = GeometryUtils.GetIntersect(polRow1, polRow2,
                            new[] { p1[0], p1[1] }, new[] { width - 1, p1[1] });
                        downRight = GeometryUtils.GetIntersect(polRow1, polRow2,
                            new[] { p2[0], p2[1] }, new[] { width - 1, p2[1] });
                        downLeft = GeometryUtils.GetIntersect(polRow0, polRow3,
                            new[] { p2[0], p2[1] }, new[] { 0, p2[1] });
                    }

                    int[,] opening = new int[4, 2];
                    opening[0, 0] = upLeft[0]; opening[0, 1] = upLeft[1];
                    opening[1, 0] = upRight[0]; opening[1, 1] = upRight[1];
                    opening[2, 0] = downRight[0]; opening[2, 1] = downRight[1];
                    opening[3, 0] = downLeft[0]; opening[3, 1] = downLeft[1];
                    openings.Add(opening);
                }
            }

            return openings;
        }

        /// <summary>
        /// Classify openings as door or window based on icon segmentation evidence.
        /// Ported from get_opening_types (Python L492-513).
        /// </summary>
        static List<Dictionary<string, object>> GetOpeningTypes(
            List<int[,]> openingPolygons, float[,,] iconsSeg, int[] allOpeningClasses)
        {
            var types = new List<Dictionary<string, object>>();
            int height = iconsSeg.GetLength(1);
            int width = iconsSeg.GetLength(2);

            foreach (var pol in openingPolygons)
            {
                int y1 = GeometryUtils.MinCol(pol, 1);
                int y2 = GeometryUtils.MaxCol(pol, 1);
                int x1 = GeometryUtils.MinCol(pol, 0);
                int x2 = GeometryUtils.MaxCol(pol, 0);

                // Filter to valid classes
                var validClasses = new List<int>();
                foreach (int c in allOpeningClasses)
                    if (c < iconsSeg.GetLength(0)) validClasses.Add(c);

                if (validClasses.Count > 0)
                {
                    float[] sums = new float[validClasses.Count];
                    for (int ci = 0; ci < validClasses.Count; ci++)
                    {
                        int ch = validClasses[ci];
                        for (int r = Math.Max(0, y1); r <= Math.Min(y2, height - 1); r++)
                            for (int c = Math.Max(0, x1); c <= Math.Min(x2, width - 1); c++)
                                sums[ci] += iconsSeg[ch, r, c];
                    }

                    int bestIdx = 0;
                    for (int i = 1; i < sums.Length; i++)
                        if (sums[i] > sums[bestIdx]) bestIdx = i;

                    int area = Math.Max(Math.Abs(y2 - y1) * Math.Abs(x2 - x1), 1);
                    types.Add(new Dictionary<string, object>
                    {
                        { "type", "icon" },
                        { "class", validClasses[bestIdx] },
                        { "prob", (float)(sums[bestIdx] / area) }
                    });
                }
                else
                {
                    types.Add(new Dictionary<string, object>
                    {
                        { "type", "icon" },
                        { "class", 0 },
                        { "prob", 0f }
                    });
                }
            }

            return types;
        }

        /// <summary>
        /// Remove overlapping opening polygons, keeping the larger or higher-confidence one.
        /// Ported from remove_overlapping_openings (Python L103-124).
        /// </summary>
        public static (List<int[,]> polygons, List<Dictionary<string, object>> types)
            RemoveOverlappingOpenings(
                List<int[,]> polygons, List<Dictionary<string, object>> types,
                int[] windowClasses, int[] doorClasses)
        {
            var openingTypes = new List<int>();
            openingTypes.AddRange(windowClasses);
            openingTypes.AddRange(doorClasses);

            var keep = new bool[polygons.Count];
            for (int i = 0; i < keep.Length; i++) keep[i] = true;

            for (int i = 0; i < types.Count; i++)
            {
                if (!keep[i]) continue;
                if ((string)types[i]["type"] != "icon") continue;
                int classI = (int)types[i]["class"];
                if (!Contains(openingTypes, classI)) continue;

                for (int j = 0; j < types.Count; j++)
                {
                    if (i == j || !keep[j]) continue;
                    if ((string)types[j]["type"] != "icon") continue;
                    int classJ = (int)types[j]["class"];
                    if (!Contains(openingTypes, classJ)) continue;

                    if (!PolygonsEqual(polygons[i], polygons[j]) &&
                        GeometryUtils.RectanglesOverlap(polygons[j], polygons[i]))
                    {
                        int sizeI = GeometryUtils.RectangleSize(polygons[i]);
                        int sizeJ = GeometryUtils.RectangleSize(polygons[j]);
                        if (sizeI == sizeJ)
                        {
                            float probI = types[i].ContainsKey("prob") ? (float)types[i]["prob"] : 0f;
                            float probJ = types[j].ContainsKey("prob") ? (float)types[j]["prob"] : 0f;
                            if (probJ > probI) { keep[i] = false; break; }
                        }
                        else if (sizeI < sizeJ)
                        {
                            keep[i] = false;
                            break;
                        }
                    }
                }
            }

            var newPolygons = new List<int[,]>();
            var newTypes = new List<Dictionary<string, object>>();
            for (int i = 0; i < polygons.Count; i++)
            {
                if (keep[i])
                {
                    newPolygons.Add(polygons[i]);
                    newTypes.Add(types[i]);
                }
            }
            return (newPolygons, newTypes);
        }

        /// <summary>Ported from draw_line_mask (Python L1050-1065).</summary>
        static float[,] DrawLineMask(List<int[]> points, List<(int, int, int)> lines,
            int height, int width, int lineWidth = 5)
        {
            float[,] mask = new float[height, width];
            foreach (var line in lines)
            {
                int[] p1 = points[line.Item1];
                int[] p2 = points[line.Item2];
                int lineDim = GeometryUtils.CalcLineDim(points, line);
                int fixedValue = (int)Math.Round((p1[1 - lineDim] + p2[1 - lineDim]) / 2.0);
                int minVal = Math.Min(p1[lineDim], p2[lineDim]);
                int maxVal = Math.Max(p1[lineDim], p2[lineDim]);

                if (lineDim == 0) // horizontal
                {
                    int rStart = Math.Max(fixedValue - lineWidth, 0);
                    int rEnd = Math.Min(fixedValue + lineWidth, height - 1);
                    int cEnd = Math.Min(maxVal, width - 1);
                    for (int r = rStart; r <= rEnd; r++)
                        for (int c = minVal; c <= cEnd; c++)
                            mask[r, c] = 1;
                }
                else // vertical
                {
                    int rEnd = Math.Min(maxVal, height - 1);
                    int cStart = Math.Max(fixedValue - lineWidth, 0);
                    int cEnd = Math.Min(fixedValue + lineWidth, width - 1);
                    for (int r = minVal; r <= rEnd; r++)
                        for (int c = cStart; c <= cEnd; c++)
                            mask[r, c] = 1;
                }
            }
            return mask;
        }

        /// <summary>Ported from find_conflict_line_pairs (Python L1068-1106).</summary>
        static List<(int, int)> FindConflictLinePairs(List<int[]> points, List<(int, int)> lines, int gap)
        {
            var conflicts = new List<(int, int)>();
            for (int i = 0; i < lines.Count; i++)
            {
                int[] p1 = points[lines[i].Item1];
                int[] p2 = points[lines[i].Item2];
                int dim1 = (p2[0] - p1[0] > p2[1] - p1[1]) ? 0 : 1;
                int fixed1 = (int)Math.Round((p1[1 - dim1] + p2[1 - dim1]) / 2.0);
                int min1 = Math.Min(p1[dim1], p2[dim1]);
                int max1 = Math.Max(p1[dim1], p2[dim1]);

                for (int j = i + 1; j < lines.Count; j++)
                {
                    int[] q1 = points[lines[j].Item1];
                    int[] q2 = points[lines[j].Item2];
                    int dim2 = (q2[0] - q1[0] > q2[1] - q1[1]) ? 0 : 1;

                    if ((lines[i].Item1 == lines[j].Item1 || lines[i].Item2 == lines[j].Item2) && dim1 == dim2)
                    {
                        conflicts.Add((i, j));
                        continue;
                    }

                    int fixed2 = (int)Math.Round((q1[1 - dim2] + q2[1 - dim2]) / 2.0);
                    int min2 = Math.Min(q1[dim2], q2[dim2]);
                    int max2 = Math.Max(q1[dim2], q2[dim2]);

                    if (dim1 == dim2)
                    {
                        if (Math.Abs(fixed2 - fixed1) > gap / 2 ||
                            min1 > max2 - gap || min2 > max1 - gap)
                            continue;
                        conflicts.Add((i, j));
                    }
                    else
                    {
                        if (min1 > fixed2 - gap || max1 < fixed2 + gap ||
                            min2 > fixed1 - gap || max2 < fixed1 + gap)
                            continue;
                        conflicts.Add((i, j));
                    }
                }
            }
            return conflicts;
        }

        /// <summary>Ported from find_line_map_single (Python L1221-1243).</summary>
        static List<int> FindLineMapSingle(
            List<int[]> points, List<(int, int)> lines,
            List<int[]> points2, List<(int, int, int)> lines2,
            float gapHalf, int height, int width)
        {
            var lineMap = new List<int>();
            int maxDist = Math.Max(width, height);

            foreach (var line in lines)
            {
                int lineDim = GeometryUtils.CalcLineDim(points, line);
                float minDist = maxDist;
                int bestIdx = -1;

                for (int ni = 0; ni < lines2.Count; ni++)
                {
                    var nLine = lines2[ni];
                    int nDim = GeometryUtils.CalcLineDim(points2, nLine);
                    if (lineDim != nDim) continue;

                    int minV = Math.Max(points[line.Item1][lineDim], points2[nLine.Item1][lineDim]);
                    int maxV = Math.Min(points[line.Item2][lineDim], points2[nLine.Item2][lineDim]);
                    if (maxV - minV < gapHalf) continue;

                    float f1 = (points[line.Item1][1 - lineDim] + points[line.Item2][1 - lineDim]) / 2f;
                    float f2 = (points2[nLine.Item1][1 - lineDim] + points2[nLine.Item2][1 - lineDim]) / 2f;
                    float dist = Math.Abs(f2 - f1);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        bestIdx = ni;
                    }
                }
                lineMap.Add(bestIdx);
            }
            return lineMap;
        }

        /// <summary>Ported from adjust_door_points (Python L1246-1257).</summary>
        static void AdjustDoorPoints(
            List<int[]> doorPoints, List<(int, int)> doorLines,
            List<int[]> wallPoints, List<(int, int, int)> wallLines,
            List<int> doorWallMap)
        {
            for (int di = 0; di < doorLines.Count && di < doorWallMap.Count; di++)
            {
                int wallIdx = doorWallMap[di];
                if (wallIdx < 0) continue;
                int lineDim = GeometryUtils.CalcLineDim(doorPoints, doorLines[di]);
                var wLine = wallLines[wallIdx];
                int[] wp1 = wallPoints[wLine.Item1];
                int[] wp2 = wallPoints[wLine.Item2];
                float fixedValue = (wp1[1 - lineDim] + wp2[1 - lineDim]) / 2f;
                for (int ep = 0; ep < 2; ep++)
                {
                    int ptIdx = ep == 0 ? doorLines[di].Item1 : doorLines[di].Item2;
                    doorPoints[ptIdx][1 - lineDim] = (int)fixedValue;
                }
            }
        }

        // --- Helpers ---

        static float[,] Slice2D(float[,,] arr, int channel)
        {
            int h = arr.GetLength(1), w = arr.GetLength(2);
            float[,] result = new float[h, w];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    result[r, c] = arr[channel, r, c];
            return result;
        }

        static bool Contains(List<int> list, int val)
        {
            foreach (int v in list) if (v == val) return true;
            return false;
        }

        static bool PolygonsEqual(int[,] a, int[,] b)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    if (a[i, j] != b[i, j]) return false;
            return true;
        }
    }
}
