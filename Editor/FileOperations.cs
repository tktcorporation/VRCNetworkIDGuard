#nullable enable
// ファイル I/O を集約するグルー層
//
// SceneParser, PinnedFile, Validator は純粋関数のみで構成されており、
// このクラスがファイルシステムとの接点を一手に担う。
//
// 責務:
//   - シーンファイルの読み込みと CRLF→LF 正規化
//   - Pinned ファイルの読み書き（BOM 対応込み）
//   - 先方 JSON の読み込み
//   - シーン復元（バックアップ付き書き込み）

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VRCNetworkIDGuard
{
    /// <summary>
    /// ファイル I/O を集約する薄いグルー層。
    /// 各メソッドはファイル読み込み + BOM/CRLF 処理 + 純粋関数呼び出しの組み合わせ。
    /// </summary>
    public static class FileOperations
    {
        // --- プロジェクト内のデフォルトパス ---
        // ユーザーがプロジェクトに合わせて設定するパス。
        // デフォルトは空文字列。NetworkIDGuardSettings で上書きされる。
        public static string DefaultScenePath { get; set; } = "";
        public static string DefaultPinnedPath { get; set; } = "networkids_pinned.json";

        /// <summary>
        /// シーンファイルを読み込み、改行コードを LF に正規化して返す。
        ///
        /// Windows のシーンファイルは git やエディタの設定により CRLF(\r\n)で保存される場合がある。
        /// SceneParser の正規表現は LF(\n) 前提で書かれているため、
        /// シーンファイル読み込みはすべてこのメソッドを経由して CRLF 正規化を保証する。
        /// </summary>
        public static string ReadSceneNormalized(string sceneFilePath)
        {
            WarnIfStaleBackupExists(sceneFilePath);
            var content = File.ReadAllText(sceneFilePath, Encoding.UTF8);
            return content.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// Pinned ファイルを読み込んでパース。ファイルが存在しない場合は null を返す。
        /// BOM 付き UTF-8 にも対応する。
        /// </summary>
        public static List<PinnedEntryBase>? LoadPinned(string pinnedPath)
        {
            if (!File.Exists(pinnedPath))
                return null;

            var json = File.ReadAllText(pinnedPath, Encoding.UTF8);
            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json.Substring(1);

            return PinnedFile.Parse(json);
        }

        /// <summary>
        /// Pinned ファイルを保存。LF 固定、UTF-8 BOM なし。
        /// ディレクトリが存在しない場合は自動作成する。
        /// </summary>
        public static void SavePinned(string pinnedPath, List<PinnedEntryBase> entries)
        {
            var content = PinnedFile.Serialize(entries);

            var directory = Path.GetDirectoryName(pinnedPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(pinnedPath, content, new UTF8Encoding(false));
        }

        /// <summary>
        /// 先方の partner JSON を読み込んでパース。ファイルが存在しない場合は null を返す。
        /// BOM 付き UTF-8 にも対応する。
        /// </summary>
        public static Dictionary<int, string>? LoadPartnerJson(string path)
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json.Substring(1);

            return PinnedFile.ParsePartnerJson(json);
        }

        /// <summary>
        /// 前回のクラッシュで残ったバックアップファイルが存在するかチェックし、
        /// 存在する場合はコンソールに警告を出力する。
        /// </summary>
        private static void WarnIfStaleBackupExists(string sceneFilePath)
        {
            var backupPath = sceneFilePath + ".networkid-backup";
            if (File.Exists(backupPath))
            {
                Console.Error.WriteLine(
                    $"WARNING: 前回の書き込みで残ったバックアップファイルが存在します: {backupPath}");
                Console.Error.WriteLine(
                    "前回の処理が中断された可能性があります。バックアップの内容を確認してください。");
            }
        }

        /// <summary>
        /// バックアップ付きでシーンファイルを書き換える共通処理。
        ///
        /// 書き込み失敗時はバックアップから復元して例外を再送出する。
        /// バックアップは書き込み成功後にのみ削除する。
        /// </summary>
        private static void WriteSceneWithBackup(string sceneFilePath, string newContent)
        {
            var backupPath = sceneFilePath + ".networkid-backup";
            File.Copy(sceneFilePath, backupPath, overwrite: true);
            try
            {
                File.WriteAllText(sceneFilePath, newContent, Encoding.UTF8);
            }
            catch
            {
                File.Copy(backupPath, sceneFilePath, overwrite: true);
                throw;
            }
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }

        /// <summary>
        /// 予約IDと衝突しているシーンエントリに新IDを採番してシーンを書き換える。
        /// </summary>
        public static Dictionary<int, int> ReassignScene(
            string sceneFilePath, List<PinnedEntryBase> pinnedEntries)
        {
            var content = ReadSceneNormalized(sceneFilePath);
            var sceneEntries = SceneParser.Parse(content);

            var reassignResult = Validator.ReassignReservedConflicts(sceneEntries, pinnedEntries);

            if (reassignResult.Reassignments.Count == 0)
                return reassignResult.Reassignments;

            var newSection = SceneParser.BuildSection(reassignResult.ReassignedSceneEntries);
            var newContent = SceneParser.ReplaceSection(content, newSection);

            WriteSceneWithBackup(sceneFilePath, newContent);

            return reassignResult.Reassignments;
        }

        /// <summary>
        /// Pinned のローカルエントリからシーンの NetworkIDs セクションを復元する。
        /// </summary>
        public static void RestoreScene(string sceneFilePath, List<PinnedEntryBase> pinnedEntries)
        {
            var localEntries = pinnedEntries
                .OfType<LocalEntry>()
                .Select(e => new NetworkIDEntry(e.ID, e.GameObjectFileID, e.SerializedTypeNames))
                .ToList();

            var content = ReadSceneNormalized(sceneFilePath);
            var newSection = SceneParser.BuildSection(localEntries);
            var newContent = SceneParser.ReplaceSection(content, newSection);

            WriteSceneWithBackup(sceneFilePath, newContent);
        }

        /// <summary>
        /// 指定ディレクトリ以下の "* Udon C# Program Asset.asset.meta" を走査して
        /// GUID → アセットパスのマッピングを構築する。
        /// </summary>
        public static Dictionary<string, string> BuildGuidToPathMap(string assetsRoot)
        {
            var metaFiles = new List<(string path, string content)>();
            foreach (var metaPath in Directory.GetFiles(assetsRoot, "* Udon C# Program Asset.asset.meta", SearchOption.AllDirectories))
            {
                var relativePath = metaPath.Replace("\\", "/");
                var content = File.ReadAllText(metaPath);
                metaFiles.Add((relativePath, content));
            }
            return PersistenceDetector.BuildGuidToPathMap(metaFiles);
        }

        /// <summary>
        /// GUID リストに対応する .asset ファイルのコンテンツを読み込む。
        /// </summary>
        public static Dictionary<string, string> LoadAssetContents(
            Dictionary<string, string> guidToPath, IEnumerable<string> guids)
        {
            var result = new Dictionary<string, string>();
            foreach (var guid in guids)
            {
                if (!guidToPath.TryGetValue(guid, out var path)) continue;
                if (!File.Exists(path)) continue;
                if (!result.ContainsKey(path))
                    result[path] = File.ReadAllText(path);
            }
            return result;
        }

        /// <summary>
        /// 指定ディレクトリ以下の .meta ファイルから、指定された GUID セットのパスを解決する。
        /// </summary>
        public static Dictionary<string, string> ResolveGuids(string assetsRoot, HashSet<string> targetGuids)
        {
            var result = new Dictionary<string, string>();
            if (targetGuids.Count == 0) return result;

            foreach (var metaPath in Directory.GetFiles(assetsRoot, "*.meta", SearchOption.AllDirectories))
            {
                if (result.Count >= targetGuids.Count) break;

                var content = File.ReadAllText(metaPath);
                var match = Regex.Match(content, @"guid:\s*([0-9a-f]{32})");
                if (!match.Success) continue;

                var guid = match.Groups[1].Value;
                if (targetGuids.Contains(guid))
                {
                    var assetPath = metaPath.EndsWith(".meta") ? metaPath.Substring(0, metaPath.Length - 5) : metaPath;
                    result[guid] = assetPath.Replace("\\", "/");
                }
            }

            return result;
        }

        /// <summary>
        /// シーンコンテンツとプロジェクトルートから PersistenceDetector の影響マップを構築する。
        /// </summary>
        public static Dictionary<string, List<PersistenceInfo>>? BuildPersistenceMap(
            string sceneContent, string projectRoot)
        {
            var assetsRoot = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assetsRoot)) return null;

            var guidToPath = BuildGuidToPathMap(assetsRoot);
            var goToGuids = PersistenceDetector.ExtractGameObjectToGuids(sceneContent);
            var strippedObjects = PersistenceDetector.ExtractStrippedObjects(sceneContent);

            var prefabGuids = new HashSet<string>(
                strippedObjects.Values.Select(s => s.PrefabGuid));
            var allGuidPaths = new Dictionary<string, string>(guidToPath);
            if (prefabGuids.Count > 0)
            {
                var prefabPaths = ResolveGuids(assetsRoot, prefabGuids);
                foreach (var kvp in prefabPaths)
                    allGuidPaths[kvp.Key] = kvp.Value;
            }

            var allGuids = goToGuids.Values.SelectMany(g => g).Distinct();
            var assetContents = LoadAssetContents(guidToPath, allGuids);
            var prefabContents = LoadAssetContents(allGuidPaths, prefabGuids);

            return PersistenceDetector.Detect(sceneContent, allGuidPaths, assetContents, prefabContents);
        }
    }
}
