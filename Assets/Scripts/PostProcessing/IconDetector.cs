using System;
using System.Collections.Generic;

namespace FloorplanVectoriser.PostProcessing
{
    /// <summary>
    /// Icon (furniture/fixture) detection from heatmaps.
    /// Ported from post_processing.py get_icon_polygon, find_icons, etc.
    /// </summary>
    public static class IconDetector
    {
        /// <summary>
        /// Detect icon polygons from heatmaps and icon segmentation.
        /// Ported from get_icon_polygon (Python L516-557).
        /// </summary>
        public static (List<int[,]> icons, List<Dictionary<string, object>> types)
            GetIconPolygons(float[,,] heatmaps, float[,,] iconsSeg, float threshold,
                int[][][] pointOrientations, int[][] orientationRanges)
        {
            int height = iconsSeg.GetLength(1);
            int width = iconsSeg.GetLength(2);

            var iconPoints = new List<int[]>();
            int[] channelOrder = { 3, 2, 0, 1 };
            for (int index = 0; index < channelOrder.Length; index++)
            {
                int i = channelOrder[index];
                int[] info = { 1, index };
                float[,] hm = Slice2D(heatmaps, i + 17);
                var pts = HeatmapProcessor.ExtractLocalMax(hm, 100, info, threshold,
                    closePointSuppression: true);
                iconPoints.AddRange(pts);
            }

            int gap = 10;
            var iconsFound = FindIcons(iconPoints, gap, pointOrientations, orientationRanges,
                height, width, false);
            var iconsGood = DropBigIcons(iconsFound, iconPoints);

            var iconPolygons = new List<int[,]>();
            var iconTypes = new List<Dictionary<string, object>>();

            foreach (var icon in iconsGood)
            {
                int[] p1 = iconPoints[icon[0]];
                int[] p2 = iconPoints[icon[1]];
                int[] p3 = iconPoints[icon[2]];
                int[] p4 = iconPoints[icon[3]];

                int x1 = (p1[0] + p3[0]) / 2;
                int x2 = (p2[0] + p4[0]) / 2;
                int y1 = (p1[1] + p2[1]) / 2;
                int y2 = (p3[1] + p4[1]) / 2;

                int iconArea = Math.Max(GetIconArea(icon, iconPoints), 1);

                // Sum evidence for each icon class within the bounding box
                int channels = iconsSeg.GetLength(0);
                float bestSum = 0;
                int iconClass = 0;
                for (int c = 0; c < channels; c++)
                {
                    float sum = 0;
                    for (int r = y1; r <= Math.Min(y2, height - 1); r++)
                        for (int cc = x1; cc <= Math.Min(x2, width - 1); cc++)
                            sum += iconsSeg[c, r, cc];
                    if (sum > bestSum) { bestSum = sum; iconClass = c; }
                }

                if (iconClass != 0)
                {
                    int[,] polygon = new int[4, 2];
                    polygon[0, 0] = x1; polygon[0, 1] = y1;
                    polygon[1, 0] = x2; polygon[1, 1] = y1;
                    polygon[2, 0] = x2; polygon[2, 1] = y2;
                    polygon[3, 0] = x1; polygon[3, 1] = y2;

                    iconPolygons.Add(polygon);
                    iconTypes.Add(new Dictionary<string, object>
                    {
                        { "type", "icon" },
                        { "class", iconClass },
                        { "prob", bestSum / iconArea }
                    });
                }
            }

            return (iconPolygons, iconTypes);
        }

        /// <summary>
        /// Find icon quadrilaterals by chaining 4 oriented neighbor lookups.
        /// Ported from find_icons (Python L1109-1198).
        /// </summary>
        public static List<int[]> FindIcons(
            List<int[]> points, int gap,
            int[][][] pointOrientations, int[][] orientationRanges,
            int height, int width, bool minDistanceOnly,
            int maxLengthX = 10000, int maxLengthY = 10000)
        {
            // Build per-point orientation → neighbor map
            var neighborMap = new List<Dictionary<int, List<int>>>();
            for (int i = 0; i < points.Count; i++)
            {
                int pointType = points[i][2];
                int subIndex = points[i][3];
                int[] orientations = pointOrientations[pointType][subIndex];
                var dict = new Dictionary<int, List<int>>();
                foreach (int o in orientations) dict[o] = new List<int>();
                neighborMap.Add(dict);
            }

            for (int pi = 0; pi < points.Count; pi++)
            {
                int[] point = points[pi];
                int[] orientations = pointOrientations[point[2]][point[3]];

                foreach (int orientation in orientations)
                {
                    int opposite = (orientation + 2) % 4;
                    int lineDim = (orientation == 0 || orientation == 2) ? 1 : 0;

                    int[] ranges = new int[4];
                    Array.Copy(orientationRanges[orientation], ranges, 4);
                    int[] deltas = { 0, 0 };
                    if (lineDim == 1) deltas[0] = gap; else deltas[1] = gap;
                    for (int c = 0; c < 2; c++)
                    {
                        ranges[c] = Math.Min(ranges[c], point[c] - deltas[c]);
                        ranges[c + 2] = Math.Max(ranges[c + 2], point[c] + deltas[c]);
                    }

                    int minDist = Math.Max(width, height);
                    int minDistNeighbor = -1;
                    var candidates = new List<int>();

                    for (int ni = pi + 1; ni < points.Count; ni++)
                    {
                        int[] neighbor = points[ni];
                        int[] nOrients = pointOrientations[neighbor[2]][neighbor[3]];
                        bool hasOpp = false;
                        foreach (int no in nOrients) if (no == opposite) { hasOpp = true; break; }
                        if (!hasOpp) continue;

                        bool inRange = true;
                        for (int c = 0; c < 2; c++)
                            if (neighbor[c] < ranges[c] || neighbor[c] > ranges[c + 2])
                            { inRange = false; break; }
                        if (!inRange) continue;

                        int absDim = Math.Abs(neighbor[lineDim] - point[lineDim]);
                        int absOther = Math.Abs(neighbor[1 - lineDim] - point[1 - lineDim]);
                        if (absDim < Math.Max(absOther, gap)) continue;

                        int distance = absDim;
                        int maxLen = lineDim == 0 ? maxLengthX : maxLengthY;
                        if (distance > maxLen) continue;

                        if (minDistanceOnly)
                        {
                            if (distance < minDist) { minDist = distance; minDistNeighbor = ni; }
                        }
                        else
                        {
                            candidates.Add(ni);
                        }
                    }

                    if (minDistanceOnly && minDistNeighbor >= 0)
                        candidates.Add(minDistNeighbor);

                    foreach (int ni in candidates)
                    {
                        neighborMap[pi][orientation].Add(ni);
                        if (neighborMap[ni].ContainsKey(opposite))
                            neighborMap[ni][opposite].Add(pi);
                    }
                }
            }

            // Search for quadrilaterals using ordered orientations (1, 2, 3, 0)
            var icons = new List<int[]>();
            int[] orderedOrient = { 1, 2, 3, 0 };

            for (int p1 = 0; p1 < neighborMap.Count; p1++)
            {
                if (!neighborMap[p1].ContainsKey(orderedOrient[0])) continue;
                int lastOpposite = (orderedOrient[3] + 2) % 4;
                if (!neighborMap[p1].ContainsKey(lastOpposite)) continue;

                var p4Candidates = neighborMap[p1][lastOpposite];

                foreach (int p2 in neighborMap[p1][orderedOrient[0]])
                {
                    if (!neighborMap[p2].ContainsKey(orderedOrient[1])) continue;
                    foreach (int p3 in neighborMap[p2][orderedOrient[1]])
                    {
                        if (!neighborMap[p3].ContainsKey(orderedOrient[2])) continue;
                        foreach (int p4 in neighborMap[p3][orderedOrient[2]])
                        {
                            if (p4Candidates.Contains(p4))
                            {
                                float avgConf = (points[p1][4] + points[p2][4] +
                                                 points[p3][4] + points[p4][4]) / 4f;
                                icons.Add(new[] { p1, p2, p4, p3, (int)avgConf });
                            }
                        }
                    }
                }
            }

            return icons;
        }

        /// <summary>Ported from drop_big_icons (Python L708-730).</summary>
        static List<int[]> DropBigIcons(List<int[]> icons, List<int[]> iconPoints)
        {
            var bad = new HashSet<int>();
            var remaining = new List<int[]>();

            for (int i = 0; i < icons.Count; i++)
            {
                for (int j = i + 1; j < icons.Count; j++)
                {
                    if (!bad.Contains(i) && !bad.Contains(j))
                    {
                        if (IconsSameCorner(icons[i], icons[j]))
                        {
                            int area1 = GetIconArea(icons[i], iconPoints);
                            int area2 = GetIconArea(icons[j], iconPoints);
                            int[] good;
                            if (area1 <= area2) { good = icons[i]; bad.Add(j); }
                            else { good = icons[j]; bad.Add(i); }
                            if (!remaining.Contains(good)) remaining.Add(good);
                        }
                        else
                        {
                            if (!remaining.Contains(icons[i]) && !bad.Contains(i))
                                remaining.Add(icons[i]);
                            if (!remaining.Contains(icons[j]) && !bad.Contains(j))
                                remaining.Add(icons[j]);
                        }
                    }
                    else
                    {
                        if (!remaining.Contains(icons[i]) && !bad.Contains(i))
                            remaining.Add(icons[i]);
                        if (!remaining.Contains(icons[j]) && !bad.Contains(j))
                            remaining.Add(icons[j]);
                    }
                }
            }

            // If no combinations tested, keep all
            if (icons.Count > 0 && remaining.Count == 0 && bad.Count == 0)
                remaining.AddRange(icons);

            var result = new List<int[]>();
            foreach (var icon in remaining)
                if (!bad.Contains(icons.IndexOf(icon))) result.Add(icon);
            return result;
        }

        /// <summary>Ported from icons_same_corner (Python L733-737).</summary>
        static bool IconsSameCorner(int[] icon1, int[] icon2)
        {
            for (int i = 0; i < 4; i++)
                if (icon1[i] == icon2[i]) return true;
            return false;
        }

        /// <summary>Ported from get_icon_area (Python L898-909).</summary>
        public static int GetIconArea(int[] icon, List<int[]> iconPoints)
        {
            int[] p1 = iconPoints[icon[0]];
            int[] p2 = iconPoints[icon[1]];
            int[] p3 = iconPoints[icon[2]];
            int[] p4 = iconPoints[icon[3]];
            int x1 = (p1[0] + p3[0]) / 2;
            int x2 = (p2[0] + p4[0]) / 2;
            int y1 = (p1[1] + p2[1]) / 2;
            int y2 = (p3[1] + p4[1]) / 2;
            return (x2 - x1) * (y2 - y1);
        }

        static float[,] Slice2D(float[,,] arr, int channel)
        {
            int h = arr.GetLength(1), w = arr.GetLength(2);
            float[,] result = new float[h, w];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    result[r, c] = arr[channel, r, c];
            return result;
        }
    }
}
