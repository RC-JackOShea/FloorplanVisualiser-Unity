using System;
using System.Collections;
using System.Collections.Generic;
using FloorplanVectoriser.Data;
using UnityEngine;

namespace FloorplanVectoriser.MeshGen
{
    /// <summary>
    /// Generates 3D meshes from a <see cref="SketchFile"/>.
    /// Reads spline control points, wall dimensions, and floor/ceiling ops
    /// directly from the sketch data — the single source of truth.
    /// </summary>
    public class FloorplanMeshBuilder
    {
        readonly Material _wallMaterial;
        readonly Material _doorMaterial;
        readonly Material _windowMaterial;

        public FloorplanMeshBuilder(Material wallMaterial, Material doorMaterial, Material windowMaterial)
        {
            _wallMaterial = wallMaterial;
            _doorMaterial = doorMaterial;
            _windowMaterial = windowMaterial;
        }

        /// <summary>
        /// Build meshes from a SketchFile, spreading creation across frames.
        /// </summary>
        public IEnumerator BuildFromSketchAsync(
            SketchFile sketch, int meshesPerFrame, Action<GameObject, Bounds> onComplete)
        {
            var root = new GameObject("Floorplan");
            var allBounds = new Bounds();
            bool boundsInitialized = false;
            int meshesThisFrame = 0;

            // Index spline entities by id for quick lookup
            var splineMap = new Dictionary<int, SplineComponent>();
            foreach (var entity in sketch.entities)
            {
                foreach (var comp in entity.components)
                {
                    if (comp is SplineComponent spline)
                    {
                        splineMap[entity.id] = spline;
                        break;
                    }
                }
            }

            // Process each entity
            for (int e = 0; e < sketch.entities.Count; e++)
            {
                var entity = sketch.entities[e];

                // Find what components this entity has
                SplineRefComponent splineRef = null;
                WallComponent wall = null;
                SplineTesselateOp tesselate = null;

                foreach (var comp in entity.components)
                {
                    if (comp is SplineRefComponent sr) splineRef = sr;
                    else if (comp is WallComponent w) wall = w;
                    else if (comp is SplineTesselateOp t) tesselate = t;
                }

                // Wall entity: extrude wall panels along spline segments
                if (splineRef != null && wall != null &&
                    splineMap.TryGetValue(splineRef.entityId, out var wallSpline))
                {
                    for (int s = 0; s < wallSpline.curveSections.Count; s++)
                    {
                        var section = wallSpline.curveSections[s];
                        var obj = CreateWallPanel(section, wall, s, root.transform);
                        EncapsulateBounds(obj, ref allBounds, ref boundsInitialized);

                        meshesThisFrame++;
                        if (meshesThisFrame >= meshesPerFrame)
                        {
                            meshesThisFrame = 0;
                            yield return null;
                        }
                    }
                }

                // Floor/Ceiling entity: skip for now (preview plane serves as floor)
                // if (splineRef != null && tesselate != null &&
                //     splineMap.TryGetValue(splineRef.entityId, out var floorSpline))
                // {
                //     var obj = CreateFloorMesh(
                //         floorSpline, tesselate, entity.name, root.transform);
                //     if (obj != null)
                //         EncapsulateBounds(obj, ref allBounds, ref boundsInitialized);
                //
                //     meshesThisFrame++;
                //     if (meshesThisFrame >= meshesPerFrame)
                //     {
                //         meshesThisFrame = 0;
                //         yield return null;
                //     }
                // }
            }

            onComplete?.Invoke(root, allBounds);
        }

        /// <summary>
        /// Create a single wall panel from a spline curve section.
        /// The section defines start (v0) and end (v3) in metres.
        /// The wall is offset by outset/inset perpendicular to the segment direction.
        /// </summary>
        GameObject CreateWallPanel(CurveSection section, WallComponent wall, int index, Transform parent)
        {
            var obj = new GameObject($"Wall_{index}");
            obj.transform.SetParent(parent);

            Vector3 start = new Vector3(section.v0.x, section.v0.y, section.v0.z);
            Vector3 end = new Vector3(section.v3.x, section.v3.y, section.v3.z);

            // Perpendicular direction in the XZ plane (rotate segment direction 90 degrees)
            Vector3 dir = (end - start).normalized;
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x);

            float outset = wall.outset;
            float inset = wall.inset;
            float height = wall.height;

            // 4 bottom vertices: two on each side of the centerline
            Vector3[] bottom = new Vector3[4];
            bottom[0] = start - perp * inset;       // start, inset side
            bottom[1] = start + perp * outset;      // start, outset side
            bottom[2] = end + perp * outset;         // end, outset side
            bottom[3] = end - perp * inset;           // end, inset side

            Vector3[] top = new Vector3[4];
            for (int i = 0; i < 4; i++)
                top[i] = bottom[i] + Vector3.up * height;

            var mesh = BuildBoxMesh(bottom, top);
            obj.AddComponent<MeshFilter>().mesh = mesh;
            obj.AddComponent<MeshRenderer>().material = _wallMaterial;

            return obj;
        }

        /// <summary>
        /// Create a flat floor or ceiling mesh by triangulating the spline polygon.
        /// Uses fan triangulation from the first vertex (works for convex polygons).
        /// </summary>
        GameObject CreateFloorMesh(
            SplineComponent spline, SplineTesselateOp tesselate, string name, Transform parent)
        {
            if (spline.controlPoints.Count < 3) return null;

            var obj = new GameObject(name);
            obj.transform.SetParent(parent);

            int n = spline.controlPoints.Count;
            var vertices = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var cp = spline.controlPoints[i].pos;
                vertices[i] = new Vector3(cp.x, cp.y, cp.z);
            }

            // Fan triangulation from vertex 0
            int triCount = (n - 2) * 3;
            var triangles = new int[triCount];
            for (int i = 0; i < n - 2; i++)
            {
                if (tesselate.flippedNormals)
                {
                    // CW winding for downward-facing normal (ceiling)
                    triangles[i * 3] = 0;
                    triangles[i * 3 + 1] = i + 2;
                    triangles[i * 3 + 2] = i + 1;
                }
                else
                {
                    // CCW winding for upward-facing normal (floor)
                    triangles[i * 3] = 0;
                    triangles[i * 3 + 1] = i + 1;
                    triangles[i * 3 + 2] = i + 2;
                }
            }

            Vector3 normal = tesselate.flippedNormals ? Vector3.down : Vector3.up;
            var normals = new Vector3[n];
            for (int i = 0; i < n; i++)
                normals[i] = normal;

            var mesh = new Mesh
            {
                vertices = vertices,
                normals = normals,
                triangles = triangles
            };
            mesh.RecalculateBounds();

            obj.AddComponent<MeshFilter>().mesh = mesh;
            obj.AddComponent<MeshRenderer>().material = _wallMaterial;

            return obj;
        }

        static void EncapsulateBounds(GameObject obj, ref Bounds allBounds, ref bool initialized)
        {
            var renderer = obj.GetComponent<MeshRenderer>();
            if (renderer == null) return;

            if (!initialized)
            {
                allBounds = renderer.bounds;
                initialized = true;
            }
            else
            {
                allBounds.Encapsulate(renderer.bounds);
            }
        }

        /// <summary>
        /// Build a box mesh from 4 bottom and 4 top vertices.
        /// Creates 5 faces (top + 4 sides, no bottom face).
        /// </summary>
        static Mesh BuildBoxMesh(Vector3[] bottom, Vector3[] top)
        {
            var vertices = new List<Vector3>(20);
            var normals = new List<Vector3>(20);
            var triangles = new List<int>(30);

            // Top face
            AddQuad(vertices, normals, triangles, top[0], top[1], top[2], top[3], Vector3.up);

            // 4 side faces
            AddQuad(vertices, normals, triangles, bottom[0], bottom[1], top[1], top[0],
                ComputeNormal(bottom[0], bottom[1], top[1]));
            AddQuad(vertices, normals, triangles, bottom[1], bottom[2], top[2], top[1],
                ComputeNormal(bottom[1], bottom[2], top[2]));
            AddQuad(vertices, normals, triangles, bottom[2], bottom[3], top[3], top[2],
                ComputeNormal(bottom[2], bottom[3], top[3]));
            AddQuad(vertices, normals, triangles, bottom[3], bottom[0], top[0], top[3],
                ComputeNormal(bottom[3], bottom[0], top[0]));

            var mesh = new Mesh
            {
                vertices = vertices.ToArray(),
                normals = normals.ToArray(),
                triangles = triangles.ToArray()
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddQuad(List<Vector3> verts, List<Vector3> norms, List<int> tris,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int start = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            norms.Add(normal); norms.Add(normal); norms.Add(normal); norms.Add(normal);
            tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
            tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
        }

        static Vector3 ComputeNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).normalized;
        }
    }
}
