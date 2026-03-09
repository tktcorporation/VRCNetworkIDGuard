#nullable enable
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace VRCNetworkIDGuard
{
    /// <summary>
    /// NetworkID Guard のプロジェクト固有設定。
    ///
    /// Edit > Project Settings > VRC NetworkID Guard から設定可能。
    /// シーンファイルと Pinned ファイルのパスをプロジェクトに合わせて設定する。
    /// 設定は EditorPrefs に保存される。
    /// </summary>
    public static class NetworkIDGuardSettings
    {
        private const string ScenePathKey = "VRCNetworkIDGuard_ScenePath";
        private const string PinnedPathKey = "VRCNetworkIDGuard_PinnedPath";

        public static string ScenePath
        {
            get => EditorPrefs.GetString(ScenePathKey, "");
            set
            {
                EditorPrefs.SetString(ScenePathKey, value);
                FileOperations.DefaultScenePath = value;
            }
        }

        public static string PinnedPath
        {
            get => EditorPrefs.GetString(PinnedPathKey, "networkids_pinned.json");
            set
            {
                EditorPrefs.SetString(PinnedPathKey, value);
                FileOperations.DefaultPinnedPath = value;
            }
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            FileOperations.DefaultScenePath = ScenePath;
            FileOperations.DefaultPinnedPath = PinnedPath;
        }
    }

    /// <summary>
    /// Project Settings ウィンドウに設定UIを追加する。
    /// </summary>
    public class NetworkIDGuardSettingsProvider : SettingsProvider
    {
        public NetworkIDGuardSettingsProvider()
            : base("Project/VRC NetworkID Guard", SettingsScope.Project) { }

        public override void OnGUI(string searchContext)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("VRC NetworkID Guard Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            var scenePath = EditorGUILayout.TextField("Scene Path", NetworkIDGuardSettings.ScenePath);
            if (scenePath != NetworkIDGuardSettings.ScenePath)
                NetworkIDGuardSettings.ScenePath = scenePath;

            var pinnedPath = EditorGUILayout.TextField("Pinned File Path", NetworkIDGuardSettings.PinnedPath);
            if (pinnedPath != NetworkIDGuardSettings.PinnedPath)
                NetworkIDGuardSettings.PinnedPath = pinnedPath;

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "Scene Path: VRChat ワールドのシーンファイルのパス (例: Assets/Scenes/MyWorld.unity)\n" +
                "Pinned File Path: NetworkID の固定情報を保存する JSON ファイルのパス",
                MessageType.Info);
        }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new NetworkIDGuardSettingsProvider
            {
                keywords = new[] { "VRChat", "NetworkID", "Guard", "Persistence" }
            };
        }
    }
}
#endif
