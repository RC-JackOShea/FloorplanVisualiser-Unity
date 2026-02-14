using System.IO;
using System.IO.Compression;
using System.Text;
using FloorplanVectoriser.Data;

namespace FloorplanVectoriser.Conversion
{
    /// <summary>
    /// Serializes a <see cref="SketchFile"/> to the .sketch format:
    /// a ZIP archive containing data.json with $id reference tracking
    /// and the { typeName, typeId, data } component wrapper convention.
    /// </summary>
    public static class SketchSerializer
    {
        /// <summary>
        /// Serialize to the data.json string (for logging/debugging).
        /// </summary>
        public static string SerializeJson(SketchFile sketch)
        {
            var ctx = new IdContext();
            var sb = new StringBuilder(8192);
            WriteSketchFile(sb, sketch, ctx, 0);
            return sb.ToString();
        }

        /// <summary>
        /// Write the .sketch ZIP file to disk at the given path.
        /// Contains data.json and optionally a floorplan.jpg.
        /// </summary>
        public static void WriteSketchFile(string path, SketchFile sketch, byte[] floorplanJpg = null)
        {
            string json = SerializeJson(sketch);

            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var dataEntry = zip.CreateEntry("data.json", System.IO.Compression.CompressionLevel.Optimal);
                using (var writer = new StreamWriter(dataEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(json);
                }

                if (floorplanJpg != null && floorplanJpg.Length > 0)
                {
                    var imgEntry = zip.CreateEntry("floorplan.jpg", System.IO.Compression.CompressionLevel.Optimal);
                    using (var imgStream = imgEntry.Open())
                    {
                        imgStream.Write(floorplanJpg, 0, floorplanJpg.Length);
                    }
                }
            }
        }

        // ── Incrementing $id tracker ──

        class IdContext
        {
            int _next = 1;
            public string Next() => (_next++).ToString();
        }

        // ── Root ──

        static void WriteSketchFile(StringBuilder sb, SketchFile s, IdContext ctx, int indent)
        {
            sb.Append("{\n");
            WriteString(sb, "$id", ctx.Next(), indent + 1); sb.Append(",\n");
            WriteString(sb, "toolVersion", s.toolVersion, indent + 1); sb.Append(",\n");
            WriteString(sb, "appVersion", s.appVersion, indent + 1); sb.Append(",\n");
            WriteString(sb, "unityVersion", s.unityVersion, indent + 1); sb.Append(",\n");
            WriteString(sb, "displayName", s.displayName, indent + 1); sb.Append(",\n");
            WriteString(sb, "fileName", s.fileName, indent + 1); sb.Append(",\n");
            WriteString(sb, "description", s.description, indent + 1); sb.Append(",\n");
            WriteString(sb, "guid", s.guid, indent + 1); sb.Append(",\n");
            WriteString(sb, "styleGUID", s.styleGUID, indent + 1); sb.Append(",\n");
            WriteString(sb, "environmentGUID", s.environmentGUID, indent + 1); sb.Append(",\n");
            WriteFloat(sb, "gridSizeMetres", s.gridSizeMetres, indent + 1); sb.Append(",\n");
            WriteVec2(sb, "photoCaptureSize", s.photoCaptureSize, indent + 1); sb.Append(",\n");
            WriteFloat(sb, "floorplanOpacity", s.floorplanOpacity, indent + 1); sb.Append(",\n");
            WriteString(sb, "unitType", s.unitType, indent + 1); sb.Append(",\n");
            WriteString(sb, "flow", s.flow, indent + 1); sb.Append(",\n");
            WriteFloat(sb, "brightness", s.brightness, indent + 1); sb.Append(",\n");
            WriteBool(sb, "nightVisionEnabled", s.nightVisionEnabled, indent + 1); sb.Append(",\n");
            WriteBool(sb, "externalWallsGenerated", s.externalWallsGenerated, indent + 1); sb.Append(",\n");
            WriteVec2(sb, "physicalMapSize", s.physicalMapSize, indent + 1); sb.Append(",\n");
            WriteVec3(sb, "physicalMapPosition", s.physicalMapPosition, indent + 1); sb.Append(",\n");
            WriteInt(sb, "maxUsedEntityId", s.maxUsedEntityId, indent + 1); sb.Append(",\n");
            Indent(sb, indent + 1); sb.Append("\"unusedIds\": [],\n");

            // Entities
            Indent(sb, indent + 1); sb.Append("\"entities\": [\n");
            for (int e = 0; e < s.entities.Count; e++)
            {
                WriteEntity(sb, s.entities[e], ctx, indent + 2);
                if (e < s.entities.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            Indent(sb, indent + 1); sb.Append("]\n");
            Indent(sb, indent); sb.Append("}");
        }

        // ── Entity ──

        static void WriteEntity(StringBuilder sb, SketchEntity entity, IdContext ctx, int indent)
        {
            Indent(sb, indent); sb.Append("{\n");
            WriteString(sb, "$id", ctx.Next(), indent + 1); sb.Append(",\n");
            WriteInt(sb, "id", entity.id, indent + 1); sb.Append(",\n");
            WriteString(sb, "name", entity.name, indent + 1); sb.Append(",\n");
            Indent(sb, indent + 1); sb.Append("\"components\": [\n");

            for (int c = 0; c < entity.components.Count; c++)
            {
                WriteComponentWrapper(sb, entity.components[c], ctx, indent + 2);
                if (c < entity.components.Count - 1) sb.Append(",");
                sb.Append("\n");
            }

            Indent(sb, indent + 1); sb.Append("]\n");
            Indent(sb, indent); sb.Append("}");
        }

        // ── Component wrapper: { typeName, typeId, data: { $id, ...fields } } ──

        static void WriteComponentWrapper(StringBuilder sb, SketchComponent comp, IdContext ctx, int indent)
        {
            Indent(sb, indent); sb.Append("{\n");
            WriteString(sb, "typeName", comp.TypeName, indent + 1); sb.Append(",\n");
            WriteString(sb, "typeId", "0", indent + 1); sb.Append(",\n");
            Indent(sb, indent + 1); sb.Append("\"data\": ");
            WriteComponentData(sb, comp, ctx, indent + 1);
            sb.Append("\n");
            Indent(sb, indent); sb.Append("}");
        }

        static void WriteComponentData(StringBuilder sb, SketchComponent comp, IdContext ctx, int indent)
        {
            sb.Append("{\n");
            WriteString(sb, "$id", ctx.Next(), indent + 1);

            switch (comp)
            {
                case SplineComponent s:
                    sb.Append(",\n");
                    WriteString(sb, "splinePlane", s.splinePlane, indent + 1); sb.Append(",\n");
                    WriteBool(sb, "isClosed", s.isClosed, indent + 1); sb.Append(",\n");
                    WriteBool(sb, "isExternal", s.isExternal, indent + 1); sb.Append(",\n");
                    WriteInt(sb, "samplesPerMtr", s.samplesPerMtr, indent + 1); sb.Append(",\n");
                    WriteInt(sb, "lastCpIndex", s.lastCpIndex, indent + 1); sb.Append(",\n");

                    // controlPoints
                    Indent(sb, indent + 1); sb.Append("\"controlPoints\": [\n");
                    for (int i = 0; i < s.controlPoints.Count; i++)
                    {
                        WriteControlPoint(sb, s.controlPoints[i], indent + 2);
                        if (i < s.controlPoints.Count - 1) sb.Append(",");
                        sb.Append("\n");
                    }
                    Indent(sb, indent + 1); sb.Append("],\n");

                    // curveSections
                    Indent(sb, indent + 1); sb.Append("\"curveSections\": [\n");
                    for (int i = 0; i < s.curveSections.Count; i++)
                    {
                        WriteCurveSection(sb, s.curveSections[i], indent + 2);
                        if (i < s.curveSections.Count - 1) sb.Append(",");
                        sb.Append("\n");
                    }
                    Indent(sb, indent + 1); sb.Append("]\n");
                    break;

                case SplineRefComponent r:
                    sb.Append(",\n");
                    WriteInt(sb, "entityId", r.entityId, indent + 1); sb.Append("\n");
                    break;

                case WallComponent w:
                    sb.Append(",\n");
                    WriteFloat(sb, "outset", w.outset, indent + 1); sb.Append(",\n");
                    WriteFloat(sb, "inset", w.inset, indent + 1); sb.Append(",\n");
                    WriteFloat(sb, "height", w.height, indent + 1); sb.Append("\n");
                    break;

                case SplineOutlineOp o:
                    sb.Append(",\n");
                    WriteFloat(sb, "inset", o.inset, indent + 1); sb.Append(",\n");
                    WriteFloat(sb, "outset", o.outset, indent + 1); sb.Append("\n");
                    break;

                case SplineExtrudeOp ex:
                    sb.Append(",\n");
                    WriteVec3(sb, "dir", ex.dir, indent + 1); sb.Append(",\n");
                    WriteFloat(sb, "amount", ex.amount, indent + 1); sb.Append(",\n");
                    WriteBool(sb, "flippedNormals", ex.flippedNormals, indent + 1); sb.Append("\n");
                    break;

                case SplineTesselateOp t:
                    sb.Append(",\n");
                    WriteBool(sb, "flippedNormals", t.flippedNormals, indent + 1); sb.Append("\n");
                    break;

                case StructuralPropComponent sp:
                    sb.Append(",\n");
                    WriteString(sb, "guid", sp.guid, indent + 1); sb.Append(",\n");
                    WriteInt(sb, "entityId", sp.entityId, indent + 1); sb.Append(",\n");
                    WriteString(sb, "assetGUID", sp.assetGUID, indent + 1); sb.Append(",\n");
                    WriteInt(sb, "splineRef", sp.splineRef, indent + 1); sb.Append(",\n");
                    WriteInt(sb, "segmentRef", sp.segmentRef, indent + 1); sb.Append(",\n");
                    WriteVec3(sb, "position", sp.position, indent + 1); sb.Append(",\n");
                    WriteVec4(sb, "rotation", sp.rotation, indent + 1); sb.Append(",\n");
                    WriteBool(sb, "isFlipped", sp.isFlipped, indent + 1); sb.Append("\n");
                    break;

                default:
                    sb.Append("\n");
                    break;
            }

            Indent(sb, indent); sb.Append("}");
        }

        // ── Sub-structures ──

        static void WriteControlPoint(StringBuilder sb, ControlPoint cp, int indent)
        {
            Indent(sb, indent); sb.Append("{\n");
            WriteString(sb, "guid", cp.guid, indent + 1); sb.Append(",\n");
            WriteInt(sb, "splineRef", cp.splineRef, indent + 1); sb.Append(",\n");
            WriteVec3(sb, "pos", cp.pos, indent + 1); sb.Append(",\n");
            WriteVec3(sb, "inHandle", cp.inHandle, indent + 1); sb.Append(",\n");
            WriteVec3(sb, "outHandle", cp.outHandle, indent + 1); sb.Append(",\n");
            WriteString(sb, "handleMode", cp.handleMode, indent + 1); sb.Append("\n");
            Indent(sb, indent); sb.Append("}");
        }

        static void WriteCurveSection(StringBuilder sb, CurveSection cs, int indent)
        {
            Indent(sb, indent); sb.Append("{\n");
            WriteVec3(sb, "v0", cs.v0, indent + 1); sb.Append(",\n");
            WriteVec3(sb, "v1", cs.v1, indent + 1); sb.Append(",\n");
            WriteVec3(sb, "v2", cs.v2, indent + 1); sb.Append(",\n");
            WriteVec3(sb, "v3", cs.v3, indent + 1); sb.Append("\n");
            Indent(sb, indent); sb.Append("}");
        }

        // ── Primitives ──

        static void Indent(StringBuilder sb, int level)
        {
            for (int i = 0; i < level; i++) sb.Append("  ");
        }

        static void WriteString(StringBuilder sb, string key, string value, int indent)
        {
            Indent(sb, indent);
            sb.Append('"').Append(key).Append("\": \"").Append(EscapeJson(value ?? "")).Append('"');
        }

        static void WriteInt(StringBuilder sb, string key, int value, int indent)
        {
            Indent(sb, indent);
            sb.Append('"').Append(key).Append("\": ").Append(value);
        }

        static void WriteFloat(StringBuilder sb, string key, float value, int indent)
        {
            Indent(sb, indent);
            sb.Append('"').Append(key).Append("\": ").Append(FormatFloat(value));
        }

        static void WriteBool(StringBuilder sb, string key, bool value, int indent)
        {
            Indent(sb, indent);
            sb.Append('"').Append(key).Append("\": ").Append(value ? "true" : "false");
        }

        static void WriteVec2(StringBuilder sb, string key, Vec2 v, int indent)
        {
            Indent(sb, indent);
            sb.Append('"').Append(key).Append("\": { \"x\": ")
              .Append(FormatFloat(v.x)).Append(", \"y\": ")
              .Append(FormatFloat(v.y)).Append(" }");
        }

        static void WriteVec3(StringBuilder sb, string key, Vec3 v, int indent)
        {
            Indent(sb, indent);
            sb.Append('"').Append(key).Append("\": { \"x\": ")
              .Append(FormatFloat(v.x)).Append(", \"y\": ")
              .Append(FormatFloat(v.y)).Append(", \"z\": ")
              .Append(FormatFloat(v.z)).Append(" }");
        }

        static void WriteVec4(StringBuilder sb, string key, Vec4 v, int indent)
        {
            Indent(sb, indent);
            sb.Append('"').Append(key).Append("\": { \"x\": ")
              .Append(FormatFloat(v.x)).Append(", \"y\": ")
              .Append(FormatFloat(v.y)).Append(", \"z\": ")
              .Append(FormatFloat(v.z)).Append(", \"w\": ")
              .Append(FormatFloat(v.w)).Append(" }");
        }

        static string FormatFloat(float f)
        {
            if (f == (int)f)
                return f.ToString("0.0");
            return f.ToString("0.######");
        }

        static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
