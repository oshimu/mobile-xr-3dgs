// Managed wrapper around Assets/GsplatFilePicker.androidlib's
// GsplatFilePickerActivity -- Android's standard Storage Access Framework
// document picker (ACTION_OPEN_DOCUMENT). Confirmed on a Quest 3 device to
// resolve to com.android.documentsui.picker.PickActivity and to actually
// render and respond to input inside the headset. No special permission is
// needed.
//
// Polling model, same reasoning as SplatConverter.ConvertJob: the picker
// activity has no way to call back into whatever Unity scene happens to be
// active when the user finishes picking (or cancels) -- UnitySendMessage
// requires a named GameObject that may not exist by the time the picker
// returns. Instead, GsplatFilePickerActivity writes its result to static
// Java fields and finishes; this wrapper polls them from Update().
using System;
using UnityEngine;

namespace GsplatLod
{
    public enum PickState
    {
        Idle = 0,
        Running = 1,
        Success = 2,
        Cancelled = 3,
        Error = 4,
    }

    public static class SplatFilePicker
    {
        const string k_activityClass = "com.oshimu.gsplat.GsplatFilePickerActivity";

        /// <summary>True only on an Android player (never in the Editor or on other platforms).</summary>
        public static bool IsSupported =>
#if UNITY_ANDROID && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>
        /// POSIX fd of the picked document, valid once Poll() returns
        /// PickState.Success. Ownership transfers to the caller: the Java
        /// side detached it (see GsplatFilePickerActivity.onActivityResult),
        /// so nothing else will close it -- whoever consumes Fd must close
        /// it exactly once. build-lod-unity's InputSource::Fd does this by
        /// wrapping it in a Rust File and letting it drop.
        /// </summary>
        public static int Fd { get; private set; } = -1;
        public static string DisplayName { get; private set; } = "";
        public static long Size { get; private set; } = -1;
        public static string Error { get; private set; }

        /// <summary>
        /// Leading bytes of the picked file's content (up to
        /// SplatConverter.HeaderPrefixBytes), read via a *separate*
        /// ContentResolver.openInputStream() on the Java side -- it does not
        /// touch the fd returned by <see cref="Fd"/> (which is detached for
        /// the convert pipeline and must keep its position untouched). Empty
        /// (never null) if the read failed or wasn't attempted; callers that
        /// need a PLY header should treat empty the same as "unavailable"
        /// and fall back to a file-size-based estimate.
        /// </summary>
        public static byte[] HeaderBytes { get; private set; } = Array.Empty<byte>();

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>Starts the SAF document picker (ACTION_OPEN_DOCUMENT).</summary>
        public static void Pick()
        {
            using var activityClass = new AndroidJavaClass(k_activityClass);
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            activityClass.CallStatic("startPick", currentActivity);
        }

        /// <summary>Reads the picker's current result. Safe to call every frame.</summary>
        public static PickState Poll()
        {
            using var activityClass = new AndroidJavaClass(k_activityClass);
            int state = activityClass.GetStatic<int>("state");
            Fd = activityClass.GetStatic<int>("fd");
            DisplayName = activityClass.GetStatic<string>("displayName");
            Size = activityClass.GetStatic<long>("size");
            Error = activityClass.GetStatic<string>("error");
            HeaderBytes = ToByteArray(activityClass.GetStatic<sbyte[]>("headerBytes"));
            return (PickState)state;
        }

        /// <summary>Resets the picker's static result fields (and this wrapper's cache) to Idle.</summary>
        public static void Reset()
        {
            using var activityClass = new AndroidJavaClass(k_activityClass);
            activityClass.CallStatic("reset");
            Fd = -1;
            DisplayName = "";
            Size = -1;
            Error = null;
            HeaderBytes = Array.Empty<byte>();
        }

        /// <summary>Java `byte[]` (marshaled to C# as sbyte[]) -> C# byte[], element-size-preserving reinterpret.</summary>
        static byte[] ToByteArray(sbyte[] raw)
        {
            if (raw == null || raw.Length == 0) return Array.Empty<byte>();
            var bytes = new byte[raw.Length];
            Buffer.BlockCopy(raw, 0, bytes, 0, raw.Length);
            return bytes;
        }

        /// <summary>
        /// Closes a fd that was picked (via Poll() returning Success) but
        /// then never handed to a ConvertJob -- e.g. the memory guard
        /// rejected it, the user picked a different file before converting,
        /// or the panel closed with a picked-but-unused selection. The fd
        /// was detached on the Java side (see GsplatFilePickerActivity), so
        /// nothing else will ever close it unless this does. No-op for
        /// fd &lt; 0.
        /// </summary>
        public static void CloseFd(int fd)
        {
            if (fd < 0) return;
            try
            {
                using var pfdClass = new AndroidJavaClass("android.os.ParcelFileDescriptor");
                using var pfd = pfdClass.CallStatic<AndroidJavaObject>("adoptFd", fd);
                pfd.Call("close");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GsplatLod] failed to close leftover fd {fd}: {e.Message}");
            }
        }
#else
        public static void Pick() { }
        public static PickState Poll() => PickState.Idle;
        public static void Reset() { }
        public static void CloseFd(int fd) { }
#endif
    }
}
