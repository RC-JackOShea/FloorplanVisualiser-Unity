using System;
using System.Collections.Generic;
using FloorplanVectoriser.Data;
using UnityEngine;

namespace FloorplanVectoriser.PostProcessing
{
    /// <summary>
    /// Orchestrator that runs the full post-processing pipeline to extract
    /// wall, door, and window polygons from model predictions.
    /// Ported from get_polygons() in post_processing.py (L291-376).
    /// </summary>
    public class PostProcessor
    {
        readonly float _threshold;
        readonly int[] _allOpeningTypes = { 1, 2 }; // 1=window, 2=door

        // Point orientation constants (from Python L309-316)
        static readonly int[][][] PointOrientations = new int[][][]
        {
            new int[][] { new[]{2}, new[]{3}, new[]{0}, new[]{1} },
            new int[][] { new[]{0,3}, new[]{0,1}, new[]{1,2}, new[]{2,3} },
            new int[][] { new[]{1,2,3}, new[]{0,2,3}, new[]{0,1,3}, new[]{0,1,2} },
            new int[][] { new[]{0,1,2,3} }
        };

        readonly int[][] _orientationRanges;

        public PostProcessor(float threshold = 0.5f)
        {
            _threshold = threshold;
            // Orientation ranges are set per-call since they depend on image dimensions
            _orientationRanges = null;
        }

        /// <summary>
        /// Run the full post-processing pipeline.
        /// </summary>
        /// <param name="heatmaps">21-channel heatmaps [21,H,W]</param>
        /// <param name="rooms">12-channel room segmentation [12,H,W]</param>
        /// <param name="icons">11-channel icon segmentation [11,H,W]</param>
        /// <returns>PolygonResult with normalized [0,1] polygon coordinates.</returns>
        public PolygonResult Process(float[,,] heatmaps, float[,,] rooms, float[,,] icons)
        {
            int height = icons.GetLength(1);
            int width = icons.GetLength(2);

            // Build orientation ranges for this image size
            int[][] orientationRanges = new int[][]
            {
                new[] { width, 0, 0, 0 },
                new[] { width, height, width, 0 },
                new[] { width, height, 0, height },
                new[] { 0, height, 0, 0 }
            };

            // 1. Extract wall heatmaps (channels 0-12)
            float[,,] wallHeatmaps = SliceChannels(heatmaps, 0, 13);
            int[] wallClasses = { 2, 8 };

            var (walls, wallTypes, wallPoints, wallLines, wallOrientMap) =
                WallDetector.GetWallPolygons(wallHeatmaps, rooms, _threshold, wallClasses,
                    PointOrientations, orientationRanges);

            // 2. Extract icon polygons
            var (iconPolygons, iconTypes) = IconDetector.GetIconPolygons(
                heatmaps, icons, _threshold, PointOrientations, orientationRanges);

            // 3. Extract opening polygons (doors/windows along walls)
            var (openings, openingTypes) = OpeningDetector.GetOpeningPolygons(
                heatmaps, walls, icons, wallPoints, wallLines, wallOrientMap,
                _threshold, PointOrientations, orientationRanges, _allOpeningTypes);

            // 4. Concatenate all polygons
            var allPolygons = new List<int[,]>();
            var allTypes = new List<Dictionary<string, object>>();

            allPolygons.AddRange(walls);
            allTypes.AddRange(wallTypes);
            allPolygons.AddRange(iconPolygons);
            allTypes.AddRange(iconTypes);
            allPolygons.AddRange(openings);
            allTypes.AddRange(openingTypes);

            // 5. Remove overlapping openings
            if (allPolygons.Count > 0)
            {
                var (filtered, filteredTypes) = OpeningDetector.RemoveOverlappingOpenings(
                    allPolygons, allTypes, new[] { 1 }, new[] { 2 });
                allPolygons = filtered;
                allTypes = filteredTypes;
            }

            // 6. Build result with normalized coordinates
            var result = new PolygonResult
            {
                Width = width,
                Height = height
            };

            for (int i = 0; i < allPolygons.Count; i++)
            {
                var poly = allPolygons[i];
                var type = allTypes[i];
                string typeStr = (string)type["type"];

                StructureCategory category;
                if (typeStr == "wall")
                {
                    category = StructureCategory.Wall;
                }
                else if (typeStr == "icon")
                {
                    int iconClass = (int)type["class"];
                    if (iconClass == 1) category = StructureCategory.Window;
                    else if (iconClass == 2) category = StructureCategory.Door;
                    else continue; // skip unknown icon classes
                }
                else continue;

                // Normalize to [0,1]
                Vector2[] verts = new Vector2[4];
                for (int v = 0; v < 4; v++)
                {
                    verts[v] = new Vector2(
                        Mathf.Round((float)poly[v, 0] / width * 1000000f) / 1000000f,
                        Mathf.Round((float)poly[v, 1] / height * 1000000f) / 1000000f
                    );
                }

                result.Polygons.Add(new PolygonEntry(verts, category));
            }

            return result;
        }

        /// <summary>Slice a range of channels from a [C,H,W] array.</summary>
        static float[,,] SliceChannels(float[,,] arr, int startChannel, int count)
        {
            int h = arr.GetLength(1);
            int w = arr.GetLength(2);
            float[,,] result = new float[count, h, w];
            for (int c = 0; c < count; c++)
                for (int r = 0; r < h; r++)
                    for (int col = 0; col < w; col++)
                        result[c, r, col] = arr[startChannel + c, r, col];
            return result;
        }
    }
}
