using System.Collections.Generic;
using UnityEngine;

namespace FloorplanVectoriser.Data
{
    public enum StructureCategory
    {
        Wall,
        Door,
        Window
    }

    public struct PolygonEntry
    {
        /// <summary>Exactly 4 vertices in normalized [0,1] coordinates.</summary>
        public Vector2[] Vertices;

        public StructureCategory Category;

        public PolygonEntry(Vector2[] vertices, StructureCategory category)
        {
            Vertices = vertices;
            Category = category;
        }
    }

    public class PolygonResult
    {
        public List<PolygonEntry> Polygons = new List<PolygonEntry>();

        public int Width;
        public int Height;
        public int OriginalWidth;
        public int OriginalHeight;
    }
}
