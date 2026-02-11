using System;
using System.Collections.Generic;

namespace FloorplanVectoriser.PostProcessing
{
    /// <summary>
    /// Wall detection chain ported from post_processing.py.
    /// Detects wall line segments from heatmaps, extracts wall polygons,
    /// fixes corners, and removes overlapping walls.
    /// </summary>
    public static class WallDetector
    {
        /// <summary>
        /// Main entry: extract wall polygons from heatmaps and room segmentation.
        /// Ported from get_wall_polygon (Python L19-43).
        /// </summary>
        public static (
            List<int[,]> walls,
            List<Dictionary<string, object>> types,
            List<int[]> wallPoints,
            List<(int, int, int)> wallLines,
            List<Dictionary<int, List<int>>> orientationMap
        ) GetWallPolygons(
            float[,,] wallHeatmaps, float[,,] roomSeg, float threshold,
            int[] wallClasses, int[][][] pointOrientations, int[][] orientationRanges)
        {
            var (wallLines, wallPoints, orientationMap) = GetWallLines(
                wallHeatmaps, roomSeg, threshold, wallClasses,
                pointOrientations, orientationRanges);

            var walls = new List<int[,]>();
            var types = new List<Dictionary<string, object>>();
            var wallLinesNew = new List<(int, int, int)>();

            for (int idx = 0; idx < wallLines.Count; idx++)
            {
                var line = wallLines[idx];
                var res = ExtractWallPolygon(line, wallPoints, roomSeg, wallClasses);
                if (res.HasValue)
                {
                    walls.Add(res.Value.polygon);
                    types.Add(new Dictionary<string, object>
                    {
                        { "type", "wall" },
                        { "class", line.Item3 }
                    });
                    wallLinesNew.Add(line);
                }
            }

            FixWallCorners(walls, wallPoints, wallLinesNew);
            RemoveOverlappingWalls(ref walls, ref types, ref wallLinesNew);

            return (walls, types, wallPoints, wallLinesNew, orientationMap);
        }

        /// <summary>
        /// Get wall line segments from heatmap peaks.
        /// Ported from get_wall_lines (Python L195-230).
        /// </summary>
        static (List<(int, int, int)> lines, List<int[]> points, List<Dictionary<int, List<int>>> orientationMap)
            GetWallLines(
                float[,,] wallHeatmaps, float[,,] roomSeg, float threshold,
                int[] wallClasses, int[][][] pointOrientations, int[][] orientationRanges,
                int maxNumPoints = 100)
        {
            int height = roomSeg.GetLength(1);
            int width = roomSeg.GetLength(2);
            int gap = 10;

            var wallPoints = new List<int[]>();
            int heatmapCount = wallHeatmaps.GetLength(0);
            for (int i = 0; i < heatmapCount; i++)
            {
                int[] info = { i / 4, i % 4 };
                float[,] hm = Slice2D(wallHeatmaps, i);
                var pts = HeatmapProcessor.ExtractLocalMax(hm, maxNumPoints, info, threshold,
                    closePointSuppression: true);
                wallPoints.AddRange(pts);
            }

            var (rawLines, orientationMap, _) = PointAnalysis.CalcPointInfo(
                wallPoints, gap, pointOrientations, orientationRanges, height, width);

            // Filter: only keep lines where the room segmentation along the line is a wall class
            var goodWallLines = new List<(int, int, int)>();
            foreach (var (i1, i2) in rawLines)
            {
                int[] p1 = wallPoints[i1];
                int[] p2 = wallPoints[i2];
                var linePxls = GeometryUtils.BresenhamLine(p1[0], p1[1], p2[0], p2[1]);

                // Sum room segmentation along line, find dominant class
                int channels = roomSeg.GetLength(0);
                float[] channelSums = new float[channels];
                foreach (var (row, col) in linePxls)
                {
                    int cr = Math.Max(0, Math.Min(row, height - 1));
                    int cc = Math.Max(0, Math.Min(col, width - 1));
                    for (int c = 0; c < channels; c++)
                        channelSums[c] += roomSeg[c, cr, cc];
                }
                int segment = ArgMax(channelSums);

                bool isWallClass = false;
                foreach (int wc in wallClasses)
                    if (segment == wc) { isWallClass = true; break; }

                if (isWallClass)
                    goodWallLines.Add((i1, i2, segment));
            }

            // Drop long walls
            var filteredLines = DropLongWalls(goodWallLines, wallPoints);

            // Separate vertical and horizontal walls
            var vWalls = new List<(int, int, int)>();
            var hWalls = new List<(int, int, int)>();
            foreach (var line in filteredLines)
            {
                if (GeometryUtils.CalcLineDim(wallPoints, line) == 1)
                    vWalls.Add(line);
                else
                    hWalls.Add(line);
            }

            // Manhattan alignment
            var connectedV = GetConnectedWalls(vWalls);
            wallPoints = PointsToManhattan(connectedV, wallPoints, 0);
            var connectedH = GetConnectedWalls(hWalls);
            wallPoints = PointsToManhattan(connectedH, wallPoints, 1);

            return (filteredLines, wallPoints, orientationMap);
        }

        /// <summary>
        /// Extract a wall polygon (4-point rectangle) with measured wall width.
        /// Ported from extract_wall_polygon (Python L776-885).
        /// </summary>
        static (float wallWidth, int[,] polygon)? ExtractWallPolygon(
            (int, int, int) wall, List<int[]> wallPoints,
            float[,,] segmentation, int[] segClass)
        {
            int maxHeight = segmentation.GetLength(1);
            int maxWidth = segmentation.GetLength(2);
            int x1 = wallPoints[wall.Item1][0];
            int x2 = wallPoints[wall.Item2][0];
            int y1 = wallPoints[wall.Item1][1];
            int y2 = wallPoints[wall.Item2][1];
            int wDim = GeometryUtils.CalcLineDim(wallPoints, wall);

            var linePxls = GeometryUtils.BresenhamLine(x1, y1, x2, y2);
            var widths = new List<float>();

            if (wDim == 1) // vertical line
            {
                foreach (var (row, col) in linePxls)
                {
                    int wPos = 0, wNeg = 0;
                    int j0 = row, i0 = col;

                    // Scan positive direction
                    bool con = true;
                    while (con && i0 < maxWidth - 1)
                    {
                        int i1 = i0 + 1;
                        int pxlClass = GeometryUtils.GetPxlClass(i1, j0, segmentation);
                        if (IsInArray(pxlClass, segClass)) wPos++;
                        else con = false;
                        i0 = i1;
                    }

                    // Scan negative direction
                    j0 = row; i0 = col;
                    con = true;
                    while (con && i0 > 0)
                    {
                        int i1 = i0 - 1;
                        int pxlClass = GeometryUtils.GetPxlClass(i1, j0, segmentation);
                        if (IsInArray(pxlClass, segClass)) wNeg++;
                        else con = false;
                        i0 = i1;
                    }

                    widths.Add(wPos + wNeg + 1);
                }

                if (widths.Count == 0) return null;
                float wallWidth = GeometryUtils.StatsMode(widths.ToArray());
                if (wallWidth > y2 - y1) wallWidth = y2 - y1;
                int wDelta = (int)(wallWidth / 2.0f);
                if (wDelta == 0) return null;

                int[,] polygon = new int[4, 2];
                polygon[0, 0] = x1 - wDelta; polygon[0, 1] = y1;     // up-left
                polygon[1, 0] = x1 + wDelta; polygon[1, 1] = y1;     // up-right
                polygon[2, 0] = x2 + wDelta; polygon[2, 1] = y2;     // down-right
                polygon[3, 0] = x2 - wDelta; polygon[3, 1] = y2;     // down-left
                ClipPolygon(polygon, maxWidth, maxHeight);
                return (wallWidth, polygon);
            }
            else // horizontal line
            {
                foreach (var (row, col) in linePxls)
                {
                    int wPos = 0, wNeg = 0;
                    int j0 = row, i0 = col;

                    bool con = true;
                    while (con && j0 < maxHeight - 1)
                    {
                        int j1 = j0 + 1;
                        int pxlClass = GeometryUtils.GetPxlClass(i0, j1, segmentation);
                        if (IsInArray(pxlClass, segClass)) wPos++;
                        else con = false;
                        j0 = j1;
                    }

                    j0 = row; i0 = col;
                    con = true;
                    while (con && j0 > 0)
                    {
                        int j1 = j0 - 1;
                        int pxlClass = GeometryUtils.GetPxlClass(i0, j1, segmentation);
                        if (IsInArray(pxlClass, segClass)) wNeg++;
                        else con = false;
                        j0 = j1;
                    }

                    widths.Add(wPos + wNeg + 1);
                }

                if (widths.Count == 0) return null;
                float wallWidth = GeometryUtils.StatsMode(widths.ToArray());
                if (wallWidth > x2 - x1) wallWidth = x2 - x1;
                int wDelta = (int)(wallWidth / 2.0f);
                if (wDelta == 0) return null;

                int[,] polygon = new int[4, 2];
                polygon[0, 0] = x1; polygon[0, 1] = y1 - wDelta;     // up-left
                polygon[1, 0] = x2; polygon[1, 1] = y2 - wDelta;     // up-right
                polygon[2, 0] = x2; polygon[2, 1] = y2 + wDelta;     // down-right
                polygon[3, 0] = x1; polygon[3, 1] = y1 + wDelta;     // down-left
                ClipPolygon(polygon, maxWidth, maxHeight);
                return (wallWidth, polygon);
            }
        }

        /// <summary>Ported from fix_wall_corners (Python L142-192).</summary>
        static void FixWallCorners(List<int[,]> walls, List<int[]> wallPoints,
            List<(int, int, int)> wallLines)
        {
            for (int i = 0; i < wallPoints.Count; i++)
            {
                int x = wallPoints[i][0], y = wallPoints[i][1];
                int rightIdx = -1, leftIdx = -1, upIdx = -1, downIdx = -1;

                for (int j = 0; j < wallLines.Count; j++)
                {
                    var (p1, p2, _) = wallLines[j];
                    int dim = GeometryUtils.CalcLineDim(wallPoints, wallLines[j]);

                    if (dim == 0) // horizontal
                    {
                        if (p1 == i) rightIdx = j;
                        else if (p2 == i) leftIdx = j;
                    }
                    else // vertical
                    {
                        if (p1 == i) downIdx = j;
                        else if (p2 == i) upIdx = j;
                    }
                }

                if (rightIdx >= 0 && (downIdx >= 0 || upIdx >= 0))
                {
                    int x1 = downIdx >= 0 ? walls[downIdx][0, 0] : int.MaxValue;
                    int x2 = upIdx >= 0 ? walls[upIdx][0, 0] : int.MaxValue;
                    int newX = Math.Min(x1, x2);
                    walls[rightIdx][0, 0] = newX;
                    walls[rightIdx][3, 0] = newX;
                }

                if (leftIdx >= 0 && (downIdx >= 0 || upIdx >= 0))
                {
                    int x1 = downIdx >= 0 ? walls[downIdx][1, 0] : 0;
                    int x2 = upIdx >= 0 ? walls[upIdx][1, 0] : 0;
                    int newX = Math.Max(x1, x2);
                    walls[leftIdx][1, 0] = newX;
                    walls[leftIdx][2, 0] = newX;
                }

                if (upIdx >= 0 && (leftIdx >= 0 || rightIdx >= 0))
                {
                    int y1 = leftIdx >= 0 ? walls[leftIdx][3, 1] : int.MaxValue;
                    int y2 = rightIdx >= 0 ? walls[rightIdx][0, 1] : int.MaxValue;
                    int newY = Math.Min(y1, y2);
                    walls[upIdx][2, 1] = newY;
                    walls[upIdx][3, 1] = newY;
                }

                if (downIdx >= 0 && (leftIdx >= 0 || rightIdx >= 0))
                {
                    int y1 = leftIdx >= 0 ? walls[leftIdx][2, 1] : 0;
                    int y2 = rightIdx >= 0 ? walls[rightIdx][0, 1] : 0;
                    int newY = Math.Max(y1, y2);
                    walls[downIdx][0, 1] = newY;
                    walls[downIdx][1, 1] = newY;
                }
            }
        }

        /// <summary>Ported from remove_overlapping_walls (Python L60-100).</summary>
        static void RemoveOverlappingWalls(
            ref List<int[,]> walls,
            ref List<Dictionary<string, object>> types,
            ref List<(int, int, int)> wallLines)
        {
            float overlapThreshold = 0.4f;
            var toRemove = new HashSet<int>();

            for (int i = 0; i < walls.Count; i++)
            {
                int yMin1 = GeometryUtils.MinCol(walls[i], 1);
                int yMax1 = GeometryUtils.MaxCol(walls[i], 1);
                int xMin1 = GeometryUtils.MinCol(walls[i], 0);
                int xMax1 = GeometryUtils.MaxCol(walls[i], 0);
                float labelArea = (float)Math.Sqrt((xMax1 - xMin1) * (xMax1 - xMin1) +
                                                    (yMax1 - yMin1) * (yMax1 - yMin1));

                for (int j = i + 1; j < walls.Count; j++)
                {
                    int dim1 = GeometryUtils.CalcPolygonDim(walls[i]);
                    int dim2 = GeometryUtils.CalcPolygonDim(walls[j]);
                    if (dim1 != dim2) continue;

                    int yMin2 = GeometryUtils.MinCol(walls[j], 1);
                    int yMax2 = GeometryUtils.MaxCol(walls[j], 1);
                    int xMin2 = GeometryUtils.MinCol(walls[j], 0);
                    int xMax2 = GeometryUtils.MaxCol(walls[j], 0);

                    float intersection = GeometryUtils.PolygonIntersection(
                        xMin1, xMax1, yMin1, yMax1, xMin2, xMax2, yMin2, yMax2);
                    float predArea = (float)Math.Sqrt((xMax2 - xMin2) * (xMax2 - xMin2) +
                                                       (yMax2 - yMin2) * (yMax2 - yMin2));
                    float union = predArea + labelArea - intersection;
                    float iou = union > 0 ? intersection / union : 0;

                    if (iou > overlapThreshold)
                    {
                        if (labelArea > predArea) toRemove.Add(i);
                        else toRemove.Add(j);
                    }
                }
            }

            var newWalls = new List<int[,]>();
            var newTypes = new List<Dictionary<string, object>>();
            var newLines = new List<(int, int, int)>();
            for (int i = 0; i < walls.Count; i++)
            {
                if (!toRemove.Contains(i))
                {
                    newWalls.Add(walls[i]);
                    newTypes.Add(types[i]);
                    newLines.Add(wallLines[i]);
                }
            }
            walls = newWalls;
            types = newTypes;
            wallLines = newLines;
        }

        /// <summary>Ported from get_connected_walls (Python L560-578).</summary>
        static List<HashSet<int>> GetConnectedWalls(List<(int, int, int)> walls)
        {
            var wallList = new List<(int, int, int)>(walls);
            var connected = new List<HashSet<int>>();

            while (wallList.Count > 0)
            {
                var wall = wallList[0];
                wallList.RemoveAt(0);
                var wallInx = new HashSet<int> { wall.Item1, wall.Item2 };

                int i = 0;
                while (i < wallList.Count)
                {
                    var conInx = new HashSet<int> { wallList[i].Item1, wallList[i].Item2 };
                    bool overlaps = false;
                    foreach (int idx in wallInx)
                        if (conInx.Contains(idx)) { overlaps = true; break; }

                    if (overlaps)
                    {
                        foreach (int idx in conInx) wallInx.Add(idx);
                        wallList.RemoveAt(i);
                        i = 0; // restart scan
                    }
                    else
                    {
                        i++;
                    }
                }
                connected.Add(wallInx);
            }
            return connected;
        }

        /// <summary>Ported from points_to_manhattan (Python L581-589).</summary>
        static List<int[]> PointsToManhattan(List<HashSet<int>> connectedWalls,
            List<int[]> wallPoints, int lineDim)
        {
            // Deep copy points
            var newPoints = new List<int[]>(wallPoints.Count);
            foreach (var p in wallPoints)
                newPoints.Add((int[])p.Clone());

            foreach (var walls in connectedWalls)
            {
                float sum = 0;
                foreach (int i in walls) sum += wallPoints[i][lineDim];
                int newCoord = (int)Math.Round(sum / walls.Count);
                foreach (int i in walls) newPoints[i][lineDim] = newCoord;
            }
            return newPoints;
        }

        /// <summary>Ported from drop_long_walls (Python L740-762).</summary>
        static List<(int, int, int)> DropLongWalls(List<(int, int, int)> walls, List<int[]> wallPoints)
        {
            var bad = new HashSet<int>();
            var remaining = new List<int>();

            for (int i = 0; i < walls.Count; i++)
            {
                for (int j = i + 1; j < walls.Count; j++)
                {
                    if (!bad.Contains(i) && !bad.Contains(j) &&
                        WallsSameCorner(walls[i], walls[j], wallPoints))
                    {
                        float len1 = GeometryUtils.GetWallLength(wallPoints, walls[i].Item1, walls[i].Item2);
                        float len2 = GeometryUtils.GetWallLength(wallPoints, walls[j].Item1, walls[j].Item2);
                        if (len1 <= len2)
                        {
                            if (!remaining.Contains(i)) remaining.Add(i);
                            bad.Add(j);
                        }
                        else
                        {
                            if (!remaining.Contains(j)) remaining.Add(j);
                            bad.Add(i);
                        }
                    }
                    else
                    {
                        if (!remaining.Contains(i) && !bad.Contains(i)) remaining.Add(i);
                        if (!remaining.Contains(j) && !bad.Contains(j)) remaining.Add(j);
                    }
                }
            }

            // If no combinations were tested, keep all
            if (walls.Count > 0 && remaining.Count == 0 && bad.Count == 0)
            {
                for (int i = 0; i < walls.Count; i++) remaining.Add(i);
            }

            var result = new List<(int, int, int)>();
            foreach (int i in remaining)
                if (!bad.Contains(i)) result.Add(walls[i]);
            return result;
        }

        /// <summary>Ported from walls_same_corner (Python L765-773).</summary>
        static bool WallsSameCorner((int, int, int) wall1, (int, int, int) wall2, List<int[]> wallPoints)
        {
            int dim1 = GeometryUtils.CalcLineDim(wallPoints, wall1);
            int dim2 = GeometryUtils.CalcLineDim(wallPoints, wall2);
            if (dim1 != dim2) return false;
            return wall1.Item1 == wall2.Item1 || wall1.Item2 == wall2.Item2;
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

        static int ArgMax(float[] arr)
        {
            int best = 0;
            for (int i = 1; i < arr.Length; i++)
                if (arr[i] > arr[best]) best = i;
            return best;
        }

        static bool IsInArray(int val, int[] arr)
        {
            foreach (int a in arr) if (a == val) return true;
            return false;
        }

        static void ClipPolygon(int[,] polygon, int maxWidth, int maxHeight)
        {
            for (int i = 0; i < 4; i++)
            {
                polygon[i, 0] = Math.Max(0, Math.Min(polygon[i, 0], maxWidth));
                polygon[i, 1] = Math.Max(0, Math.Min(polygon[i, 1], maxHeight));
            }
        }
    }
}
