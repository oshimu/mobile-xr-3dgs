// Managed wrapper around the native `gsplat_convert` plugin (Rust).
// The native library converts a .ply/.spz/.splat/.ksplat/.sog/.zip capture into
// the runtime .usst/.usc/.manifest.json triple this package streams at
// runtime. See:
//   Packages/com.gsplat.lod/Runtime/Plugins/x86_64/gsplat_convert.dll
//   Packages/com.gsplat.lod/Runtime/Plugins/Android/arm64-v8a/libgsplat_convert.so
//
// Marshaling rules (do not relax these):
//   - Strings crossing the FFI boundary go out as NUL-terminated UTF-8 byte[],
//     never as `string`. A `string` parameter lets the CLR marshaler own (and
//     free) the buffer; Rust owns its side, so mixing ownership either leaks
//     or double-frees.
//   - `gsplat_convert_result_json` returns memory owned by the native handle
//     (freed only by `gsplat_convert_free`). The P/Invoke signature MUST
//     declare it as IntPtr and decode with Marshal.PtrToStringUTF8 — a
//     `string` return type would again hand the marshaler a free() it must
//     not perform, corrupting the native heap.
//   - `gsplat_convert_poll`'s log buffer is a caller-owned byte[]; decode only
//     up to the NUL terminator the native side writes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace GsplatLod
{
    /// <summary>
    /// Mirrors the native `RawOptions` JSON schema exactly (camelCase, every
    /// field always present — the native side treats certain values as
    /// "use the default" sentinels rather than accepting missing fields).
    /// </summary>
    [Serializable]
    public class ConvertOptions
    {
        // input / inputFd are mutually exclusive: set exactly one (leave
        // input empty and inputFd >= 0 for an SAF-picked fd; leave inputFd
        // at -1 and input non-empty for a plain path). displayName is
        // required when inputFd is used -- the native side has no path to
        // sniff a format/name from an fd, so it uses displayName's extension
        // and stem instead (see SplatFilePicker.DisplayName).
        public string input = "";
        public int inputFd = -1;       // -1 -> not fd input; use `input` (path) instead
        public string displayName = ""; // required (non-empty) when inputFd >= 0
        public string outDir = "";     // empty -> input's directory (native default)
        public string name = "";       // empty -> input's file stem (native default)
        public bool quality;           // false = tiny-lod, true = bhatt-lod
        public float lodBase;          // <=0 -> method default
        public int maxSh;              // format accepts 0 only
        public string coord = "flip-x";
        public int regionSplats;       // <=0 -> encoder default
        public bool force;
    }

    static class NativeMethods
    {
        // Resolves to gsplat_convert.dll (x86_64) / libgsplat_convert.so
        // (Android arm64-v8a) — same DllImport name on both platforms.
        const string k_lib = "gsplat_convert";

        [DllImport(k_lib)]
        public static extern int gsplat_convert_abi_version();

        [DllImport(k_lib)]
        public static extern IntPtr gsplat_convert_start(byte[] optionsJsonUtf8);

        [DllImport(k_lib)]
        public static extern int gsplat_convert_poll(IntPtr h, byte[] buf, int bufLen);

        [DllImport(k_lib)]
        public static extern void gsplat_convert_cancel(IntPtr h);

        // Deliberately IntPtr, not string — see file header.
        [DllImport(k_lib)]
        public static extern IntPtr gsplat_convert_result_json(IntPtr h);

        [DllImport(k_lib)]
        public static extern void gsplat_convert_free(IntPtr h);
    }

    /// <summary>
    /// One conversion run. Start it, call Poll() every frame until IsDone,
    /// then Dispose() (see its doc comment for why that never blocks). Must
    /// be called even after a successful/failed run, not just on early
    /// abandonment.
    /// </summary>
    public sealed class ConvertJob : IDisposable
    {
        const int k_pollBufSize = 4096;   // ample for one buf_len of queued log lines
        const int k_maxLogLines = 200;    // AllLog keeps only the most recent N lines

        IntPtr m_handle = IntPtr.Zero;
        readonly byte[] m_pollBuf = new byte[k_pollBufSize];
        readonly List<string> m_log = new();

        public bool IsRunning { get; private set; }
        public bool IsDone { get; private set; }
        public bool Succeeded { get; private set; }
        public string LastLogLine { get; private set; } = "";
        public IReadOnlyList<string> AllLog => m_log;
        public string Error { get; private set; }
        public string OutputUsstPath { get; private set; }

        /// <summary>
        /// Starts the conversion on a native worker thread. Returns false if
        /// the plugin is unavailable or the native side rejected the options
        /// (e.g. bad coord preset) — Error explains why, and the job is
        /// already IsDone in that case (nothing to Poll/Cancel).
        /// </summary>
        public bool Start(ConvertOptions options)
        {
            if (m_handle != IntPtr.Zero)
                throw new InvalidOperationException("ConvertJob already started; Dispose before reusing");

            OutputUsstPath = Path.Combine(options.outDir, options.name + ".usst");

            if (!SplatConverter.IsAvailable)
            {
                Error = "gsplat_convert native plugin is not available on this platform/build";
                IsDone = true;
                return false;
            }

            string json = JsonUtility.ToJson(options);
            m_handle = NativeMethods.gsplat_convert_start(Encoding.UTF8.GetBytes(json + "\0"));
            if (m_handle == IntPtr.Zero)
            {
                Error = "gsplat_convert_start failed (invalid options or could not spawn worker thread)";
                IsDone = true;
                return false;
            }

            IsRunning = true;
            IsDone = false;
            Succeeded = false;
            Error = null;
            return true;
        }

        /// <summary>Call once per frame while IsRunning. No-op once IsDone.</summary>
        public void Poll()
        {
            if (m_handle == IntPtr.Zero || IsDone) return;

            int status = NativeMethods.gsplat_convert_poll(m_handle, m_pollBuf, m_pollBuf.Length);
            bool drainedAny = DrainLog();
            if (status == 0) return;   // still running

            // Finished: the native side can still hold buffered log lines
            // that didn't fit in this poll's buffer (e.g. a long error
            // message) — keep draining until a poll comes back empty, so
            // AllLog/LastLogLine reflect everything before IsDone flips.
            // The native side keeps draining after completion (see
            // gsplat_convert_poll's doc comment), so this is safe to loop.
            while (drainedAny)
            {
                NativeMethods.gsplat_convert_poll(m_handle, m_pollBuf, m_pollBuf.Length);
                drainedAny = DrainLog();
            }

            IsRunning = false;
            IsDone = true;
            Succeeded = status == 1;

            IntPtr resultPtr = NativeMethods.gsplat_convert_result_json(m_handle);
            string resultText = resultPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(resultPtr) : null;
            if (!Succeeded)
                Error = resultText ?? "conversion failed (no error message returned)";
            // On success resultText is the manifest JSON; OutputUsstPath was
            // already derived from the options we sent (this panel always
            // supplies non-empty outDir/name, so there is no default-path
            // ambiguity to resolve out of the manifest).
        }

        /// <returns>True if any new log line was decoded from this poll's buffer.</returns>
        bool DrainLog()
        {
            int nul = Array.IndexOf(m_pollBuf, (byte)0);
            if (nul <= 0) return false;   // nothing new
            string chunk = Encoding.UTF8.GetString(m_pollBuf, 0, nul);
            bool any = false;
            foreach (var line in chunk.Split('\n'))
            {
                if (line.Length == 0) continue;
                any = true;
                m_log.Add(line);
                LastLogLine = line;
                if (m_log.Count > k_maxLogLines) m_log.RemoveAt(0);
            }
            return any;
        }

        /// <summary>Cooperative cancel: the worker stops at its next log line, not immediately.</summary>
        public void Cancel()
        {
            if (m_handle != IntPtr.Zero) NativeMethods.gsplat_convert_cancel(m_handle);
        }

        /// <summary>
        /// Releases the native handle. Does NOT wait for the worker thread to
        /// stop — gsplat_convert_free detaches rather than joins (cancellation
        /// is only checked between log lines, and several stretches of the
        /// pipeline emit none at all, so joining here could block the caller
        /// for minutes; on a mobile headset during app teardown or a scene
        /// change that is an ANR). This calls Cancel() first when the job is
        /// still running, so the worker at least gets the chance to stop
        /// quickly — but Dispose itself never blocks on it.
        /// Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (m_handle == IntPtr.Zero) return;
            if (IsRunning) NativeMethods.gsplat_convert_cancel(m_handle);
            NativeMethods.gsplat_convert_free(m_handle);   // non-blocking
            m_handle = IntPtr.Zero;
        }
    }

    public static class SplatConverter
    {
        public static readonly string[] SupportedInputExtensions =
            { "ply", "spz", "splat", "ksplat", "sog", "zip" };

        static bool? s_available;

        /// <summary>
        /// True if the native plugin is loadable and reports the ABI version
        /// this wrapper speaks (1). Cached after the first call. Never
        /// throws — a missing/mismatched plugin (e.g. this build was made
        /// without the native binaries) just disables conversion.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (s_available.HasValue) return s_available.Value;
                try
                {
                    s_available = NativeMethods.gsplat_convert_abi_version() == 1;
                }
                catch (DllNotFoundException)
                {
                    s_available = false;
                }
                catch (EntryPointNotFoundException)
                {
                    s_available = false;
                }
                return s_available.Value;
            }
        }

        // --- memory guard ---
        //
        // gsplat_convert has no OOM protection: an allocation failure aborts
        // the process natively with no exception and no message (this is a
        // Rust allocator abort, not a .NET OutOfMemoryException — nothing on
        // the C# side can catch it). This is the only line of defense against
        // that on memory-constrained mobile headsets, so the estimate below
        // is deliberately conservative rather than tight.
        //
        // Measured on Windows x86_64, tools/splat-pipeline's release
        // build-lod-unity.exe, converting synthetic PLYs matching
        // build-lod-unity/tests/e2e.rs's generator (14 float32 properties per
        // splat = 56 bytes/splat uncompressed *SH0* input), sampling
        // System.Diagnostics.Process.PeakWorkingSet64 (OS-tracked all-time
        // high, not a polling sample) to completion:
        //
        //   tiny-lod  (default):
        //     100k splats, 5,600,362 B input -> peak 30,007,296 B
        //     300k splats, 16,800,362 B input -> peak 71,778,304 B
        //     => ~209 B RAM per input splat marginal, ~8.7 MB fixed overhead
        //   bhatt-lod (quality=true, worse case):
        //     100k splats, 5,600,362 B input -> peak 45,699,072 B
        //     300k splats, 16,800,362 B input -> peak 117,809,152 B
        //     => ~361 B RAM per input splat marginal, ~9.2 MB fixed overhead
        //
        // Those per-splat ratios were measured against SH0 (56 B/splat)
        // input, i.e. no f_rest_* properties. A higher SH degree adds more
        // input bytes per splat (up to 176 B/splat at SH degree 2, 44
        // float32s incl. f_rest_0..23) but does NOT scale the *processing*
        // RAM 1:1 with input file size -- splat *count* is what mostly
        // drives RAM, not input byte count. A 370 MB SH-degree-2 .ply
        // (~2.2M splats) stays under ~1.1 GB, whereas a file-size multiplier
        // would put it near 6 GB and reject it. Decoding still keeps the raw
        // input SH coefficients in memory alongside the per-splat processing
        // cost, so that part IS added on top, per splat, as
        // `ShCoefficientCount * 4` bytes (each f_rest_* value is stored as a
        // float once decoded).
        const long k_tinyRamBytesPerSplat = 209;   // measured, tiny-lod, SH0 input
        const long k_bhattRamBytesPerSplat = 361;  // measured, bhatt-lod, SH0 input
        const int k_bytesPerShCoefficient = 4;     // f_rest_* decoded to float
        const long k_fixedOverheadBytes = 32L * 1024 * 1024;     // ~3x the measured ~9-10MB fixed overhead
        // Applied on top of every estimate below (splat-count-based and
        // file-size-based alike). Kept modest because the estimates below
        // are already close to the measured ratios.
        const double k_safetyFactor = 1.5;

        // --- fallback ratios: used only when the splat count can't be read
        // from a PLY header (non-.ply input, or an ascii .ply, whose
        // per-vertex byte size isn't fixed) ---
        //
        // Only .ply's ratio is directly measured (56 B/splat uncompressed
        // SH0 .ply, see above). Compressed container formats
        // (.spz/.splat/.ksplat) pack more splats into fewer input bytes than
        // raw .ply, so their *effective* RAM-per-input-byte ratio is higher
        // even though the underlying per-splat RAM cost is unchanged -- not
        // measured on-device, so scaled up from the raw-.ply ratio by a
        // conservative guess at each format's size reduction versus raw
        // float32 .ply (SPZ-family formats are commonly quantized+compressed
        // to a fraction of raw .ply size; ~4x is a conservative guess
        // pending real numbers). .sog/.zip are archives of unknown internal
        // structure (could bundle multiple assets), so they keep the
        // largest, most conservative ratio.
        static double RawPlyRamBytesPerFileByte(bool quality) =>
            (quality ? k_bhattRamBytesPerSplat : k_tinyRamBytesPerSplat) / 56.0;

        static double FallbackRamRatio(string extension, bool quality)
        {
            double rawPlyRatio = RawPlyRamBytesPerFileByte(quality); // ~3.73x tiny, ~6.45x bhatt
            switch (extension.ToLowerInvariant())
            {
                case ".ply":
                    return rawPlyRatio; // ascii .ply or unparseable header
                case ".spz":
                case ".splat":
                case ".ksplat":
                    return rawPlyRatio * 4.0;
                case ".sog":
                case ".zip":
                default:
                    return rawPlyRatio * 6.0;
            }
        }

        // How much *additional* RAM we're willing to let a conversion claim
        // on top of whatever the running XR app already holds.
        //
        // Measured on Quest 3, `/proc/meminfo`:
        //   MemTotal:     7,943,148 kB  (~7.6 GB physical RAM)
        //   MemAvailable: 3,643,676 kB  (~3.5 GB free at time of measurement)
        // Budgeting 40% of *physical* RAM (via SystemInfo.systemMemorySize,
        // which reports physical RAM in MB) lands close to, and safely under,
        // the observed MemAvailable headroom, while not hard-coding a
        // Quest-3-specific absolute number that would misjudge other mobile
        // headsets/phones with different physical RAM. 768 MB is kept as an
        // absolute floor for unusually memory-constrained devices.
        const long k_minMobileConversionRamBytes = 768L * 1024 * 1024;
        const double k_mobileRamBudgetFraction = 0.4;

        static long MaxMobileConversionRamBytes
        {
            get
            {
                long physicalRamBytes = (long)SystemInfo.systemMemorySize * 1024L * 1024L;
                long budget = (long)(physicalRamBytes * k_mobileRamBudgetFraction);
                return Math.Max(budget, k_minMobileConversionRamBytes);
            }
        }

        /// <summary>
        /// How many leading bytes of a candidate input to read for
        /// <see cref="PlyHeader.TryParse"/> — ample for any real splat PLY
        /// header (property lists are short; this is generous headroom).
        /// </summary>
        public const int HeaderPrefixBytes = 64 * 1024;

        /// <summary>
        /// On a mobile platform, rejects inputs whose estimated conversion RAM
        /// (see constants above) exceeds the guard threshold. Always true on
        /// non-mobile platforms (desktop/Editor headroom is assumed ample).
        /// </summary>
        public static bool CheckInputSize(string path, bool quality, out string warning)
        {
            warning = null;
            if (!Application.isMobilePlatform) return true;

            long fileSize;
            try
            {
                fileSize = new FileInfo(path).Length;
            }
            catch (Exception e)
            {
                warning = $"could not read '{path}': {e.Message}";
                return false;
            }

            byte[] headerBytes = null;
            if (string.Equals(Path.GetExtension(path), ".ply", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    headerBytes = ReadHeaderPrefix(path);
                }
                catch (Exception)
                {
                    headerBytes = null; // fall back to file-size estimate below
                }
            }

            return CheckEstimatedSize(fileSize, Path.GetFileName(path), headerBytes, quality, out warning);
        }

        static byte[] ReadHeaderPrefix(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            return reader.ReadBytes((int)Math.Min(HeaderPrefixBytes, fs.Length));
        }

        /// <summary>
        /// Same guard as <see cref="CheckInputSize"/>, for an SAF-picked file
        /// (SplatFilePicker) where there is no filesystem path to stat --
        /// only whatever size the content provider reported via
        /// OpenableColumns.SIZE (SplatFilePicker.Size). That value can be -1
        /// when the provider didn't report one; the guard cannot estimate
        /// anything from an unknown size, so it warns but allows the
        /// conversion to proceed rather than blocking on missing information.
        /// <paramref name="headerBytes"/> is the leading
        /// <see cref="HeaderPrefixBytes"/> of the picked file (see
        /// SplatFilePicker.HeaderBytes), or null/empty if unavailable -- the
        /// estimate falls back to the file-size heuristic in that case.
        /// </summary>
        public static bool CheckInputSizeByLength(long sizeBytes, string displayName, byte[] headerBytes, bool quality, out string warning)
        {
            warning = null;
            if (!Application.isMobilePlatform) return true;

            if (sizeBytes < 0)
            {
                warning = $"'{displayName}': ファイルサイズが不明です（SAFプロバイダが未報告）。" +
                    "事前のメモリ見積もりができないため、警告のみで続行します。";
                return true;
            }

            return CheckEstimatedSize(sizeBytes, displayName, headerBytes, quality, out warning);
        }

        static bool CheckEstimatedSize(long fileSize, string displayName, byte[] headerBytes, bool quality, out string warning)
        {
            warning = null;
            long estimated = EstimateConversionRamBytes(fileSize, displayName, headerBytes, quality);
            long maxBytes = MaxMobileConversionRamBytes;
            if (estimated <= maxBytes) return true;

            warning =
                $"'{displayName}' ({fileSize / (1024f * 1024f):0.#} MB) needs an estimated " +
                $"{estimated / (1024f * 1024f):0.#} MB to convert on-device, over the " +
                $"{maxBytes / (1024f * 1024f):0.#} MB guard — converting would likely " +
                "crash the app (native OOM abort, no error message). Convert on desktop " +
                "(Tools > Gsplat LoD > Setup Window) and copy the .usst/.usc/.manifest.json to the device instead.";
            return false;
        }

        /// <summary>
        /// Estimates on-device conversion RAM in bytes. Prefers a splat-count
        /// based estimate (accurate regardless of SH degree) when
        /// <paramref name="headerBytes"/> parses as a *binary* PLY header;
        /// otherwise (non-.ply input, unparseable header, or ascii .ply,
        /// which has no fixed per-vertex byte size) falls back to a
        /// file-size-based estimate using a per-extension ratio. Public so
        /// EditMode tests can cover the regression scenario this guard
        /// exists for (see PlyHeaderTests).
        /// </summary>
        public static long EstimateConversionRamBytes(long fileSize, string displayName, byte[] headerBytes, bool quality)
        {
            if (headerBytes != null && headerBytes.Length > 0 &&
                PlyHeader.TryParse(headerBytes, out var header) &&
                header.IsBinary && header.VertexCount >= 0)
            {
                long perSplatBytes = (quality ? k_bhattRamBytesPerSplat : k_tinyRamBytesPerSplat)
                    + (long)header.ShCoefficientCount * k_bytesPerShCoefficient;
                double raw = k_fixedOverheadBytes + header.VertexCount * (double)perSplatBytes;
                return (long)(raw * k_safetyFactor);
            }

            string ext = Path.GetExtension(displayName);
            double ratio = FallbackRamRatio(ext, quality);
            double rawFallback = k_fixedOverheadBytes + fileSize * ratio;
            return (long)(rawFallback * k_safetyFactor);
        }
    }
}
