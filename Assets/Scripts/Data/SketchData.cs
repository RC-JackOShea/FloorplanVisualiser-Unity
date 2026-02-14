using System;
using System.Collections.Generic;

namespace FloorplanVectoriser.Data
{
    // ── Lightweight vector types for serialization (no UnityEngine dependency) ──

    [Serializable]
    public struct Vec2
    {
        public float x, y;
        public Vec2(float x, float y) { this.x = x; this.y = y; }
    }

    [Serializable]
    public struct Vec3
    {
        public float x, y, z;
        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    [Serializable]
    public struct Vec4
    {
        public float x, y, z, w;
        public Vec4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vec4 Identity => new Vec4(0f, 0f, 0f, 1f);
    }

    // ── Sketch root ──

    [Serializable]
    public class SketchFile
    {
        public string toolVersion = "1.0.0";
        public string appVersion = "1.0.0";
        public string unityVersion = "2022.3.0f1";
        public string displayName = "Floor Plan";
        public string fileName;
        public string description = "";
        public string guid;
        public string styleGUID = "def3ded8-07bd-45cc-81f3-ac536a32d831";
        public string environmentGUID = "a0e6d0f3-347d-4749-b53a-7c9a8c0d9b5b";
        public float gridSizeMetres = 0.5f;
        public Vec2 photoCaptureSize = new Vec2(7f, 7f);
        public float floorplanOpacity = 1f;
        public string unitType = "Meters";
        public string flow = "Custom";
        public float brightness = 1f;
        public bool nightVisionEnabled;
        public bool externalWallsGenerated;
        public Vec2 physicalMapSize = new Vec2(0f, 0f);
        public Vec3 physicalMapPosition = new Vec3(0f, 0f, 0f);
        public int maxUsedEntityId;
        public List<int> unusedIds = new List<int>();
        public List<SketchEntity> entities = new List<SketchEntity>();
    }

    // ── Entity ──

    [Serializable]
    public class SketchEntity
    {
        public int id;
        public string name;
        public List<SketchComponent> components = new List<SketchComponent>();
    }

    // ── Components (polymorphic via typeName/typeId/data wrapper) ──

    [Serializable]
    public abstract class SketchComponent
    {
        public abstract string TypeName { get; }
    }

    [Serializable]
    public class SplineComponent : SketchComponent
    {
        public override string TypeName => "Spline";
        public string splinePlane = "y";
        public bool isClosed;
        public bool isExternal;
        public int samplesPerMtr = 1;
        public int lastCpIndex;
        public List<ControlPoint> controlPoints = new List<ControlPoint>();
        public List<CurveSection> curveSections = new List<CurveSection>();
    }

    [Serializable]
    public class SplineRefComponent : SketchComponent
    {
        public override string TypeName => "SplineRef";
        public int entityId;
    }

    [Serializable]
    public class WallComponent : SketchComponent
    {
        public override string TypeName => "Wall";
        public float outset = 0.05f;
        public float inset = 0.05f;
        public float height = 2.4f;
    }

    [Serializable]
    public class SplineOutlineOp : SketchComponent
    {
        public override string TypeName => "SplineOutlineOp";
        public float inset = 0.05f;
        public float outset = 0.05f;
    }

    [Serializable]
    public class SplineExtrudeOp : SketchComponent
    {
        public override string TypeName => "SplineExtrudeOp";
        public Vec3 dir = new Vec3(0f, 1f, 0f);
        public float amount = 2.4f;
        public bool flippedNormals;
    }

    [Serializable]
    public class SplineTesselateOp : SketchComponent
    {
        public override string TypeName => "SplineTesselateOp";
        public bool flippedNormals;
    }

    [Serializable]
    public class StructuralPropComponent : SketchComponent
    {
        public override string TypeName => "StructuralProp";
        public string guid;
        public int entityId;
        public string assetGUID = "";
        public int splineRef;
        public int segmentRef;
        public Vec3 position;
        public Vec4 rotation = Vec4.Identity;
        public bool isFlipped;
    }

    // ── Spline sub-types ──

    [Serializable]
    public struct ControlPoint
    {
        public string guid;
        public int splineRef;
        public Vec3 pos;
        public Vec3 inHandle;
        public Vec3 outHandle;
        public string handleMode;
    }

    [Serializable]
    public struct CurveSection
    {
        public Vec3 v0, v1, v2, v3;
    }
}
