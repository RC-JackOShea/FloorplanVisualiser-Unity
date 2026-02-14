using System;
using System.Collections.Generic;
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
        /// Result of the extraction: room outlines plus indices of segments
        /// that weren't included in any room face.
        /// </summary>
        public struct ExtractionResult
        {
            public List<RoomOutline> Rooms;
            public List<int> UncoveredSegmentIndices;
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
                UncoveredSegmentIndices = new List<int>()
            };

            if (segments.Count < 3)
            {
                // All segments are uncovered
                for (int i = 0; i < segments.Count; i++)
                    emptyResult.UncoveredSegmentIndices.Add(i);
                return emptyResult;
            }

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

            // 5. Prune dead ends (degree-1 vertices) — they can't form rooms
            PruneDeadEnds(adjacency, junctionCount);

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
                int safety = junctionCount * 4;

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
            var results = new List<RoomOutline>();
            float mostNegativeArea = 0f;
            int outerFaceIdx = -1;
            const float MinFaceArea = 0.01f;

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

                if (signedArea < mostNegativeArea)
                {
                    mostNegativeArea = signedArea;
                    outerFaceIdx = results.Count;
                }

                results.Add(outline);
            }

            // Mark and fix the outer face winding
            if (outerFaceIdx >= 0)
            {
                var outer = results[outerFaceIdx];
                outer.IsExterior = true;
                outer.Points.Reverse();
                results[outerFaceIdx] = outer;
            }

            return new ExtractionResult
            {
                Rooms = results,
                UncoveredSegmentIndices = uncoveredIndices
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

        static void PruneDeadEnds(HashSet<int>[] adjacency, int count)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int v = 0; v < count; v++)
                {
                    if (adjacency[v].Count == 1)
                    {
                        foreach (int neighbor in adjacency[v])
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
