// EditMode tests for PlyHeader (see Packages/com.gsplat.lod/Runtime/Convert/PlyHeader.cs)
// and the splat-count-based memory guard estimate it feeds
// (SplatConverter.EstimateConversionRamBytes).

using System.Text;
using NUnit.Framework;

namespace GsplatLod.Tests
{
    public class PlyHeaderTests
    {
        static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        [Test]
        public void ParsesBinarySh0Header()
        {
            string header =
                "ply\n" +
                "format binary_little_endian 1.0\n" +
                "element vertex 1000\n" +
                "property float x\n" +
                "property float y\n" +
                "property float z\n" +
                "property float f_dc_0\n" +
                "property float f_dc_1\n" +
                "property float f_dc_2\n" +
                "property float opacity\n" +
                "property float scale_0\n" +
                "property float scale_1\n" +
                "property float scale_2\n" +
                "property float rot_0\n" +
                "property float rot_1\n" +
                "property float rot_2\n" +
                "property float rot_3\n" +
                "end_header\n";

            Assert.IsTrue(PlyHeader.TryParse(Ascii(header), out var parsed));
            Assert.AreEqual(1000, parsed.VertexCount);
            Assert.AreEqual(0, parsed.ShCoefficientCount);
            Assert.IsTrue(parsed.IsBinary);
        }

        // 20 non-SH float32 properties (names don't matter to the parser) +
        // 24 f_rest_* (SH degree 2) = 44 properties = 176 B/splat, the
        // layout of a typical SH-degree-2 training output.
        static string BuildShDegree2Header(long vertexCount)
        {
            var sb = new StringBuilder();
            sb.Append("ply\nformat binary_little_endian 1.0\n");
            sb.Append($"element vertex {vertexCount}\n");
            for (int i = 0; i < 20; i++)
                sb.Append($"property float base_{i}\n");
            for (int i = 0; i < 24; i++)
                sb.Append($"property float f_rest_{i}\n");
            sb.Append("end_header\n");
            return sb.ToString();
        }

        [Test]
        public void ParsesBinaryShDegree2Header()
        {
            byte[] headerBytes = Ascii(BuildShDegree2Header(2206782));

            Assert.IsTrue(PlyHeader.TryParse(headerBytes, out var parsed));
            Assert.AreEqual(2206782, parsed.VertexCount);
            Assert.AreEqual(24, parsed.ShCoefficientCount);
            Assert.IsTrue(parsed.IsBinary);
        }

        [Test]
        public void RejectsNonPlyBytes()
        {
            Assert.IsFalse(PlyHeader.TryParse(Ascii("this is not a ply file at all\n"), out _));
        }

        [Test]
        public void RejectsEmptyOrNullInput()
        {
            Assert.IsFalse(PlyHeader.TryParse(null, out _));
            Assert.IsFalse(PlyHeader.TryParse(new byte[0], out _));
        }

        [Test]
        public void RejectsHeaderMissingEndHeader()
        {
            string header =
                "ply\n" +
                "format binary_little_endian 1.0\n" +
                "element vertex 10\n" +
                "property float x\n";
            Assert.IsFalse(PlyHeader.TryParse(Ascii(header), out _));
        }

        [Test]
        public void AsciiFormatParsesVertexCount()
        {
            string header =
                "ply\n" +
                "format ascii 1.0\n" +
                "element vertex 42\n" +
                "property float x\n" +
                "property float y\n" +
                "property float z\n" +
                "end_header\n";

            Assert.IsTrue(PlyHeader.TryParse(Ascii(header), out var parsed));
            Assert.AreEqual(42, parsed.VertexCount);
            Assert.IsFalse(parsed.IsBinary);
        }

        // --- a large SH-degree-2 file must not be rejected on Quest 3 ---

        [Test]
        public void Degree2LargeFileEstimateStaysUnderQuest3Budget()
        {
            // 370.4 MB, SH degree 2 (44 float32 properties/splat =
            // 176 B/splat, 24 f_rest_* coefficients), ~2.2M splats.
            const long fileSize = (long)(370.4 * 1024 * 1024);
            const int bytesPerVertex = 176;
            long vertexCount = fileSize / bytesPerVertex;

            byte[] headerBytes = Ascii(BuildShDegree2Header(vertexCount));

            // Quest 3 measured: MemTotal ~7.6 GB physical.
            // SplatConverter budgets 40% of physical RAM for conversion,
            // i.e. ~3.1 GB -- reproduced here directly rather than via
            // SystemInfo.systemMemorySize (not meaningful in the Editor).
            const long questPhysicalRamBytes = 7943148L * 1024; // /proc/meminfo MemTotal
            const long questBudgetBytes = (long)(questPhysicalRamBytes * 0.4);

            long tinyEstimate = SplatConverter.EstimateConversionRamBytes(fileSize, "scene_sh2.ply", headerBytes, quality: false);
            long bhattEstimate = SplatConverter.EstimateConversionRamBytes(fileSize, "scene_sh2.ply", headerBytes, quality: true);

            Assert.Less(tinyEstimate, questBudgetBytes,
                $"tiny-lod estimate {tinyEstimate / (1024f * 1024f):0.#} MB should be under the ~{questBudgetBytes / (1024f * 1024f):0.#} MB Quest 3 budget");
            Assert.Less(bhattEstimate, questBudgetBytes,
                $"bhatt-lod estimate {bhattEstimate / (1024f * 1024f):0.#} MB should be under the ~{questBudgetBytes / (1024f * 1024f):0.#} MB Quest 3 budget");

            // A file-size multiplier (fileSize*16 + 32MB ~= 5958 MB) would
            // reject this file; the splat-count estimate must stay well below.
            long fileSizeMultiplierEstimate = fileSize * 16 + 32L * 1024 * 1024;
            Assert.Less(tinyEstimate, fileSizeMultiplierEstimate);
            Assert.Less(bhattEstimate, fileSizeMultiplierEstimate);
        }

        [Test]
        public void NonPlyExtensionFallsBackToFileSizeEstimate()
        {
            const long fileSize = 100L * 1024 * 1024;
            long estimate = SplatConverter.EstimateConversionRamBytes(fileSize, "capture.spz", headerBytes: null, quality: false);
            Assert.Greater(estimate, fileSize); // some multiplier > 1x was applied
        }
    }
}
