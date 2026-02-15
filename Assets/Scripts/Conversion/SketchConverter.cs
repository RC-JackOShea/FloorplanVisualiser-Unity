using System;
using System.Collections.Generic;
using FloorplanVectoriser.Data;
using UnityEngine;

namespace FloorplanVectoriser.Conversion
{
    /// <summary>
    /// Converts a <see cref="PolygonResult"/> into a <see cref="SketchFile"/>
    /// using spline-based room outlines with wall/floor/ceiling/door/window entities.
    /// </summary>
    public static class SketchConverter
    {
        const float DefaultWallHeight = 2.4f;
        const float ConnectionThreshold = 0.3f;

        /// <summary>
        /// Convert detected polygons into the sketch format.
        /// </summary>
        /// <param name="result">Post-processing result with normalized polygons.</param>
        /// <param name="captureSize">Photo capture size in metres (e.g., 7x7).</param>
        /// <param name="displayName">Display name for the sketch file.</param>
        public static SketchFile Convert(PolygonResult result, Vector2 captureSize, string displayName = "Floor Plan")
        {
            // Separate polygons by category
            var walls = new List<PolygonEntry>();
            var doors = new List<PolygonEntry>();
            var windows = new List<PolygonEntry>();

            foreach (var poly in result.Polygons)
            {
                switch (poly.Category)
                {
                    case StructureCategory.Wall:   walls.Add(poly);   break;
                    case StructureCategory.Door:    doors.Add(poly);   break;
                    case StructureCategory.Window:  windows.Add(poly); break;
                }
            }

            // Build wall chains using graph-based room extraction + uncovered segment recovery
            var chainResults = WallChainBuilder.BuildChainsWithMetadata(walls, captureSize, ConnectionThreshold);

            string sketchGuid = Guid.NewGuid().ToString();
            var sketch = new SketchFile
            {
                displayName = displayName,
                guid = sketchGuid,
                fileName = $"{displayName}_{sketchGuid}.sketch",
                photoCaptureSize = new Vec2(captureSize.x, captureSize.y)
            };

            int nextEntityId = 1;

            // Track all spline entities and their chains for door/window placement
            var splineEntities = new List<(int entityId, List<Vector3> chain, bool isClosed)>();

            // Create spline + wall entities for each chain
            // TODO: Currently restricted to exterior-only output for iterating on outer edge.
            for (int c = 0; c < chainResults.Count; c++)
            {
                var chainResult = chainResults[c];
                if (!chainResult.IsExterior) continue; // outer edge only for now
                var chain = chainResult.Points;
                float thickness = chainResult.Thickness;
                float halfThickness = Mathf.Max(thickness * 0.5f, 0.05f);
                bool isClosed = chainResult.IsClosed;

                // ── Spline entity ──
                int splineId = nextEntityId++;
                string splineName;
                if (chainResult.IsExterior) splineName = "External Spline";
                else if (isClosed) splineName = "Room Spline";
                else splineName = "Wall Spline";

                var splineEntity = new SketchEntity
                {
                    id = splineId,
                    name = splineName
                };

                var spline = CreateSplineComponent(chain, splineId, chainResult.IsExterior, isClosed);
                splineEntity.components.Add(spline);
                sketch.entities.Add(splineEntity);
                splineEntities.Add((splineId, chain, isClosed));

                // ── Wall entity ──
                int wallId = nextEntityId++;
                sketch.entities.Add(new SketchEntity
                {
                    id = wallId,
                    name = "Wall",
                    components = new List<SketchComponent>
                    {
                        new SplineRefComponent { entityId = splineId },
                        new WallComponent
                        {
                            outset = halfThickness,
                            inset = halfThickness,
                            height = DefaultWallHeight
                        },
                        new SplineOutlineOp
                        {
                            inset = halfThickness,
                            outset = halfThickness
                        },
                        new SplineExtrudeOp
                        {
                            dir = new Vec3(0f, 1f, 0f),
                            amount = DefaultWallHeight
                        }
                    }
                });

                // Floor + Ceiling for every wall spline (required by primary app loader)
                int floorId = nextEntityId++;
                sketch.entities.Add(new SketchEntity
                {
                    id = floorId,
                    name = "Floor",
                    components = new List<SketchComponent>
                    {
                        new SplineRefComponent { entityId = splineId },
                        new SplineTesselateOp { flippedNormals = false }
                    }
                });

                int ceilingId = nextEntityId++;
                sketch.entities.Add(new SketchEntity
                {
                    id = ceilingId,
                    name = "Ceiling",
                    components = new List<SketchComponent>
                    {
                        new SplineRefComponent { entityId = splineId },
                        new SplineTesselateOp { flippedNormals = true }
                    }
                });
            }

            // TODO: Doors/windows disabled while iterating on outer edge
            // PlaceStructuralProps(doors, StructureCategory.Door, captureSize, splineEntities, sketch, ref nextEntityId);
            // PlaceStructuralProps(windows, StructureCategory.Window, captureSize, splineEntities, sketch, ref nextEntityId);

            sketch.maxUsedEntityId = nextEntityId - 1;
            return sketch;
        }

        static SplineComponent CreateSplineComponent(
            List<Vector3> chain, int splineId, bool isExternal, bool isClosed)
        {
            var spline = new SplineComponent
            {
                isClosed = isClosed,
                isExternal = isExternal,
                lastCpIndex = chain.Count - 1
            };

            // Create control points — for autoCorner, handles equal the position
            for (int i = 0; i < chain.Count; i++)
            {
                Vector3 p = chain[i];

                spline.controlPoints.Add(new ControlPoint
                {
                    guid = Guid.NewGuid().ToString(),
                    splineRef = splineId,
                    pos = ToVec3(p),
                    inHandle = ToVec3(p),
                    outHandle = ToVec3(p),
                    handleMode = "autoCorner"
                });
            }

            // Create curve sections — straight lines: v1=v0, v2=v3
            // Closed: N sections for N points (last wraps to first)
            // Open:   N-1 sections for N points (no wrapping)
            int sectionCount = isClosed ? chain.Count : chain.Count - 1;
            for (int i = 0; i < sectionCount; i++)
            {
                int j = (i + 1) % chain.Count;
                Vec3 a = ToVec3(chain[i]);
                Vec3 b = ToVec3(chain[j]);

                spline.curveSections.Add(new CurveSection
                {
                    v0 = a,
                    v1 = a,
                    v2 = b,
                    v3 = b
                });
            }

            return spline;
        }

        static void PlaceStructuralProps(
            List<PolygonEntry> props, StructureCategory category,
            Vector2 captureSize, List<(int entityId, List<Vector3> chain, bool isClosed)> splineEntities,
            SketchFile sketch, ref int nextEntityId)
        {
            string label = category == StructureCategory.Door ? "Door" : "Window";

            foreach (var prop in props)
            {
                // Compute center position in metres
                Vector3 center = Vector3.zero;
                for (int i = 0; i < 4; i++)
                {
                    center += new Vector3(
                        prop.Vertices[i].x * captureSize.x,
                        0f,
                        (1f - prop.Vertices[i].y) * captureSize.y
                    );
                }
                center /= 4f;

                // Find nearest wall spline segment
                int bestSplineId = -1;
                int bestSegmentIdx = 0;
                float bestDist = float.MaxValue;
                Vector3 bestDir = Vector3.forward;

                foreach (var (entityId, chain, isClosed) in splineEntities)
                {
                    // For closed splines: check all N segments (wrapping)
                    // For open splines: check N-1 segments (no wrapping)
                    int segCount = isClosed ? chain.Count : chain.Count - 1;
                    for (int s = 0; s < segCount; s++)
                    {
                        int sNext = (s + 1) % chain.Count;
                        Vector3 closest = ClosestPointOnSegment(center, chain[s], chain[sNext]);
                        float dist = Vector3.Distance(center, closest);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestSplineId = entityId;
                            bestSegmentIdx = s;
                            bestDir = (chain[sNext] - chain[s]).normalized;
                        }
                    }
                }

                // Compute rotation: quaternion that faces along the wall direction
                Quaternion rot = bestDir.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(bestDir, Vector3.up)
                    : Quaternion.identity;

                int propEntityId = nextEntityId++;
                sketch.entities.Add(new SketchEntity
                {
                    id = propEntityId,
                    name = $"StructuralProp: {label}",
                    components = new List<SketchComponent>
                    {
                        new StructuralPropComponent
                        {
                            guid = Guid.NewGuid().ToString(),
                            entityId = propEntityId,
                            splineRef = bestSplineId,
                            segmentRef = bestSegmentIdx,
                            position = ToVec3(center),
                            rotation = new Vec4(rot.x, rot.y, rot.z, rot.w),
                            isFlipped = false
                        }
                    }
                });
            }
        }

        static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float sqrLen = ab.sqrMagnitude;
            if (sqrLen < 0.0001f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / sqrLen);
            return a + ab * t;
        }

        static Vec3 ToVec3(Vector3 v) => new Vec3(v.x, v.y, v.z);
    }
}
