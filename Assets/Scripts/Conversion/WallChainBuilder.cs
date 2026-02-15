using System.Collections.Generic;
using FloorplanVectoriser.Data;
using UnityEngine;

namespace FloorplanVectoriser.Conversion
{
    /// <summary>
    /// Connects independent wall polygons into ordered chains of points
    /// that can be converted into spline control points.
    /// Uses graph-based room face extraction for clean room outlines,
    /// with a greedy fallback for disconnected components.
    /// </summary>
    public static class WallChainBuilder
    {
        /// <summary>
        /// Result of centerline extraction for a single wall polygon.
        /// </summary>
        public struct WallSegment
        {
            public Vector3 Start;
            public Vector3 End;
            public float Thickness;
        }

        /// <summary>
        /// Chain result with metadata about whether it represents an exterior boundary.
        /// </summary>
        public struct ChainResult
        {
            public List<Vector3> Points;
            public float Thickness;
            public bool IsExterior;
            public bool IsClosed;
        }

        /// <summary>
        /// Extract centerline segments from wall polygons and build room outlines
        /// using planar graph face extraction. Falls back to greedy chaining for
        /// any segments that don't form closed rooms.
        /// </summary>
        public static (List<List<Vector3>> chains, List<float> thicknesses) BuildChains(
            List<PolygonEntry> walls, Vector2 captureSize, float connectionThreshold = 0.3f)
        {
            if (walls.Count == 0)
                return (new List<List<Vector3>>(), new List<float>());

            // Extract centerline segments
            var segments = new List<WallSegment>(walls.Count);
            for (int i = 0; i < walls.Count; i++)
                segments.Add(ExtractCenterline(walls[i], captureSize));

            var result = BuildChainsWithMetadata(segments, connectionThreshold);

            var chains = new List<List<Vector3>>(result.Count);
            var thicknesses = new List<float>(result.Count);
            foreach (var r in result)
            {
                chains.Add(r.Points);
                thicknesses.Add(r.Thickness);
            }
            return (chains, thicknesses);
        }

        /// <summary>
        /// Full build returning metadata (including exterior flag) for each chain.
        /// </summary>
        public static List<ChainResult> BuildChainsWithMetadata(
            List<PolygonEntry> walls, Vector2 captureSize, float connectionThreshold = 0.3f)
        {
            if (walls.Count == 0)
                return new List<ChainResult>();

            var segments = new List<WallSegment>(walls.Count);
            for (int i = 0; i < walls.Count; i++)
                segments.Add(ExtractCenterline(walls[i], captureSize));

            return BuildChainsWithMetadata(segments, connectionThreshold);
        }

        /// <summary>
        /// Core logic: graph-based room extraction for closed rooms,
        /// then greedy chaining for any uncovered wall segments.
        /// Also recovers dead-end connections that were stripped from room outlines.
        /// </summary>
        static List<ChainResult> BuildChainsWithMetadata(
            List<WallSegment> segments, float connectionThreshold)
        {
            var extraction = RoomOutlineExtractor.Extract(segments, connectionThreshold);

            var results = new List<ChainResult>();

            // Add closed room outlines from graph extraction
            foreach (var room in extraction.Rooms)
            {
                results.Add(new ChainResult
                {
                    Points = room.Points,
                    Thickness = room.Thickness,
                    IsExterior = room.IsExterior,
                    IsClosed = true
                });
            }

            // Second pass: chain any uncovered segments (dead ends, partial walls, etc.)
            if (extraction.UncoveredSegmentIndices.Count > 0)
            {
                var uncoveredSegments = new List<WallSegment>(extraction.UncoveredSegmentIndices.Count);
                foreach (int idx in extraction.UncoveredSegmentIndices)
                    uncoveredSegments.Add(extraction.SplitSegments[idx]);

                var extraChains = GreedyBuildChains(uncoveredSegments, connectionThreshold);

                // Mark these as open chains (they didn't form closed rooms)
                foreach (var chain in extraChains)
                {
                    var openChain = chain;
                    openChain.IsClosed = false;
                    results.Add(openChain);
                }

                Debug.Log($"[WallChainBuilder] Graph extraction: {extraction.Rooms.Count} rooms + " +
                          $"{extraChains.Count} open wall chains from {extraction.UncoveredSegmentIndices.Count} " +
                          $"uncovered segments (total {segments.Count} wall segments)");
            }
            else
            {
                Debug.Log($"[WallChainBuilder] Graph extraction: {extraction.Rooms.Count} rooms, " +
                          $"all {segments.Count} wall segments covered");
            }

            // Third pass: recover dead-end connections that were stripped from room outlines.
            // These are real wall segments (shown as green in debug) that got removed by
            // stub stripping because they form dead-end excursions in the planar graph faces.
            // Build a set of all junction edges covered by room outline point sequences,
            // then emit any uncovered connections as open wall chains.
            var coveredEdges = new HashSet<long>();
            var junctionPositions = new Dictionary<int, Vector3>();
            foreach (var j in extraction.Junctions)
                junctionPositions[j.Id] = j.Position;

            foreach (var room in extraction.Rooms)
            {
                var pts = room.Points;
                for (int i = 0; i < pts.Count; i++)
                {
                    int next = (i + 1) % pts.Count;
                    // Find which junctions these points correspond to
                    int jA = FindClosestJunction(pts[i], extraction.Junctions);
                    int jB = FindClosestJunction(pts[next], extraction.Junctions);
                    if (jA >= 0 && jB >= 0 && jA != jB)
                    {
                        long ek = PackEdgeKey(jA, jB);
                        coveredEdges.Add(ek);
                    }
                }
            }

            int recoveredCount = 0;
            foreach (var conn in extraction.Connections)
            {
                long ek = PackEdgeKey(conn.JunctionA, conn.JunctionB);
                if (coveredEdges.Contains(ek)) continue;

                // This connection is not covered by any room outline — emit as open chain
                if (!junctionPositions.TryGetValue(conn.JunctionA, out var posA)) continue;
                if (!junctionPositions.TryGetValue(conn.JunctionB, out var posB)) continue;

                results.Add(new ChainResult
                {
                    Points = new List<Vector3> { posA, posB },
                    Thickness = conn.Thickness,
                    IsExterior = false,
                    IsClosed = false
                });
                recoveredCount++;
            }

            if (recoveredCount > 0)
            {
                Debug.Log($"[WallChainBuilder] Recovered {recoveredCount} dead-end wall segment(s) " +
                          $"stripped from room outlines");
            }

            return results;
        }

        static long PackEdgeKey(int a, int b)
        {
            int lo = System.Math.Min(a, b);
            int hi = System.Math.Max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        static int FindClosestJunction(Vector3 point, List<RoomOutlineExtractor.JunctionPoint> junctions)
        {
            int best = -1;
            float bestDist = 0.01f; // within 1cm
            for (int i = 0; i < junctions.Count; i++)
            {
                float dist = Vector3.Distance(point, junctions[i].Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = junctions[i].Id;
                }
            }
            return best;
        }

        /// <summary>
        /// Original greedy chain-building algorithm as fallback.
        /// </summary>
        static List<ChainResult> GreedyBuildChains(
            List<WallSegment> segments, float connectionThreshold)
        {
            int n = segments.Count;
            var used = new bool[n];
            var results = new List<ChainResult>();

            for (int seed = 0; seed < n; seed++)
            {
                if (used[seed]) continue;
                used[seed] = true;

                var chain = new List<Vector3> { segments[seed].Start, segments[seed].End };
                float avgThickness = segments[seed].Thickness;
                int thicknessCount = 1;

                // Extend forward
                bool extended = true;
                while (extended)
                {
                    extended = false;
                    Vector3 tip = chain[chain.Count - 1];
                    int bestIdx = -1;
                    float bestDist = connectionThreshold;
                    bool connectAtStart = true;

                    for (int j = 0; j < n; j++)
                    {
                        if (used[j]) continue;
                        float dStart = Vector3.Distance(tip, segments[j].Start);
                        float dEnd = Vector3.Distance(tip, segments[j].End);

                        if (dStart < bestDist) { bestDist = dStart; bestIdx = j; connectAtStart = true; }
                        if (dEnd < bestDist) { bestDist = dEnd; bestIdx = j; connectAtStart = false; }
                    }

                    if (bestIdx >= 0)
                    {
                        used[bestIdx] = true;
                        extended = true;
                        avgThickness += segments[bestIdx].Thickness;
                        thicknessCount++;
                        chain.Add(connectAtStart ? segments[bestIdx].End : segments[bestIdx].Start);
                    }
                }

                // Extend backward
                extended = true;
                while (extended)
                {
                    extended = false;
                    Vector3 tip = chain[0];
                    int bestIdx = -1;
                    float bestDist = connectionThreshold;
                    bool connectAtEnd = true;

                    for (int j = 0; j < n; j++)
                    {
                        if (used[j]) continue;
                        float dStart = Vector3.Distance(tip, segments[j].Start);
                        float dEnd = Vector3.Distance(tip, segments[j].End);

                        if (dEnd < bestDist) { bestDist = dEnd; bestIdx = j; connectAtEnd = true; }
                        if (dStart < bestDist) { bestDist = dStart; bestIdx = j; connectAtEnd = false; }
                    }

                    if (bestIdx >= 0)
                    {
                        used[bestIdx] = true;
                        extended = true;
                        avgThickness += segments[bestIdx].Thickness;
                        thicknessCount++;
                        chain.Insert(0, connectAtEnd ? segments[bestIdx].Start : segments[bestIdx].End);
                    }
                }

                results.Add(new ChainResult
                {
                    Points = chain,
                    Thickness = avgThickness / thicknessCount,
                    IsExterior = false
                });
            }

            return results;
        }

        /// <summary>
        /// Extract the centerline of a wall polygon (4 vertices).
        /// The centerline runs along the long axis of the rectangle.
        /// </summary>
        public static WallSegment ExtractCenterline(PolygonEntry wall, Vector2 captureSize)
        {
            // Convert normalized [0,1] to metres.
            // X → X, Y (normalized) → Z (world), Y (world) = 0.
            Vector3[] pts = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                pts[i] = new Vector3(
                    wall.Vertices[i].x * captureSize.x,
                    0f,
                    (1f - wall.Vertices[i].y) * captureSize.y
                );
            }

            // Find the two pairs of edges: (0-1, 2-3) and (1-2, 3-0).
            // The short edges are the wall's "ends"; the long edges are the wall's "sides".
            float edge01 = Vector3.Distance(pts[0], pts[1]);
            float edge12 = Vector3.Distance(pts[1], pts[2]);

            Vector3 midA, midB;
            float thickness;

            if (edge01 <= edge12)
            {
                midA = (pts[0] + pts[1]) * 0.5f;
                midB = (pts[2] + pts[3]) * 0.5f;
                thickness = edge01;
            }
            else
            {
                midA = (pts[1] + pts[2]) * 0.5f;
                midB = (pts[3] + pts[0]) * 0.5f;
                thickness = edge12;
            }

            // Manhattan snap: enforce perfectly axis-aligned centerlines.
            // The source wall polygons are Manhattan-aligned but floating-point
            // drift from normalization/scaling can introduce micro-angles.
            float dx = Mathf.Abs(midA.x - midB.x);
            float dz = Mathf.Abs(midA.z - midB.z);

            if (dx < dz)
            {
                // Vertical wall — snap both endpoints to same X
                float avgX = (midA.x + midB.x) * 0.5f;
                midA.x = avgX;
                midB.x = avgX;
            }
            else
            {
                // Horizontal wall — snap both endpoints to same Z
                float avgZ = (midA.z + midB.z) * 0.5f;
                midA.z = avgZ;
                midB.z = avgZ;
            }

            return new WallSegment
            {
                Start = midA,
                End = midB,
                Thickness = thickness
            };
        }
    }
}
