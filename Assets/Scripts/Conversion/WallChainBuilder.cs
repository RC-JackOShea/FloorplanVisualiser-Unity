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
        /// Core logic: uses the planar graph extraction to get junctions and connections,
        /// then builds chains directly from connections — each connection emitted exactly once.
        /// The outer boundary becomes one connected closed spline; every internal connection
        /// becomes an individual open spline segment.
        /// </summary>
        static List<ChainResult> BuildChainsWithMetadata(
            List<WallSegment> segments, float connectionThreshold)
        {
            var extraction = RoomOutlineExtractor.Extract(segments, connectionThreshold);

            var results = new List<ChainResult>();

            // Build junction position lookup
            var junctionPos = new Dictionary<int, Vector3>();
            foreach (var j in extraction.Junctions)
                junctionPos[j.Id] = j.Position;

            // Build connection lookup by ID
            var connectionById = new Dictionary<int, RoomOutlineExtractor.Connection>();
            foreach (var conn in extraction.Connections)
                connectionById[conn.Id] = conn;

            // 1. Outer boundary: walk connections in order to build one connected closed spline
            if (extraction.OuterBoundaryConnectionIds.Count > 0)
            {
                var outerChain = BuildOrderedOuterBoundary(
                    extraction.OuterBoundaryConnectionIds, connectionById, junctionPos);

                if (outerChain.Points != null && outerChain.Points.Count >= 3)
                {
                    results.Add(outerChain);
                    Debug.Log($"[WallChainBuilder] Outer boundary: {outerChain.Points.Count} points " +
                              $"from {extraction.OuterBoundaryConnectionIds.Count} connections");
                }
            }

            // 2. Internal connections: each non-outer connection becomes an individual open chain
            int internalCount = 0;
            foreach (var conn in extraction.Connections)
            {
                if (extraction.OuterBoundaryConnectionIds.Contains(conn.Id)) continue;

                if (!junctionPos.TryGetValue(conn.JunctionA, out var posA)) continue;
                if (!junctionPos.TryGetValue(conn.JunctionB, out var posB)) continue;

                results.Add(new ChainResult
                {
                    Points = new List<Vector3> { posA, posB },
                    Thickness = conn.Thickness,
                    IsExterior = false,
                    IsClosed = false
                });
                internalCount++;
            }

            Debug.Log($"[WallChainBuilder] Connection-based chains: 1 outer boundary + " +
                      $"{internalCount} internal walls = {results.Count} total " +
                      $"(from {extraction.Connections.Count} connections)");

            return results;
        }

        /// <summary>
        /// Walk outer boundary connections in order to produce a single connected closed chain.
        /// Builds a local adjacency from just the outer connections, then walks junction-to-junction.
        /// </summary>
        static ChainResult BuildOrderedOuterBoundary(
            HashSet<int> outerConnIds,
            Dictionary<int, RoomOutlineExtractor.Connection> connectionById,
            Dictionary<int, Vector3> junctionPos)
        {
            // Build adjacency for outer boundary junctions only
            var outerAdj = new Dictionary<int, List<int>>();
            float totalThickness = 0f;
            int thicknessCount = 0;

            foreach (int connId in outerConnIds)
            {
                if (!connectionById.TryGetValue(connId, out var conn)) continue;

                if (!outerAdj.ContainsKey(conn.JunctionA))
                    outerAdj[conn.JunctionA] = new List<int>();
                if (!outerAdj.ContainsKey(conn.JunctionB))
                    outerAdj[conn.JunctionB] = new List<int>();

                outerAdj[conn.JunctionA].Add(conn.JunctionB);
                outerAdj[conn.JunctionB].Add(conn.JunctionA);

                totalThickness += conn.Thickness;
                thicknessCount++;
            }

            float avgThickness = thicknessCount > 0 ? totalThickness / thicknessCount : 0.1f;

            if (outerAdj.Count == 0)
            {
                return new ChainResult
                {
                    Points = new List<Vector3>(),
                    Thickness = avgThickness,
                    IsExterior = true,
                    IsClosed = true
                };
            }

            // Walk the boundary: pick a starting junction, follow unvisited neighbors
            var visited = new HashSet<int>();
            var chain = new List<Vector3>();

            // Start from any junction in the outer boundary
            int current = -1;
            foreach (int jId in outerAdj.Keys) { current = jId; break; }

            int safety = outerAdj.Count + 2;
            while (current >= 0 && safety-- > 0)
            {
                visited.Add(current);
                if (junctionPos.TryGetValue(current, out var pos))
                    chain.Add(pos);

                // Find unvisited neighbor
                int next = -1;
                if (outerAdj.TryGetValue(current, out var neighbors))
                {
                    foreach (int n in neighbors)
                    {
                        if (!visited.Contains(n))
                        {
                            next = n;
                            break;
                        }
                    }
                }
                current = next;
            }

            return new ChainResult
            {
                Points = chain,
                Thickness = avgThickness,
                IsExterior = true,
                IsClosed = true
            };
        }

        /// <summary>
        /// Extract the centerline of a wall polygon (4 vertices).
        /// The centerline runs along the long axis of the rectangle.
        /// </summary>
        public static WallSegment ExtractCenterline(PolygonEntry wall, Vector2 captureSize)
        {
            // Convert normalized [0,1] to metres, centered around origin.
            // X → X, Y (normalized) → Z (world), Y (world) = 0.
            float halfW = captureSize.x * 0.5f;
            float halfH = captureSize.y * 0.5f;
            Vector3[] pts = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                pts[i] = new Vector3(
                    wall.Vertices[i].x * captureSize.x - halfW,
                    0f,
                    (1f - wall.Vertices[i].y) * captureSize.y - halfH
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
