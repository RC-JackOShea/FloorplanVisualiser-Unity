using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace FloorplanVectoriser.Conversion
{
    /// <summary>
    /// Extracts room outlines from a planar graph of wall junction points.
    /// Uses half-edge face traversal to find minimal closed faces (rooms)
    /// from wall centerline segments, producing one spline per room.
    /// Also reports which segments weren't part of any room face.
    /// </summary>
    public static class RoomOutlineExtractor
    {
        /// <summary>
        /// A room outline extracted from the planar graph.
        /// </summary>
        public struct RoomOutline
        {
            /// <summary>Ordered vertices forming a closed room boundary (Y=0 plane).</summary>
            public List<Vector3> Points;
            /// <summary>True if this is the outer building boundary, false for interior rooms.</summary>
            public bool IsExterior;
            /// <summary>Average wall thickness (metres) for edges forming this room.</summary>
            public float Thickness;
        }

        /// <summary>
        /// A junction node in the planar graph, with a stable ID that matches J[n] labels.
        /// </summary>
        public struct JunctionPoint
        {
            /// <summary>Stable identifier matching the J[n] debug label.</summary>
            public int Id;
            /// <summary>World-space position (Y=0 plane).</summary>
            public Vector3 Position;
        }

        /// <summary>
        /// A connection (edge) between two junction points, representing a wall segment.
        /// </summary>
        public struct Connection
        {
            /// <summary>Stable identifier matching the C[n] debug label.</summary>
            public int Id;
            /// <summary>JunctionPoint.Id of one end.</summary>
            public int JunctionA;
            /// <summary>JunctionPoint.Id of the other end.</summary>
            public int JunctionB;
            /// <summary>Average wall thickness (metres) along this edge.</summary>
            public float Thickness;
        }

        /// <summary>
        /// A point where two wall segments cross each other mid-segment.
        /// Stored separately from endpoint-based junctions.
        /// </summary>
        public struct IntersectionPoint
        {
            /// <summary>World-space position of the crossing (Y=0 plane).</summary>
            public Vector3 Position;
            /// <summary>Index of the first crossing segment in the original list.</summary>
            public int SegmentA;
            /// <summary>Index of the second crossing segment in the original list.</summary>
            public int SegmentB;
            /// <summary>Parametric t along segment A at the crossing.</summary>
            public float tA;
            /// <summary>Parametric t along segment B at the crossing.</summary>
            public float tB;
        }

        /// <summary>
        /// Result of the extraction: room outlines plus indices of segments
        /// that weren't included in any room face, plus intersection points.
        /// </summary>
        public struct ExtractionResult
        {
            public List<RoomOutline> Rooms;
            public List<int> UncoveredSegmentIndices;
            /// <summary>Points where wall segments cross each other (separate from endpoint junctions).</summary>
            public List<IntersectionPoint> Intersections;
            /// <summary>The segment list used for graph building (after intersection splitting).
            /// UncoveredSegmentIndices refers to this list, not the original input.</summary>
            public List<WallChainBuilder.WallSegment> SplitSegments;
            /// <summary>All junction nodes in the planar graph, with stable IDs matching J[n] labels.</summary>
            public List<JunctionPoint> Junctions;
            /// <summary>All connections (edges) between junctions, with stable IDs matching C[n] labels.</summary>
            public List<Connection> Connections;
            /// <summary>Connection IDs that form the outer boundary (longest path around the outside).</summary>
            public HashSet<int> OuterBoundaryConnectionIds;
        }

        /// <summary>
        /// Build a planar graph from wall centerline segments and extract room faces.
        /// Also identifies segments not part of any room for second-pass processing.
        /// </summary>
        /// <param name="segments">Wall centerline segments with start/end positions.</param>
        /// <param name="connectionThreshold">Max distance (m) to merge endpoints into a junction.</param>
        /// <returns>Extraction result with room outlines and uncovered segment indices.</returns>
        public static ExtractionResult Extract(
            List<WallChainBuilder.WallSegment> segments, float connectionThreshold)
        {
            var emptyResult = new ExtractionResult
            {
                Rooms = new List<RoomOutline>(),
                UncoveredSegmentIndices = new List<int>(),
                Intersections = new List<IntersectionPoint>(),
                SplitSegments = segments,
                Junctions = new List<JunctionPoint>(),
                Connections = new List<Connection>(),
                OuterBoundaryConnectionIds = new HashSet<int>()
            };

            if (segments.Count < 3)
            {
                // All segments are uncovered
                for (int i = 0; i < segments.Count; i++)
                    emptyResult.UncoveredSegmentIndices.Add(i);
                return emptyResult;
            }

            // 0. Detect segment-segment intersections and split at crossing points
            var intersections = FindIntersections(segments, connectionThreshold);
            var splitSegments = SplitSegmentsAtIntersections(segments, intersections);
            segments = splitSegments;

            // 1. Collect all endpoints
            int epCount = segments.Count * 2;
            var endpoints = new Vector3[epCount];
            for (int i = 0; i < segments.Count; i++)
            {
                endpoints[2 * i] = segments[i].Start;
                endpoints[2 * i + 1] = segments[i].End;
            }

            // 2. Cluster nearby endpoints into junction nodes (union-find)
            int[] parent = new int[epCount];
            for (int i = 0; i < epCount; i++) parent[i] = i;

            float threshSq = connectionThreshold * connectionThreshold;
            for (int i = 0; i < epCount; i++)
            {
                for (int j = i + 1; j < epCount; j++)
                {
                    float dx = endpoints[i].x - endpoints[j].x;
                    float dz = endpoints[i].z - endpoints[j].z;
                    if (dx * dx + dz * dz < threshSq)
                        Union(parent, i, j);
                }
            }

            // 3. Build junction nodes from clusters
            var clusterMembers = new Dictionary<int, List<int>>();
            for (int i = 0; i < epCount; i++)
            {
                int root = Find(parent, i);
                if (!clusterMembers.ContainsKey(root))
                    clusterMembers[root] = new List<int>();
                clusterMembers[root].Add(i);
            }

            int junctionCount = clusterMembers.Count;
            var junctions = new Vector3[junctionCount];
            int[] epToJunction = new int[epCount];
            int jIdx = 0;
            foreach (var kvp in clusterMembers)
            {
                Vector3 centroid = Vector3.zero;
                foreach (int ep in kvp.Value)
                    centroid += endpoints[ep];
                centroid /= kvp.Value.Count;

                junctions[jIdx] = centroid;
                foreach (int ep in kvp.Value)
                    epToJunction[ep] = jIdx;
                jIdx++;
            }

            // 4. Build adjacency graph with thickness tracking
            //    Track which segment maps to which junction edge
            var adjacency = new HashSet<int>[junctionCount];
            for (int i = 0; i < junctionCount; i++)
                adjacency[i] = new HashSet<int>();

            var edgeThickness = new Dictionary<long, float>();
            // Map: segment index → junction edge key
            var segmentEdgeKeys = new long[segments.Count];

            for (int s = 0; s < segments.Count; s++)
            {
                int jA = epToJunction[2 * s];
                int jB = epToJunction[2 * s + 1];

                if (jA == jB)
                {
                    segmentEdgeKeys[s] = -1; // degenerate
                    continue;
                }

                adjacency[jA].Add(jB);
                adjacency[jB].Add(jA);

                long edgeKey = EdgeKey(jA, jB);
                segmentEdgeKeys[s] = edgeKey;

                if (!edgeThickness.ContainsKey(edgeKey))
                    edgeThickness[edgeKey] = segments[s].Thickness;
                else
                    edgeThickness[edgeKey] = (edgeThickness[edgeKey] + segments[s].Thickness) * 0.5f;
            }

            // 4b. Manhattan-snap junction positions along each edge.
            //     Since wall centerlines are axis-aligned, connected junctions
            //     should share either X or Z. Snap the smaller delta to the average.
            SnapJunctionsToManhattan(junctions, adjacency, junctionCount);

            // 4c. Capture pre-pruning state for debug output
            var prePruneNeighbors = new List<HashSet<int>>(junctionCount);
            for (int i = 0; i < junctionCount; i++)
                prePruneNeighbors.Add(new HashSet<int>(adjacency[i]));

            // 5. Compute prune log for debug, but do NOT apply pruning to the adjacency
            //    graph. The full (unpruned) graph is used for face traversal so the outer
            //    boundary includes dead-end stubs as back-and-forth excursions.
            var pruneLog = new List<string>();
            {
                // Clone adjacency to compute what pruning would remove (debug only)
                var pruneClone = new HashSet<int>[junctionCount];
                for (int i = 0; i < junctionCount; i++)
                    pruneClone[i] = new HashSet<int>(adjacency[i]);
                PruneDeadEnds(pruneClone, junctionCount, pruneLog);
            }

            // 6. Sort neighbors at each vertex by angle in the X-Z plane
            var sortedNeighbors = new List<int>[junctionCount];
            for (int v = 0; v < junctionCount; v++)
            {
                if (adjacency[v].Count == 0)
                {
                    sortedNeighbors[v] = new List<int>();
                    continue;
                }

                var neighbors = new List<int>(adjacency[v]);
                Vector3 center = junctions[v];
                neighbors.Sort((a, b) =>
                {
                    double angA = Math.Atan2(
                        junctions[a].z - center.z,
                        junctions[a].x - center.x);
                    double angB = Math.Atan2(
                        junctions[b].z - center.z,
                        junctions[b].x - center.x);
                    return angA.CompareTo(angB);
                });
                sortedNeighbors[v] = neighbors;
            }

            // 7. Build "next" map for directed half-edges
            var nextEdge = new Dictionary<long, long>();

            for (int v = 0; v < junctionCount; v++)
            {
                var neighbors = sortedNeighbors[v];
                for (int i = 0; i < neighbors.Count; i++)
                {
                    int u = neighbors[i];
                    int prevIdx = (i - 1 + neighbors.Count) % neighbors.Count;
                    int w = neighbors[prevIdx];

                    long fromEdge = DirectedEdgeKey(u, v);
                    long toEdge = DirectedEdgeKey(v, w);
                    nextEdge[fromEdge] = toEdge;
                }
            }

            // 8. Trace faces by following half-edge chains
            var visited = new HashSet<long>();
            var faces = new List<List<int>>();

            foreach (long edgeKey in nextEdge.Keys)
            {
                if (visited.Contains(edgeKey)) continue;

                var face = new List<int>();
                long current = edgeKey;
                int safety = junctionCount * 8;

                while (!visited.Contains(current) && safety-- > 0)
                {
                    visited.Add(current);
                    face.Add(SourceOfDirectedEdge(current));

                    if (!nextEdge.TryGetValue(current, out long next))
                        break;
                    current = next;
                }

                if (face.Count >= 3)
                    faces.Add(face);
            }

            // 9. Collect all undirected edges that appear in any face
            var edgesInFaces = new HashSet<long>();
            foreach (var face in faces)
            {
                for (int i = 0; i < face.Count; i++)
                {
                    int j = (i + 1) % face.Count;
                    edgesInFaces.Add(EdgeKey(face[i], face[j]));
                }
            }

            // 10. Identify uncovered segments (not part of any face)
            var uncoveredIndices = new List<int>();
            for (int s = 0; s < segments.Count; s++)
            {
                long ek = segmentEdgeKeys[s];
                if (ek < 0 || !edgesInFaces.Contains(ek))
                    uncoveredIndices.Add(s);
            }

            // 11. Classify faces by signed area
            const float MinFaceArea = 0.01f;
            var results = new List<RoomOutline>();
            int outerFaceIdx = -1;
            float mostNegativeArea = 0f;

            for (int f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                float signedArea = ComputeSignedArea(face, junctions);

                if (Mathf.Abs(signedArea) < MinFaceArea)
                    continue;

                // Compute average thickness for this face's edges
                float totalThickness = 0f;
                int thicknessCount = 0;
                for (int i = 0; i < face.Count; i++)
                {
                    int j = (i + 1) % face.Count;
                    long ek = EdgeKey(face[i], face[j]);
                    if (edgeThickness.TryGetValue(ek, out float t))
                    {
                        totalThickness += t;
                        thicknessCount++;
                    }
                }
                float avgThickness = thicknessCount > 0 ? totalThickness / thicknessCount : 0.1f;

                var outline = new RoomOutline
                {
                    Points = new List<Vector3>(),
                    IsExterior = false,
                    Thickness = avgThickness
                };

                foreach (int idx in face)
                    outline.Points.Add(junctions[idx]);

                // Strip dead-end stubs: where the path visits a junction, goes out
                // to a dead-end, and returns (points[i] == points[i+2]).
                // Repeat until no more stubs remain.
                bool stripped = true;
                while (stripped)
                {
                    stripped = false;
                    var pts = outline.Points;
                    for (int i = 0; i < pts.Count - 2; i++)
                    {
                        if (Vector3.Distance(pts[i], pts[i + 2]) < 0.001f)
                        {
                            // Remove the spike: point[i+1] (the dead-end) and point[i+2] (the duplicate)
                            pts.RemoveAt(i + 2);
                            pts.RemoveAt(i + 1);
                            stripped = true;
                            break; // restart scan
                        }
                    }
                    // Also check wrap-around for closed outlines
                    if (!stripped && pts.Count >= 3)
                    {
                        int n = pts.Count;
                        // Check last-first-second
                        if (Vector3.Distance(pts[n - 1], pts[1]) < 0.001f)
                        {
                            pts.RemoveAt(0);
                            pts.RemoveAt(pts.Count - 1);
                            stripped = true;
                        }
                        // Check second-to-last, last, first
                        else if (Vector3.Distance(pts[n - 2], pts[0]) < 0.001f)
                        {
                            pts.RemoveAt(n - 1);
                            pts.RemoveAt(n - 2);
                            stripped = true;
                        }
                    }
                }

                if (signedArea < mostNegativeArea)
                {
                    mostNegativeArea = signedArea;
                    outerFaceIdx = results.Count;
                }

                results.Add(outline);
            }

            // 12. Mark outer face (most negative signed area = unbounded exterior face)
            //     This only uses real graph edges — no artificial connections.
            if (outerFaceIdx >= 0)
            {
                var outer = results[outerFaceIdx];
                outer.IsExterior = true;
                results[outerFaceIdx] = outer;

                Debug.Log($"[RoomOutlineExtractor] Outer boundary: {outer.Points.Count} points " +
                          $"(face {outerFaceIdx}, area={mostNegativeArea:F3})");
            }

            // 13. Build exposed junction and connection lists (all edges, including dead-end stubs)
            var junctionList = new List<JunctionPoint>(junctionCount);
            for (int i = 0; i < junctionCount; i++)
            {
                junctionList.Add(new JunctionPoint { Id = i, Position = junctions[i] });
            }

            var connectionList = new List<Connection>();
            var edgeKeyToConnId = new Dictionary<long, int>();
            var visitedEdges = new HashSet<long>();
            int connId = 0;
            for (int v = 0; v < junctionCount; v++)
            {
                foreach (int u in prePruneNeighbors[v])
                {
                    long ek = EdgeKey(v, u);
                    if (visitedEdges.Contains(ek)) continue;
                    visitedEdges.Add(ek);

                    float thickness = edgeThickness.TryGetValue(ek, out float t) ? t : 0.1f;
                    edgeKeyToConnId[ek] = connId;
                    connectionList.Add(new Connection
                    {
                        Id = connId++,
                        JunctionA = v,
                        JunctionB = u,
                        Thickness = thickness
                    });
                }
            }

            // 14. Identify which connections form the outer boundary
            var outerConnIds = new HashSet<int>();
            if (outerFaceIdx >= 0)
            {
                // Find the face that was classified as outer
                List<int> outerFace = null;
                // outerFaceIdx is the index in 'results' — we need the corresponding face
                // Re-trace: results were added in face order (skipping tiny ones)
                int convergingIdx = 0;
                for (int f = 0; f < faces.Count; f++)
                {
                    float signedArea = ComputeSignedArea(faces[f], junctions);
                    if (Mathf.Abs(signedArea) < MinFaceArea) continue;
                    if (convergingIdx == outerFaceIdx)
                    {
                        outerFace = faces[f];
                        break;
                    }
                    convergingIdx++;
                }

                if (outerFace != null)
                {
                    for (int i = 0; i < outerFace.Count; i++)
                    {
                        int j = (i + 1) % outerFace.Count;
                        long ek = EdgeKey(outerFace[i], outerFace[j]);
                        if (edgeKeyToConnId.TryGetValue(ek, out int cid))
                            outerConnIds.Add(cid);
                    }
                    Debug.Log($"[RoomOutlineExtractor] Outer boundary uses {outerConnIds.Count} connections " +
                              $"(face has {outerFace.Count} vertices)");
                }
            }

            // Debug: dump all pipeline data to a file for analysis
            DumpDebugFile(segments, junctions, junctionCount, adjacency,
                faces, results, outerFaceIdx, uncoveredIndices, edgesInFaces,
                intersections, connectionList,
                segmentEdgeKeys, epToJunction, prePruneNeighbors, pruneLog);

            return new ExtractionResult
            {
                Rooms = results,
                UncoveredSegmentIndices = uncoveredIndices,
                Intersections = intersections,
                SplitSegments = segments,
                Junctions = junctionList,
                Connections = connectionList,
                OuterBoundaryConnectionIds = outerConnIds
            };
        }

        // ── Union-Find ──

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        // ── Dead-end pruning ──

        static void PruneDeadEnds(HashSet<int>[] adjacency, int count, List<string> log)
        {
            bool changed = true;
            int pass = 0;
            while (changed)
            {
                changed = false;
                pass++;
                for (int v = 0; v < count; v++)
                {
                    if (adjacency[v].Count == 1)
                    {
                        int neighbor = -1;
                        foreach (int n in adjacency[v]) neighbor = n;
                        log.Add($"Pass {pass}: pruned J[{v}] (was connected to J[{neighbor}])");
                        adjacency[neighbor].Remove(v);
                        adjacency[v].Clear();
                        changed = true;
                    }
                }
            }
        }

        // ── Manhattan snapping ──

        /// <summary>
        /// For each edge in the graph, snap the two junction endpoints so they
        /// share the same X (vertical wall) or same Z (horizontal wall).
        /// Uses iterative propagation so aligned chains converge to consistent values.
        /// </summary>
        static void SnapJunctionsToManhattan(
            Vector3[] junctions, HashSet<int>[] adjacency, int count)
        {
            // Multiple passes to propagate alignment through connected junctions
            for (int pass = 0; pass < 3; pass++)
            {
                for (int v = 0; v < count; v++)
                {
                    foreach (int u in adjacency[v])
                    {
                        if (u <= v) continue; // process each edge once

                        float dx = Mathf.Abs(junctions[v].x - junctions[u].x);
                        float dz = Mathf.Abs(junctions[v].z - junctions[u].z);

                        if (dx < dz)
                        {
                            // Vertical edge — snap X to average
                            float avgX = (junctions[v].x + junctions[u].x) * 0.5f;
                            junctions[v] = new Vector3(avgX, junctions[v].y, junctions[v].z);
                            junctions[u] = new Vector3(avgX, junctions[u].y, junctions[u].z);
                        }
                        else
                        {
                            // Horizontal edge — snap Z to average
                            float avgZ = (junctions[v].z + junctions[u].z) * 0.5f;
                            junctions[v] = new Vector3(junctions[v].x, junctions[v].y, avgZ);
                            junctions[u] = new Vector3(junctions[u].x, junctions[u].y, avgZ);
                        }
                    }
                }
            }
        }

        // ── Edge key helpers ──

        static long EdgeKey(int a, int b)
        {
            int lo = Math.Min(a, b);
            int hi = Math.Max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        static long DirectedEdgeKey(int from, int to)
        {
            return ((long)from << 32) | (uint)to;
        }

        static int SourceOfDirectedEdge(long key)
        {
            return (int)(key >> 32);
        }

        // ── Debug output ──

        static void DumpDebugFile(
            List<WallChainBuilder.WallSegment> segments,
            Vector3[] junctions, int junctionCount,
            HashSet<int>[] adjacency,
            List<List<int>> faces,
            List<RoomOutline> results,
            int outerFaceIdx,
            List<int> uncoveredIndices,
            HashSet<long> edgesInFaces,
            List<IntersectionPoint> intersections,
            List<Connection> connections,
            long[] segmentEdgeKeys,
            int[] epToJunction,
            List<HashSet<int>> prePruneNeighbors,
            List<string> pruneLog)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "debug_extraction.txt");
                var sb = new StringBuilder();

                sb.AppendLine("=== ROOM OUTLINE EXTRACTION DEBUG ===");
                sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                // 1. Raw input segments
                sb.AppendLine($"--- RAW WALL SEGMENTS ({segments.Count}) ---");
                for (int i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    sb.AppendLine($"  Seg[{i}]: ({s.Start.x:F3}, {s.Start.z:F3}) -> ({s.End.x:F3}, {s.End.z:F3})  thickness={s.Thickness:F3}");
                }
                sb.AppendLine();

                // 1b. Segment-to-junction mapping
                sb.AppendLine($"--- SEGMENT → JUNCTION MAPPING ({segments.Count}) ---");
                for (int i = 0; i < segments.Count; i++)
                {
                    int jA = epToJunction[2 * i];
                    int jB = epToJunction[2 * i + 1];
                    string edgeStr = segmentEdgeKeys[i] < 0 ? "DEGENERATE" : $"J[{jA}] <-> J[{jB}]";
                    sb.AppendLine($"  Seg[{i}] -> {edgeStr}");
                }
                sb.AppendLine();

                // 1c. Pre-pruning adjacency
                sb.AppendLine($"--- PRE-PRUNING ADJACENCY ({junctionCount}) ---");
                for (int i = 0; i < junctionCount; i++)
                {
                    var pre = prePruneNeighbors[i];
                    sb.AppendLine($"  J[{i}]: ({junctions[i].x:F3}, {junctions[i].z:F3})  degree={pre.Count}  neighbors=[{string.Join(", ", pre)}]");
                }
                sb.AppendLine();

                // 1d. Prune log
                sb.AppendLine($"--- DEAD-END PRUNING LOG ({pruneLog.Count} removals) ---");
                foreach (var entry in pruneLog)
                    sb.AppendLine($"  {entry}");
                sb.AppendLine();

                // 2. Junction nodes after clustering + Manhattan snap + pruning
                sb.AppendLine($"--- JUNCTIONS (post-prune) ({junctionCount}) ---");
                for (int i = 0; i < junctionCount; i++)
                {
                    var j = junctions[i];
                    var neighbors = adjacency[i];
                    sb.AppendLine($"  J[{i}]: ({j.x:F3}, {j.z:F3})  degree={neighbors.Count}  neighbors=[{string.Join(", ", neighbors)}]");
                }
                sb.AppendLine();

                // 3. All faces found by half-edge traversal
                sb.AppendLine($"--- FACES ({faces.Count}) ---");
                for (int f = 0; f < faces.Count; f++)
                {
                    var face = faces[f];
                    float area = ComputeSignedArea(face, junctions);
                    sb.Append($"  Face[{f}]: area={area:F3}  junctions=[{string.Join(", ", face)}]  points=[");
                    for (int i = 0; i < face.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append($"({junctions[face[i]].x:F3}, {junctions[face[i]].z:F3})");
                    }
                    sb.AppendLine("]");
                }
                sb.AppendLine();

                // 4. Room outlines (post-filtering)
                sb.AppendLine($"--- ROOM OUTLINES ({results.Count}) ---");
                for (int r = 0; r < results.Count; r++)
                {
                    var room = results[r];
                    string tag = r == outerFaceIdx ? " [OUTER]" : "";
                    sb.AppendLine($"  Room[{r}]{tag}: exterior={room.IsExterior}  thickness={room.Thickness:F3}  points={room.Points.Count}");
                    for (int i = 0; i < room.Points.Count; i++)
                    {
                        var p = room.Points[i];
                        sb.AppendLine($"    [{i}]: ({p.x:F3}, {p.z:F3})");
                    }
                }
                sb.AppendLine();

                // 5. Uncovered segments
                sb.AppendLine($"--- UNCOVERED SEGMENTS ({uncoveredIndices.Count}) ---");
                foreach (int idx in uncoveredIndices)
                {
                    var s = segments[idx];
                    sb.AppendLine($"  Seg[{idx}]: ({s.Start.x:F3}, {s.Start.z:F3}) -> ({s.End.x:F3}, {s.End.z:F3})");
                }
                sb.AppendLine();

                // 6. Intersection points
                sb.AppendLine($"--- INTERSECTION POINTS ({intersections.Count}) ---");
                for (int i = 0; i < intersections.Count; i++)
                {
                    var ix = intersections[i];
                    sb.AppendLine($"  IX[{i}]: ({ix.Position.x:F3}, {ix.Position.z:F3})  " +
                                  $"SegA={ix.SegmentA} (t={ix.tA:F3})  SegB={ix.SegmentB} (t={ix.tB:F3})");
                }
                sb.AppendLine();

                // 7. Connections (edges between junctions)
                sb.AppendLine($"--- CONNECTIONS ({connections.Count}) ---");
                for (int i = 0; i < connections.Count; i++)
                {
                    var c = connections[i];
                    sb.AppendLine($"  C[{c.Id}]: J[{c.JunctionA}] <-> J[{c.JunctionB}]  thickness={c.Thickness:F3}");
                }

                File.WriteAllText(path, sb.ToString());
                Debug.Log($"[RoomOutlineExtractor] Debug data written to: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoomOutlineExtractor] Failed to write debug file: {ex.Message}");
            }
        }

        // ── Intersection detection & segment splitting ──

        /// <summary>
        /// Find all points where two wall segments cross each other mid-segment.
        /// Uses the existing SegmentsIntersect2D helper. Results are stored in a
        /// dedicated list, separate from endpoint-based junctions.
        /// Intersections that fall within <paramref name="tolerance"/> of any
        /// existing segment endpoint are ignored (they'd just duplicate a spline point).
        /// </summary>
        public static List<IntersectionPoint> FindIntersections(
            List<WallChainBuilder.WallSegment> segments, float tolerance = 0.25f)
        {
            var intersections = new List<IntersectionPoint>();
            float tolSq = tolerance * tolerance;

            // Collect all segment endpoints for proximity checking
            var endpoints = new List<Vector3>(segments.Count * 2);
            for (int i = 0; i < segments.Count; i++)
            {
                endpoints.Add(segments[i].Start);
                endpoints.Add(segments[i].End);
            }

            for (int i = 0; i < segments.Count; i++)
            {
                for (int j = i + 1; j < segments.Count; j++)
                {
                    if (!SegmentsIntersect2D(
                            segments[i].Start, segments[i].End,
                            segments[j].Start, segments[j].End,
                            out float tA, out float tB))
                        continue;

                    Vector3 pos = new Vector3(
                        Mathf.Lerp(segments[i].Start.x, segments[i].End.x, tA),
                        0f,
                        Mathf.Lerp(segments[i].Start.z, segments[i].End.z, tA));

                    // Skip if too close to any existing endpoint (would duplicate a spline point)
                    bool tooClose = false;
                    for (int ep = 0; ep < endpoints.Count; ep++)
                    {
                        float dx = pos.x - endpoints[ep].x;
                        float dz = pos.z - endpoints[ep].z;
                        if (dx * dx + dz * dz < tolSq)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose) continue;

                    intersections.Add(new IntersectionPoint
                    {
                        Position = pos,
                        SegmentA = i,
                        SegmentB = j,
                        tA = tA,
                        tB = tB
                    });
                }
            }

            return intersections;
        }

        /// <summary>
        /// Split segments at their intersection points, producing a new expanded
        /// segment list. Each crossing splits the affected segment into two
        /// sub-segments at the intersection position, preserving thickness.
        /// The original segment list is not modified.
        /// </summary>
        static List<WallChainBuilder.WallSegment> SplitSegmentsAtIntersections(
            List<WallChainBuilder.WallSegment> segments,
            List<IntersectionPoint> intersections)
        {
            if (intersections.Count == 0)
                return new List<WallChainBuilder.WallSegment>(segments);

            // Collect all splits per segment: segIndex → list of (t, position)
            var splitsPerSegment = new Dictionary<int, List<(float t, Vector3 pos)>>();

            foreach (var ix in intersections)
            {
                if (!splitsPerSegment.ContainsKey(ix.SegmentA))
                    splitsPerSegment[ix.SegmentA] = new List<(float, Vector3)>();
                splitsPerSegment[ix.SegmentA].Add((ix.tA, ix.Position));

                if (!splitsPerSegment.ContainsKey(ix.SegmentB))
                    splitsPerSegment[ix.SegmentB] = new List<(float, Vector3)>();
                splitsPerSegment[ix.SegmentB].Add((ix.tB, ix.Position));
            }

            var result = new List<WallChainBuilder.WallSegment>();

            for (int s = 0; s < segments.Count; s++)
            {
                if (!splitsPerSegment.TryGetValue(s, out var splits))
                {
                    // No intersections on this segment — keep as-is
                    result.Add(segments[s]);
                    continue;
                }

                // Sort splits by parametric t so we walk along the segment in order
                splits.Sort((a, b) => a.t.CompareTo(b.t));

                Vector3 current = segments[s].Start;
                float thickness = segments[s].Thickness;

                foreach (var (t, pos) in splits)
                {
                    result.Add(new WallChainBuilder.WallSegment
                    {
                        Start = current,
                        End = pos,
                        Thickness = thickness
                    });
                    current = pos;
                }

                // Final sub-segment from last split to original end
                result.Add(new WallChainBuilder.WallSegment
                {
                    Start = current,
                    End = segments[s].End,
                    Thickness = thickness
                });
            }

            Debug.Log($"[RoomOutlineExtractor] Found {intersections.Count} intersection(s), " +
                      $"split {segments.Count} segments into {result.Count}");

            return result;
        }

        // ── Segment intersection helper ──

        /// <summary>
        /// Test if two line segments intersect in the XZ plane.
        /// Returns parametric t values for each segment at the intersection.
        /// </summary>
        static bool SegmentsIntersect2D(
            Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4,
            out float t1, out float t2)
        {
            float d1x = p2.x - p1.x, d1z = p2.z - p1.z;
            float d2x = p4.x - p3.x, d2z = p4.z - p3.z;
            float denom = d1x * d2z - d1z * d2x;

            t1 = t2 = 0f;
            if (Mathf.Abs(denom) < 1e-8f) return false; // parallel

            float ox = p3.x - p1.x, oz = p3.z - p1.z;
            t1 = (ox * d2z - oz * d2x) / denom;
            t2 = (ox * d1z - oz * d1x) / denom;

            const float eps = 0.001f;
            return t1 > eps && t1 < 1f - eps && t2 > eps && t2 < 1f - eps;
        }

        // ── Geometry ──

        static float ComputeSignedArea(List<int> face, Vector3[] junctions)
        {
            float area = 0f;
            for (int i = 0; i < face.Count; i++)
            {
                int j = (i + 1) % face.Count;
                Vector3 pi = junctions[face[i]];
                Vector3 pj = junctions[face[j]];
                area += pi.x * pj.z - pj.x * pi.z;
            }
            return area * 0.5f;
        }
    }
}
