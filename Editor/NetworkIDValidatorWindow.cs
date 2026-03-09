#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using VRC.SDKBase.Editor.BuildPipeline;

// ────────────────────────────────────────────────────────────────────────
// NetworkID Validator - Unity Editor 統合
//
// 背景:
//   VRChatワールドのNetworkIDが変わるとプレイヤーのパーシステンス（セーブデータ）が
//   消失する。複数環境で同一ワールドを管理する場合、外部環境で確定した
//   NetworkIDの割り当てをローカルでも維持する必要がある。
//
// 統合Pinnedファイル (networkids_pinned.json) で一元管理する:
//   - gameObject付きエントリ: ローカルで解決済み。シーン復元と変更検知に使用。
//   - gameObjectなしエントリ: 外部環境のみに存在する予約ID。衝突検知に使用。
//
// このファイルが担う2つの役割:
//
//   1. NetworkIDBuildPreprocessor [IPreprocessBuildWithReport, callbackOrder=-1000]
//      VRChatアップロード時の最終防衛ライン。ビルド処理の先頭で実行され、
//      危険な変更があればダイアログで確認する。
//
//   2. NetworkIDValidatorWindow [EditorWindow]
//      メニュー「VRChat SDK > Utilities > NetworkID Validator」から開く手動チェックUI。
// ────────────────────────────────────────────────────────────────────────

namespace VRCNetworkIDGuard
{

/// <summary>
/// Unity Editor 環境でのプロジェクトルート取得ユーティリティ。
/// NetworkIDBuildValidator と NetworkIDValidatorWindow の両方がここを参照する。
/// </summary>
internal static class EditorPathUtil
{
    /// <summary>
    /// Application.dataPath の親ディレクトリ（= プロジェクトルート）を返す。
    /// Application.dataPath は "...ProjectRoot/Assets" なので、その親がプロジェクトルート。
    /// </summary>
    public static string GetProjectRoot()
    {
        return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
    }
}

internal static class NetworkIDBuildValidator
{
    /// <summary>
    /// ビルド前の NetworkID 検証を実行する。
    ///
    /// 戻り値:
    ///   true  — ビルド続行 OK
    ///   false — ビルドをキャンセル
    /// </summary>
    public static bool ValidateAndPrompt()
    {
        var scenePath = FileOperations.DefaultScenePath;
        var pinnedPath = FileOperations.DefaultPinnedPath;

        var projectRoot = GetProjectRoot();
        var fullScenePath = Path.Combine(projectRoot, scenePath);
        var fullPinnedPath = Path.Combine(projectRoot, pinnedPath);

        if (string.IsNullOrEmpty(scenePath) || !File.Exists(fullScenePath) || !File.Exists(fullPinnedPath))
        {
            return true;
        }

        try
        {
            // シーンが未保存の場合は先に保存する（ファイルベースで検証するため）
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.isDirty && activeScene.path == scenePath)
            {
                Debug.Log("[NetworkID Validator] ビルド前にシーンを保存します...");
                EditorSceneManager.SaveScene(activeScene);
            }

            var content = FileOperations.ReadSceneNormalized(fullScenePath);
            var current = SceneParser.Parse(content);
            var pinned = FileOperations.LoadPinned(fullPinnedPath);

            if (pinned == null)
            {
                Debug.LogWarning("[NetworkID Validator] Pinnedファイルが存在しないため、ビルド前のNetworkID検証をスキップしました。");
                return true;
            }

            // セーブデータ影響検知: エラー時は null（影響情報なしで従来通り動作）
            Dictionary<string, List<PersistenceInfo>>? persistenceMap = null;
            try
            {
                persistenceMap = FileOperations.BuildPersistenceMap(content, projectRoot);
            }
            catch (Exception ex2)
            {
                Debug.LogWarning($"[NetworkID Validator] セーブデータ影響検知中にエラー: {ex2.Message}");
            }

            var result = Validator.Validate(current, pinned, persistenceMap);

            if (!result.IsValid)
            {
                Debug.LogError($"[NetworkID Validator] 危険なNetworkIDの変更を検出しました。\n{result.GetDetailedMessage()}");

                var hasReservedConflicts = result.Changes.Any(c => c.Kind == ChangeKind.ReservedConflict);
                var hasOtherDangerousChanges = result.DangerousChanges.Any(c => c.Kind != ChangeKind.ReservedConflict);

                if (hasReservedConflicts && !hasOtherDangerousChanges)
                {
                    return HandleReservedConflictsOnly(fullScenePath, pinned, result);
                }
                else
                {
                    return HandleDangerousChanges(fullScenePath, pinned, result);
                }
            }
            else if (result.HasAnyChanges)
            {
                Debug.Log($"[NetworkID Validator] 安全な追加が{result.SafeAdditions.Count()}件あります。ビルドを続行します。");
            }
            else
            {
                Debug.Log("[NetworkID Validator] NetworkIDに変更なし。ビルドを続行します。");
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetworkID Validator] ビルド前の検証中にエラーが発生しました: {ex.Message}");
            // 安全側に倒す: 検証に失敗した場合はビルドを中断する
            return false;
        }
    }

    /// <summary>予約ID衝突のみの場合: 再割り当て/キャンセル/リストアの3択ダイアログ</summary>
    private static bool HandleReservedConflictsOnly(
        string scenePath, List<PinnedEntryBase> pinned, ValidationResult result)
    {
        var conflictIDs = string.Join(", ", result.Changes
            .Where(c => c.Kind == ChangeKind.ReservedConflict)
            .Select(c => c.ID).OrderBy(x => x));

        var choice = EditorUtility.DisplayDialogComplex(
            "予約IDとの衝突を検出しました",
            $"{result.GetDetailedMessage()}\n\n" +
            "このままアップロードすると外部環境のセーブデータに影響する可能性があります。\n\n" +
            "「再割り当て」: 衝突IDに新しいIDを自動採番してビルド続行\n" +
            "「リストア」: Pinnedの状態に復元してビルド続行（衝突エントリは除去）",
            "再割り当てしてビルド続行",
            "ビルドをキャンセル",
            "リストアしてビルド続行"
        );

        switch (choice)
        {
            case 0: // 再割り当て
                var reassignments = FileOperations.ReassignScene(scenePath, pinned);
                AssetDatabase.Refresh();
                var details = string.Join(", ", reassignments.Select(r => $"{r.Key}→{r.Value}"));
                Debug.Log($"[NetworkID Validator] 予約IDとの衝突を再割り当てで解消しました（シーンのみ）: {details}");
                return true;
            case 2: // リストア
                FileOperations.RestoreScene(scenePath, pinned);
                AssetDatabase.Refresh();
                Debug.Log("[NetworkID Validator] NetworkIDをPinnedから復元しました。ビルドを続行します。");
                return true;
            default: // キャンセル
                Debug.LogWarning($"[NetworkID Validator] ビルドをキャンセルしました。\n{result.GetSummaryMessage()}");
                return false;
        }
    }

    /// <summary>予約衝突以外の危険な変更がある場合: リストア/キャンセルの2択ダイアログ</summary>
    private static bool HandleDangerousChanges(
        string scenePath, List<PinnedEntryBase> pinned, ValidationResult result)
    {
        bool restore = EditorUtility.DisplayDialog(
            "NetworkID の変更を検出しました",
            $"危険なNetworkIDの変更が検出されました。\n" +
            $"このままアップロードするとプレイヤーのセーブデータが消失する可能性があります。\n\n" +
            $"{result.GetDetailedMessage()}\n\n" +
            "Pinnedファイルの状態にリストアしてからビルドを続行しますか？",
            "リストアしてビルド続行",
            "ビルドをキャンセル"
        );

        if (restore)
        {
            FileOperations.RestoreScene(scenePath, pinned);
            AssetDatabase.Refresh();
            Debug.Log("[NetworkID Validator] NetworkIDをPinnedから復元しました。ビルドを続行します。");
            return true;
        }
        else
        {
            Debug.LogWarning($"[NetworkID Validator] ビルドをキャンセルしました。\n{result.GetSummaryMessage()}");
            return false;
        }
    }

    private static string GetProjectRoot() => EditorPathUtil.GetProjectRoot();
}

/// <summary>
/// VRChat SDK のアップロード前に NetworkID を検証するコールバック。
/// </summary>
public class NetworkIDVRCBuildCallback : IVRCSDKBuildRequestedCallback
{
    public int callbackOrder => -1000;

    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        if (requestedBuildType != VRCSDKRequestedBuildType.Scene)
            return true;

        return NetworkIDBuildValidator.ValidateAndPrompt();
    }
}

/// <summary>
/// Unity 標準ビルド（File &gt; Build）時のフォールバック。
/// </summary>
public class NetworkIDBuildPreprocessor : IPreprocessBuildWithReport
{
    public int callbackOrder => -1000;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (!NetworkIDBuildValidator.ValidateAndPrompt())
        {
            throw new BuildFailedException(
                "[NetworkID Validator] NetworkID検証に失敗しました。ビルドを中断します。");
        }
    }
}

/// <summary>
/// Play モード突入時に NetworkID を検証するフック。
/// </summary>
[InitializeOnLoad]
internal static class NetworkIDPlayModeValidator
{
    static NetworkIDPlayModeValidator()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingEditMode)
            return;

        if (!NetworkIDBuildValidator.ValidateAndPrompt())
        {
            EditorApplication.isPlaying = false;
            Debug.LogWarning("[NetworkID Validator] NetworkID検証に失敗したため、Play モードをキャンセルしました。");
        }
    }
}

/// <summary>
/// NetworkID Validator エディタウィンドウ。
/// メニュー「VRChat SDK &gt; Utilities &gt; NetworkID Validator」から開く。
/// </summary>
public class NetworkIDValidatorWindow : EditorWindow
{
    private ValidationResult? lastResult;
    private int currentEntryCount;
    private int localEntryCount;
    private int reservedEntryCount;
    private Vector2 scrollPosition;
    private string? lastError;

    [MenuItem("VRChat SDK/Utilities/NetworkID Validator", false, 991)]
    public static void ShowWindow()
    {
        var window = GetWindow<NetworkIDValidatorWindow>();
        window.titleContent = new GUIContent("NetworkID Validator");
        window.minSize = new Vector2(420, 380);
        window.Show();
    }

    private void OnEnable()
    {
        RunValidation();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("NetworkID Validator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "NetworkIDの変更を検知し、意図しないユーザーデータのリセットを防止します。\n" +
            "ビルド時に危険な変更が検出された場合はダイアログで確認します。\n" +
            "「リストアしてビルド続行」でPinnedの状態に復元してビルドを継続できます。",
            MessageType.Info
        );

        EditorGUILayout.Space(10);

        // パス設定セクション
        EditorGUILayout.LabelField("パス設定", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            var scenePath = EditorGUILayout.TextField("Scene Path", FileOperations.DefaultScenePath);
            if (scenePath != FileOperations.DefaultScenePath)
                NetworkIDGuardSettings.ScenePath = scenePath;

            var pinnedPath = EditorGUILayout.TextField("Pinned File Path", FileOperations.DefaultPinnedPath);
            if (pinnedPath != FileOperations.DefaultPinnedPath)
                NetworkIDGuardSettings.PinnedPath = pinnedPath;
        }

        EditorGUILayout.Space(10);

        // ステータス表示
        EditorGUILayout.LabelField("ステータス", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField($"シーン: {currentEntryCount}件");
            EditorGUILayout.LabelField($"Pinned  ローカル: {localEntryCount}件  /  予約: {reservedEntryCount}件");
            EditorGUILayout.Space(4);

            if (lastError != null)
            {
                var errStyle = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = new Color(0.9f, 0.3f, 0.3f) },
                    wordWrap = true
                };
                EditorGUILayout.LabelField(lastError, errStyle);
            }
            else if (lastResult == null)
            {
                EditorGUILayout.LabelField("検証未実行", EditorStyles.miniLabel);
            }
            else if (!lastResult.IsValid)
            {
                var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.3f, 0.3f) } };
                EditorGUILayout.LabelField("NG - 危険な変更あり", style);
            }
            else if (lastResult.HasSafeAdditionsOnly)
            {
                var style = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.8f, 0.8f, 0.2f) } };
                EditorGUILayout.LabelField("OK - 安全な追加のみ", style);
            }
            else
            {
                var style = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.2f, 0.8f, 0.2f) } };
                EditorGUILayout.LabelField("OK - 変更なし", style);
            }
        }

        EditorGUILayout.Space(10);

        // ボタン
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("検証", GUILayout.Height(28)))
                RunValidation();

            using (new EditorGUI.DisabledScope(lastResult == null || !lastResult.HasAnyChanges))
            {
                if (GUILayout.Button("リストア", GUILayout.Height(28)))
                    RestoreFromPinned();
            }

            if (GUILayout.Button("Import (外部JSON)", GUILayout.Height(28)))
                ImportFromPartnerJson();
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.HelpBox(
            "Import: 外部環境の network_ids JSON ({\"ID\": \"/path\"} 形式) をロードし、\n" +
            "シーン内オブジェクトをパスで検索して fileID を解決します。\n" +
            "CLI の import-pinned より正確（stripped prefab も解決可能）。",
            MessageType.None
        );

        EditorGUILayout.Space(10);

        // 変更内容の詳細
        if (lastResult != null && lastResult.HasAnyChanges)
        {
            EditorGUILayout.LabelField("変更内容", EditorStyles.boldLabel);
            scrollPosition = EditorGUILayout.BeginScrollView(
                scrollPosition, EditorStyles.helpBox, GUILayout.MinHeight(100));
            EditorGUILayout.LabelField(lastResult.GetDetailedMessage(), EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }
    }

    // ─── Import (パスベース / Unity API) ──────────────────────────────────

    /// <summary>
    /// 外部環境の network_ids.json ({ID: path}) をロードし、Unity API でパスから fileID を
    /// 解決して Pinned ファイルとシーンの NetworkIDs セクションを更新する。
    /// </summary>
    private void ImportFromPartnerJson()
    {
        var filePath = EditorUtility.OpenFilePanel("外部環境の network_ids JSON を選択", "", "json");
        if (string.IsNullOrEmpty(filePath)) return;

        var partnerMapping = FileOperations.LoadPartnerJson(filePath);
        if (partnerMapping == null || partnerMapping.Count == 0)
        {
            EditorUtility.DisplayDialog("エラー", "JSONの読み込みに失敗しました。", "OK");
            return;
        }

        // シーン内の全オブジェクトのパス → GameObject マップを構築
        var scene = SceneManager.GetActiveScene();
        if (!scene.isLoaded)
        {
            EditorUtility.DisplayDialog("エラー", "シーンが読み込まれていません。\n対象シーンを開いてから実行してください。", "OK");
            return;
        }

        var pathToGo = new Dictionary<string, GameObject>();
        foreach (var root in scene.GetRootGameObjects())
            BuildPathMap("", root, pathToGo);

        // パスベースマッチング → PinnedEntryBase を生成
        var pinnedEntries = new List<PinnedEntryBase>();
        int localCount = 0, reservedCount = 0;

        foreach (var kvp in partnerMapping.OrderBy(k => k.Key))
        {
            var id = kvp.Key;
            var path = kvp.Value;

            if (pathToGo.TryGetValue(path, out var go))
            {
                var fileId = GetSceneFileID(go);
                if (fileId == 0)
                {
                    Debug.LogWarning($"[NetworkID Validator] fileID を取得できません（ID {id}, path: {path}）→ 予約エントリとして登録");
                    pinnedEntries.Add(new ReservedEntry(id, path));
                    reservedCount++;
                }
                else
                {
                    pinnedEntries.Add(new LocalEntry(id, path, fileId.ToString(), GetNetworkComponentTypes(go).AsReadOnly()));
                    localCount++;
                }
            }
            else
            {
                pinnedEntries.Add(new ReservedEntry(id, path));
                reservedCount++;
            }
        }

        if (!EditorUtility.DisplayDialog(
            "Import 確認",
            $"外部 JSON: {partnerMapping.Count}件\n" +
            $"  ローカル解決: {localCount}件\n" +
            $"  予約（ローカルに存在しない）: {reservedCount}件\n\n" +
            "Pinned ファイルとシーンの NetworkIDs を更新しますか？",
            "Import", "キャンセル"))
            return;

        var projectRoot = GetProjectRoot();
        var pinnedPath = Path.Combine(projectRoot, FileOperations.DefaultPinnedPath);
        var scenePath = Path.Combine(projectRoot, FileOperations.DefaultScenePath);

        try
        {
            FileOperations.SavePinned(pinnedPath, pinnedEntries);
            FileOperations.RestoreScene(scenePath, pinnedEntries);
            AssetDatabase.Refresh();

            Debug.Log($"[NetworkID Validator] Import 完了: ローカル {localCount}件, 予約 {reservedCount}件");
            RunValidation();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetworkID Validator] Import 中にエラーが発生しました: {ex.Message}");
            EditorUtility.DisplayDialog("エラー", $"Import に失敗しました。\n{ex.Message}", "OK");
        }
    }

    /// <summary>
    /// シーン内の全 GameObject のパスマップを再帰構築する。
    /// </summary>
    private static void BuildPathMap(string parentPath, GameObject obj, Dictionary<string, GameObject> map)
    {
        var path = parentPath + "/" + obj.name;
        if (!map.ContainsKey(path))
            map[path] = obj;

        for (int i = 0; i < obj.transform.childCount; i++)
            BuildPathMap(path, obj.transform.GetChild(i).gameObject, map);
    }

    /// <summary>
    /// GameObject に付いている VRC 系ネットワークコンポーネントの型名リストを返す。
    /// </summary>
    private static List<string> GetNetworkComponentTypes(GameObject go)
    {
        var types = new List<string>();
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            var fullName = c.GetType().FullName ?? "";
            if (fullName.StartsWith("VRC.Udon") || fullName.StartsWith("VRC.SDK3.Components"))
                types.Add(fullName);
        }
        return types;
    }

    /// <summary>
    /// GameObject のシーン YAML 上の fileID を取得する。
    /// </summary>
    private static long GetSceneFileID(GameObject go)
    {
        var so = new SerializedObject(go);
        var inspectorModeProperty = typeof(SerializedObject).GetProperty(
            "inspectorMode", BindingFlags.NonPublic | BindingFlags.Instance);
        if (inspectorModeProperty != null)
            inspectorModeProperty.SetValue(so, InspectorMode.Debug);

        var prop = so.FindProperty("m_LocalIdentfierInFile");
        return prop?.longValue ?? 0;
    }

    // ─── 検証 / リストア ───────────────────────────────────────────────────

    /// <summary>
    /// ウィンドウが開いていれば検証を再実行して表示を更新する。
    /// </summary>
    internal static void RefreshIfOpen()
    {
        var windows = Resources.FindObjectsOfTypeAll<NetworkIDValidatorWindow>();
        foreach (var window in windows)
            window.RunValidation();
    }

    /// <summary>
    /// シーンと Pinned ファイルを比較して検証結果を更新する。
    /// </summary>
    private void RunValidation()
    {
        lastError = null;
        var projectRoot = GetProjectRoot();
        var scenePath = Path.Combine(projectRoot, FileOperations.DefaultScenePath);
        var pinnedPath = Path.Combine(projectRoot, FileOperations.DefaultPinnedPath);

        if (string.IsNullOrEmpty(FileOperations.DefaultScenePath))
        {
            lastError = "シーンパスが未設定です。Project Settings > VRC NetworkID Guard でパスを設定してください。";
            Repaint();
            return;
        }

        if (!File.Exists(scenePath))
        {
            lastError = $"シーンファイルが見つかりません: {FileOperations.DefaultScenePath}";
            Repaint();
            return;
        }

        try
        {
            var content = FileOperations.ReadSceneNormalized(scenePath);
            var current = SceneParser.Parse(content);
            currentEntryCount = current.Count;

            var pinned = FileOperations.LoadPinned(pinnedPath);
            if (pinned == null)
            {
                localEntryCount = 0;
                reservedEntryCount = 0;
                lastResult = null;
                lastError = "Pinnedファイルが存在しません。「Import」ボタンで外部JSONを取り込んでください。";
                Repaint();
                return;
            }

            localEntryCount = pinned.OfType<LocalEntry>().Count();
            reservedEntryCount = pinned.OfType<ReservedEntry>().Count();

            // セーブデータ影響検知: エラー時は null
            Dictionary<string, List<PersistenceInfo>>? persistenceMap = null;
            try
            {
                persistenceMap = FileOperations.BuildPersistenceMap(content, projectRoot);
            }
            catch (Exception ex2)
            {
                Debug.LogWarning($"[NetworkID Validator] セーブデータ影響検知中にエラー: {ex2.Message}");
            }

            lastResult = Validator.Validate(current, pinned, persistenceMap);

            if (!lastResult.IsValid)
                Debug.LogWarning($"[NetworkID Validator] 危険な変更が検出されました:\n{lastResult.GetDetailedMessage()}");
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            Debug.LogError($"[NetworkID Validator] 検証中にエラーが発生しました: {ex.Message}");
        }

        Repaint();
    }

    /// <summary>
    /// Pinned ファイルのローカルエントリをシーンの NetworkIDs に書き戻す。
    /// </summary>
    private void RestoreFromPinned()
    {
        var projectRoot = GetProjectRoot();
        var scenePath = Path.Combine(projectRoot, FileOperations.DefaultScenePath);
        var pinnedPath = Path.Combine(projectRoot, FileOperations.DefaultPinnedPath);

        var pinned = FileOperations.LoadPinned(pinnedPath);
        if (pinned == null)
        {
            Debug.LogError("[NetworkID Validator] Pinnedファイルが存在しません。");
            return;
        }

        if (!EditorUtility.DisplayDialog(
            "リストア確認",
            "シーンの NetworkIDs を Pinned の状態に復元しますか？\n\nこの操作は元に戻せません。",
            "リストア", "キャンセル"))
            return;

        try
        {
            FileOperations.RestoreScene(scenePath, pinned);
            AssetDatabase.Refresh();
            Debug.Log("[NetworkID Validator] NetworkIDs をリストアしました。");
            RunValidation();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetworkID Validator] リストアに失敗しました: {ex.Message}");
        }
    }

    private static string GetProjectRoot() => EditorPathUtil.GetProjectRoot();
}

} // namespace VRCNetworkIDGuard
#endif
