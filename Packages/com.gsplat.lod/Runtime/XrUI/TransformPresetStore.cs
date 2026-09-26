// Per-capture transform presets for the setup panel. Every .usst comes out of
// its own capture pipeline, so position/rotation/scale that suit one file are
// meaningless for the next; the panel stores what the user dialed in, keyed by
// file name, and re-applies it the next time that file is loaded.
//
// Stored as a single JSON file in persistentDataPath so it survives app
// restarts and can be pulled off the headset with adb.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GsplatLod.XrUI
{
    [Serializable]
    public sealed class TransformPreset
    {
        public string Key;
        public Vector3 Position;
        public Vector3 Euler;
        public float Scale = 1f;
        public bool FlipX;
    }

    public static class TransformPresetStore
    {
        const string k_fileName = "gsplat_transform_presets.json";

        [Serializable]
        sealed class Payload
        {
            public List<TransformPreset> Presets = new();
        }

        // Deliberately no static cache: the file is tiny, and a cached copy that
        // goes stale (domain reload, an external edit, a second panel) is exactly
        // the failure that makes presets look like they were never saved.

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, k_fileName);

        // File name without extension, case-folded: the same capture copied to
        // another folder should still find its preset.
        public static string KeyFor(string usstPath) =>
            string.IsNullOrEmpty(usstPath)
                ? "" : Path.GetFileNameWithoutExtension(usstPath).ToLowerInvariant();

        public static TransformPreset Find(string usstPath)
        {
            string key = KeyFor(usstPath);
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var p in Load().Presets)
                if (p.Key == key) return p;
            return null;
        }

        public static void Save(string usstPath, Vector3 position, Vector3 euler,
            float scale, bool flipX)
        {
            string key = KeyFor(usstPath);
            if (string.IsNullOrEmpty(key)) return;
            var data = Load();
            var p = data.Presets.Find(x => x.Key == key);
            if (p == null)
            {
                p = new TransformPreset { Key = key };
                data.Presets.Add(p);
            }
            p.Position = position;
            p.Euler = euler;
            p.Scale = scale;
            p.FlipX = flipX;
            Write(data);
            Debug.Log($"[GsplatLod] preset saved '{key}' pos={position} euler={euler} " +
                      $"scale={scale} flipX={flipX} -> {FilePath}");
        }

        public static bool Delete(string usstPath)
        {
            string key = KeyFor(usstPath);
            var data = Load();
            int removed = data.Presets.RemoveAll(x => x.Key == key);
            if (removed > 0) Write(data);
            return removed > 0;
        }

        static Payload Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var parsed = JsonUtility.FromJson<Payload>(File.ReadAllText(FilePath));
                    if (parsed?.Presets != null) return parsed;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GsplatLod] preset load failed: {e.Message}");
            }
            return new Payload();
        }

        static void Write(Payload data)
        {
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GsplatLod] preset save failed: {e.Message}");
            }
        }
    }
}
