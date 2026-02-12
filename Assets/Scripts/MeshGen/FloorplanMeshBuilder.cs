using System;
using System.Collections;
using System.Collections.Generic;
using FloorplanVectoriser.Data;
using UnityEngine;

namespace FloorplanVectoriser.MeshGen
{
    /// <summary>
    /// Generates extruded 3D meshes from detected floorplan polygons.
    /// Each wall/door/window polygon is extruded upward to create a box mesh.
    /// </summary>
    public class FloorplanMeshBuilder
    {
        readonly Material _wallMaterial;
        readonly Material _doorMaterial;
        readonly Material _windowMaterial;
        readonly float _worldScale;
        readonly float _extrudeHeight;
        readonly float _aspectRatio;
        readonly float _worldWidth;
        readonly float _worldHeight;

        public FloorplanMeshBuilder(Material wallMaterial, Material doorMaterial, Material windowMaterial,
            float worldScale = 10f, float extrudeHeight = 2f, float aspectRatio = 1f)
        {
            _wallMaterial = wallMaterial;
            _doorMaterial = doorMaterial;
            _windowMaterial = windowMaterial;
            _worldScale = worldScale;
            _extrudeHeight = extrudeHeight;
            _aspectRatio = aspectRatio;
            
            // Calculate actual world dimensions based on aspect ratio
            // This must match the preview plane sizing in ImageCapture.UpdatePreviewPlaneAspectRatio
            if (aspectRatio >= 1f)
            {
                // Landscape: width is constrained by worldScale
                _worldWidth = worldScale;
                _worldHeight = worldScale / aspectRatio;
            }
            else
            {
                // Portrait: height is constrained by worldScale
                _worldHeight = worldScale;
                _worldWidth = worldScale * aspectRatio;
            }
        }

        /// <summary>
        /// Build all meshes from a PolygonResult and parent them under a root GameObject.
        /// Returns the root GameObject and the combined bounds of all meshes.
        /// </summary>
        public (GameObject root, Bounds bounds) BuildFromResult(PolygonResult result)
        {
            var root = new GameObject("Floorplan");
            var allBounds = new Bounds();
            bool boundsInitialized = false;

            for (int i = 0; i < result.Polygons.Count; i++)
            {
                var entry = result.Polygons[i];
                var obj = CreateExtrudedBox(entry, i, root.transform);

                var renderer = obj.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    if (!boundsInitialized)
                    {
                        allBounds = renderer.bounds;
                        boundsInitialized = true;
                    }
                    else
                    {
                        allBounds.Encapsulate(renderer.bounds);
                    }
                }
            }

            return (root, allBounds);
        }

        /// <summary>
        /// Async version of BuildFromResult that spreads mesh creation across multiple frames
        /// to avoid GPU timeout on mobile devices.
        /// </summary>
        /// <param name="result">The polygon detection result.</param>
        /// <param name="meshesPerFrame">Number of meshes to create per frame before yielding.</param>
        /// <param name="onComplete">Callback with the root GameObject and combined bounds.</param>
        public IEnumerator BuildFromResultAsync(PolygonResult result, int meshesPerFrame, Action<GameObject, Bounds> onComplete)
        {
            var root = new GameObject("Floorplan");
            var allBounds = new Bounds();
            bool boundsInitialized = false;

            int meshesThisFrame = 0;

            for (int i = 0; i < result.Polygons.Count; i++)
            {
                var entry = result.Polygons[i];
                var obj = CreateExtrudedBox(entry, i, root.transform);

                var renderer = obj.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    if (!boundsInitialized)
                    {
                        allBounds = renderer.bounds;
                        boundsInitialized = true;
                    }
                    else
                    {
                        allBounds.Encapsulate(renderer.bounds);
                    }
                }

                meshesThisFrame++;
                if (meshesThisFrame >= meshesPerFrame)
                {
                    meshesThisFrame = 0;
                    yield return null; // Wait for next frame
                }
            }

            onComplete?.Invoke(root, allBounds);
        }

        GameObject CreateExtrudedBox(PolygonEntry entry, int index, Transform parent)
        {
            string prefix = entry.Category.ToString();
            var obj = new GameObject($"{prefix}_{index}");
            obj.transform.SetParent(parent);

            bool isDoorOrWindow = entry.Category == StructureCategory.Door || 
                                  entry.Category == StructureCategory.Window;
            bool isWindow = entry.Category == StructureCategory.Window;

            // Map normalized [0,1] coords to world XZ plane with Y-flip
            // Use _worldWidth for X and _worldHeight for Z to match the preview plane aspect ratio
            Vector3[] bottom = new Vector3[4];
            Vector3[] top = new Vector3[4];
            
            // Calculate the offset to center the mesh (matching preview plane centering)
            float offsetX = (_worldScale - _worldWidth) / 2f;
            float offsetZ = (_worldScale - _worldHeight) / 2f;
            
            // Calculate center for scaling doors/windows
            float centerX = 0f, centerZ = 0f;
            if (isDoorOrWindow)
            {
                for (int i = 0; i < 4; i++)
                {
                    centerX += entry.Vertices[i].x * _worldWidth + offsetX;
                    centerZ += (1f - entry.Vertices[i].y) * _worldHeight + offsetZ;
                }
                centerX /= 4f;
                centerZ /= 4f;
            }

            // Determine Y positions based on category
            float bottomY = isWindow ? 1f : 0f;                           // Windows start 1m up
            float topY = isWindow ? 1.75f : _extrudeHeight;               // Windows are 1m tall

            for (int i = 0; i < 4; i++)
            {
                float wx = entry.Vertices[i].x * _worldWidth + offsetX;
                float wz = (1f - entry.Vertices[i].y) * _worldHeight + offsetZ; // Y-flip

                // Scale doors/windows by 1.1x from center to prevent z-fighting
                if (isDoorOrWindow)
                {
                    wx = centerX + (wx - centerX) * 1.1f;
                    wz = centerZ + (wz - centerZ) * 1.1f;
                }

                bottom[i] = new Vector3(wx, bottomY, wz);
                top[i] = new Vector3(wx, topY, wz);
            }

            var mesh = BuildBoxMesh(bottom, top);
            obj.AddComponent<MeshFilter>().mesh = mesh;

            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.material = entry.Category switch
            {
                StructureCategory.Wall => _wallMaterial,
                StructureCategory.Door => _doorMaterial,
                StructureCategory.Window => _windowMaterial,
                _ => _wallMaterial
            };

            return obj;
        }

        /// <summary>
        /// Build a box mesh from 4 bottom and 4 top vertices.
        /// Creates 5 faces (top + 4 sides, no bottom face).
        /// Vertex order: 0=TL, 1=TR, 2=BR, 3=BL.
        /// </summary>
        static Mesh BuildBoxMesh(Vector3[] bottom, Vector3[] top)
        {
            // 8 vertices: bottom[0-3], top[0-3]
            // But for flat shading with correct normals, we duplicate verts per face.
            // 5 faces × 4 verts = 20 vertices, 5 faces × 2 tris × 3 = 30 indices.

            var vertices = new List<Vector3>(20);
            var normals = new List<Vector3>(20);
            var triangles = new List<int>(30);

            // Top face (Y-up normal)
            AddQuad(vertices, normals, triangles, top[0], top[1], top[2], top[3], Vector3.up);

            // Front side: bottom[0]-bottom[1] edge → top
            AddQuad(vertices, normals, triangles, bottom[0], bottom[1], top[1], top[0],
                ComputeNormal(bottom[0], bottom[1], top[1]));

            // Right side: bottom[1]-bottom[2] edge → top
            AddQuad(vertices, normals, triangles, bottom[1], bottom[2], top[2], top[1],
                ComputeNormal(bottom[1], bottom[2], top[2]));

            // Back side: bottom[2]-bottom[3] edge → top
            AddQuad(vertices, normals, triangles, bottom[2], bottom[3], top[3], top[2],
                ComputeNormal(bottom[2], bottom[3], top[3]));

            // Left side: bottom[3]-bottom[0] edge → top
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
