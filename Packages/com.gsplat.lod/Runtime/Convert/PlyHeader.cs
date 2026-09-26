// Minimal PLY header parser used only to size the on-device memory guard
// (see SplatConverter.CheckEstimatedSize). Reads whatever prefix of the file
// is handed to it (a few dozen KB is enough for any real splat PLY header)
// and extracts just the fields the guard needs: how many vertices (splats)
// the file holds, and how many `f_rest_*` (SH) coefficients each vertex
// carries. Never throws -- an unparseable or non-PLY input just yields
// TryParse() == false, and callers fall back to a file-size-based estimate.
//
// Deliberately a plain line-by-line parser, no external dependency: PLY
// headers are ASCII, newline-terminated, and end at a line that is exactly
// "end_header" -- there is nothing here that benefits from a real grammar.

using System;
using System.Collections.Generic;
using System.Text;

namespace GsplatLod
{
    public readonly struct PlyHeader
    {
        /// <summary>Number of vertices (splats) declared by `element vertex N`.</summary>
        public readonly long VertexCount;

        /// <summary>Count of vertex properties named `f_rest_&lt;i&gt;` (SH coefficients beyond SH0).</summary>
        public readonly int ShCoefficientCount;

        /// <summary>True for `format binary_little_endian`/`binary_big_endian`; false for `format ascii`.</summary>
        public readonly bool IsBinary;

        PlyHeader(long vertexCount, int shCoefficientCount, bool isBinary)
        {
            VertexCount = vertexCount;
            ShCoefficientCount = shCoefficientCount;
            IsBinary = isBinary;
        }

        /// <summary>
        /// Parses a PLY header out of the leading bytes of a file. The input
        /// need not contain the whole file -- only up through the
        /// "end_header" line -- but if "end_header" never appears in the
        /// given bytes, parsing fails (the caller handed too short a
        /// prefix). Never throws.
        /// </summary>
        public static bool TryParse(byte[] headerBytes, out PlyHeader header)
        {
            header = default;
            if (headerBytes == null || headerBytes.Length == 0) return false;

            string text;
            try
            {
                // PLY headers are pure ASCII; decoding as Latin1 avoids any
                // risk of a stray high byte from the binary body (if the
                // prefix runs past end_header) throwing/mangling the parse.
                text = Encoding.GetEncoding("ISO-8859-1").GetString(headerBytes);
            }
            catch (Exception)
            {
                return false;
            }

            var lines = text.Split('\n');
            if (lines.Length == 0) return false;

            int i = 0;
            // First non-empty line must be "ply".
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Length || lines[i].Trim() != "ply") return false;
            i++;

            bool? isBinary = null;
            long vertexCount = -1;
            int shCount = 0;
            bool inVertexElement = false;
            bool sawEndHeader = false;

            for (; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                if (line == "end_header")
                {
                    sawEndHeader = true;
                    break;
                }

                var tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                switch (tokens[0])
                {
                    case "format":
                        if (tokens.Length < 2) return false;
                        if (tokens[1] == "binary_little_endian" || tokens[1] == "binary_big_endian")
                            isBinary = true;
                        else if (tokens[1] == "ascii")
                            isBinary = false;
                        else
                            return false; // unknown format
                        break;

                    case "element":
                        if (tokens.Length < 3) return false;
                        inVertexElement = tokens[1] == "vertex";
                        if (inVertexElement)
                        {
                            if (!long.TryParse(tokens[2], out vertexCount)) return false;
                        }
                        break;

                    case "property":
                        if (!inVertexElement) break;
                        if (tokens.Length < 3) return false;
                        // "property list <count-type> <value-type> <name>" —
                        // not used by splat PLYs' vertex element; skip rather
                        // than mis-size it.
                        if (tokens[1] == "list") break;

                        string type = tokens[1];
                        string name = tokens[2];
                        if (ScalarPropertyByteSize(type) <= 0) return false; // unknown scalar type
                        if (name.StartsWith("f_rest_", StringComparison.Ordinal))
                            shCount++;
                        break;
                }
            }

            if (!sawEndHeader) return false;
            if (isBinary == null) return false;
            if (vertexCount < 0) return false;

            header = new PlyHeader(vertexCount, shCount, isBinary.Value);
            return true;
        }

        static int ScalarPropertyByteSize(string plyType)
        {
            switch (plyType)
            {
                case "char":
                case "uchar":
                case "int8":
                case "uint8":
                    return 1;
                case "short":
                case "ushort":
                case "int16":
                case "uint16":
                    return 2;
                case "int":
                case "uint":
                case "int32":
                case "uint32":
                case "float":
                case "float32":
                    return 4;
                case "double":
                case "float64":
                case "int64":
                case "uint64":
                    return 8;
                default:
                    return -1;
            }
        }
    }
}
