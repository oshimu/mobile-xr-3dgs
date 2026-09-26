// Scene setup + conversion helper. Scenes are never edited on disk by tooling;
// this window creates/configures GameObjects in the currently open scene so
// the user saves the scene themselves.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace GsplatLod.Editor
{
    public sealed class GsplatLodSetupWindow : EditorWindow
    {
        string m_usstPath = "";
        string m_plyPath = "";
        string m_outDir = "";
        bool m_quality;
        string m_coordPreset = "identity";
        Vector2 m_scroll;
        string m_convertLog = "";

        static readonly string[] k_coordPresets = { "identity", "flip-z", "flip-y" };

        [MenuItem("Tools/Gsplat LoD/Setup Window")]
        static void Open()
        {
            GetWindow<GsplatLodSetupWindow>("Gsplat LoD Setup");
        }
        void OnGUI()
        {
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

            GUILayout.Label("1. Convert (.ply → .usst/.usc)", EditorStyles.boldLabel);
            DrawConvertSection();

            EditorGUILayout.Space(12);
            GUILayout.Label("2. Scene Setup", EditorStyles.boldLabel);
            DrawSetupSection();

            EditorGUILayout.Space(12);
            GUILayout.Label("3. Runtime Setup UI", EditorStyles.boldLabel);
            DrawRuntimeUiSection();

            EditorGUILayout.EndScrollView();
        }

        void DrawConvertSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                m_plyPath = EditorGUILayout.TextField("Input (.ply/.spz)", m_plyPath);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string p = EditorUtility.OpenFilePanelWithFilters("Select splat file", "",
                        new[] { "Splat files", "ply,spz,splat,ksplat,sog,zip", "All files", "*" });
                    if (!string.IsNullOrEmpty(p)) m_plyPath = p;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                m_outDir = EditorGUILayout.TextField("Output directory", m_outDir);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string p = EditorUtility.OpenFolderPanel("Select output directory", "", "");
                    if (!string.IsNullOrEmpty(p)) m_outDir = p;
                }
            }

            m_quality = EditorGUILayout.Toggle(new GUIContent("Quality (bhatt-lod)", "既定は tiny-lod（高速）"), m_quality);
            int coordIdx = System.Array.IndexOf(k_coordPresets, m_coordPreset);
            coordIdx = EditorGUILayout.Popup("Coord preset", Mathf.Max(0, coordIdx), k_coordPresets);
            m_coordPreset = k_coordPresets[coordIdx];

            string cli = FindCliPath();
            using (new EditorGUI.DisabledScope(cli == null || string.IsNullOrEmpty(m_plyPath)))
            {
                if (GUILayout.Button("Convert"))
                    RunConvert(cli);
            }
            if (cli == null)
                EditorGUILayout.HelpBox(
                    "build-lod-unity.exe not found. Build it first:\n" +
                    "cd tools/splat-pipeline && cargo build --release", MessageType.Warning);
            if (!string.IsNullOrEmpty(m_convertLog))
                EditorGUILayout.HelpBox(m_convertLog, MessageType.None);
        }

        void DrawSetupSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                m_usstPath = EditorGUILayout.TextField(".usst file", m_usstPath);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string p = EditorUtility.OpenFilePanel("Select .usst", m_outDir, "usst");
                    if (!string.IsNullOrEmpty(p)) m_usstPath = p;
                }
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(m_usstPath) || !File.Exists(m_usstPath)))
            {
                if (GUILayout.Button("Create Streaming Object"))
                    CreateStreamingObject();
            }

            EditorGUILayout.HelpBox(
                "固定プール+チャンクストリーミングでシーンを表示するオブジェクトを作成します。",
                MessageType.Info);
        }

        void DrawRuntimeUiSection()
        {
            EditorGUILayout.HelpBox(
                "実行時に .usst を選び、Config を調整し、ドライバを生成する" +
                "ワールド空間UIパネルを現在開いているシーン（Start など）に追加します。\n" +
                "XRIのレイ/ポークで操作でき（EventSystemのXRUIInputModuleを利用）、" +
                "エディタ再生でも Quest 実機でも動作。フラット画面のオーバーレイは出ません。\n" +
                "シーンはツールでは保存しません — 追加後にご自身で保存してください。",
                MessageType.Info);

            if (GUILayout.Button("Add Setup Panel to Scene"))
                AddSetupUiToScene();
        }

        static void AddSetupUiToScene()
        {
            var existing = Object.FindFirstObjectByType<XrUI.LodSetupPanel>();
            if (existing)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing);
                if (!EditorUtility.DisplayDialog("Setup Panel",
                        "このシーンには既に LodSetupPanel があります。もう1つ追加しますか？",
                        "追加する", "選択のみ"))
                    return;
            }

            var go = new GameObject("Gsplat LoD Setup Panel");
            var ui = go.AddComponent<XrUI.LodSetupPanel>();
            if (ui == null)   // stale/failed compile — don't NRE inside OnGUI
            {
                Object.DestroyImmediate(go);
                Debug.LogError("[GsplatLod] Could not add LodSetupPanel. " +
                               "Wait for scripts to finish compiling (no console errors) and retry.");
                return;
            }
            Undo.RegisterCreatedObjectUndo(go, "Add Gsplat LoD Setup Panel");
            // Default pose: ~1.3m up, 1.5m in front of the origin, facing back
            // toward it (the panel also re-centers to the camera at Start).
            go.transform.position = new Vector3(0f, 1.3f, 1.5f);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            ui.Config = FindOrCreateConfig();
            ui.SplatShader = Shader.Find("GsplatLod/Splat");
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        void CreateStreamingObject()
        {
            var go = new GameObject($"GsplatLod_{Path.GetFileNameWithoutExtension(m_usstPath)}");
            Undo.RegisterCreatedObjectUndo(go, "Create Gsplat LoD Streaming Object");
            var driver = go.AddComponent<LodStreamingDriver>();
            driver.UsstPath = m_usstPath;
            driver.SplatShader = Shader.Find("GsplatLod/Splat");
            driver.GammaToLinear = PlayerSettings.colorSpace == ColorSpace.Linear;
            driver.Config = FindOrCreateConfig();
            go.AddComponent<LodDebugHud>();
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        static LodStreamingConfig FindOrCreateConfig()
        {
            string[] guids = AssetDatabase.FindAssets("t:LodStreamingConfig");
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<LodStreamingConfig>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));

            var config = ScriptableObject.CreateInstance<LodStreamingConfig>();
            if (!AssetDatabase.IsValidFolder("Assets/GsplatLod"))
                AssetDatabase.CreateFolder("Assets", "GsplatLod");
            AssetDatabase.CreateAsset(config, "Assets/GsplatLod/LodStreamingConfig.asset");
            AssetDatabase.SaveAssets();
            return config;
        }

        static string FindCliPath()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string p = Path.Combine(projectRoot, "tools", "splat-pipeline", "target", "release", "build-lod-unity.exe");
            return File.Exists(p) ? p : null;
        }

        System.Diagnostics.Process m_convertProc;
        string m_convertOutUsst;

        // Runs the CLI in its own console window (progress visible there) and
        // polls for exit — no stream redirection. Reading redirected pipes
        // synchronously deadlocks once the converter's stdout fills the pipe
        // buffer, and blocks the editor UI for minutes besides.
        void RunConvert(string cli)
        {
            string outDir = string.IsNullOrEmpty(m_outDir)
                ? Path.GetDirectoryName(m_plyPath)
                : m_outDir;
            string name = Path.GetFileNameWithoutExtension(m_plyPath);

            var args = $"\"{m_plyPath}\" --out \"{outDir}\" --name \"{name}\" --coord {m_coordPreset} --force";
            if (m_quality)
                args += " --quality";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cli,
                Arguments = args,
                UseShellExecute = true,
                CreateNoWindow = false,
            };
            m_convertProc = System.Diagnostics.Process.Start(psi);
            m_convertOutUsst = Path.Combine(outDir, name + ".usst");
            m_convertLog = "変換中... 進捗は開いたコンソールウィンドウに表示されます";
            EditorApplication.update += PollConvert;
        }

        void PollConvert()
        {
            if (m_convertProc == null || !m_convertProc.HasExited)
                return;
            EditorApplication.update -= PollConvert;
            if (m_convertProc.ExitCode == 0)
            {
                m_usstPath = m_convertOutUsst;
                m_convertLog = $"OK: {m_usstPath}";
            }
            else
            {
                m_convertLog = $"FAILED (exit {m_convertProc.ExitCode}) — " +
                               "コマンドラインから実行するとエラー詳細が確認できます（docs/operations.md 参照）";
            }
            m_convertProc.Dispose();
            m_convertProc = null;
            Repaint();
        }
    }
}
