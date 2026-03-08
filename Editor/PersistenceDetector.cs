#nullable enable
// セーブデータ影響検知の純粋関数モジュール
//
// NetworkID 変更時に、その GameObject の UdonBehaviour が [UdonSynced] フィールドを
// 持つかを検知する。シーン YAML → MonoBehaviour → programSource GUID → .asset ファイル
// → UdonSyncedAttribute の経路をファイルベースで辿る。
//
// I/O を一切持たず、文字列の入出力のみで動作するため、テストが容易。
// ファイル読み込みは呼び出し側の責任とし、このクラスは string → データ変換のみ行う。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VRCNetworkIDGuard
{
    /// <summary>
    /// stripped オブジェクト（プレハブインスタンス由来）のソース情報。
    ///
    /// stripped オブジェクトはシーン YAML 上で "--- !u!1 &amp;XXXX stripped" として記録され、
    /// 実体はプレハブファイル側に存在する。CorrespondingSourceObject の fileID と
    /// プレハブの GUID を辿ることで、プレハブ内の MonoBehaviour を解決できる。
    /// </summary>
    public sealed record StrippedObjectInfo(string PrefabGuid, string SourceObjectFileID);

    /// <summary>
    /// セーブデータ影響検知の純粋関数群。
    ///
    /// NetworkID が変更されたとき、対象の GameObject に UdonSynced フィールドを持つ
    /// UdonBehaviour がアタッチされていれば、プレイヤーのセーブデータが消失するリスクがある。
    /// このクラスはシーン YAML と .asset ファイルの文字列解析のみで検知を行い、
    /// Unity API や I/O に依存しない。
    /// </summary>
    public static class PersistenceDetector
    {
        private static readonly Regex MonoBehaviourBlockRegex = new Regex(
            @"--- !u!114 &\d+\r?\n((?:(?!--- !u!).*\r?\n)*)",
            RegexOptions.Compiled
        );

        private static readonly Regex GameObjectFileIDRegex = new Regex(
            @"m_GameObject:\s*\{fileID:\s*(-?\d+)\}",
            RegexOptions.Compiled
        );

        private static readonly Regex ProgramSourceGuidRegex = new Regex(
            @"programSource:\s*\{[^}]*guid:\s*([0-9a-f]{32})",
            RegexOptions.Compiled
        );

        private static readonly Regex StrippedObjectBlockRegex = new Regex(
            @"--- !u!1 &(-?\d+) stripped\r?\n((?:(?!--- !u!).*\r?\n)*)",
            RegexOptions.Compiled
        );

        private static readonly Regex CorrespondingSourceRegex = new Regex(
            @"m_CorrespondingSourceObject:\s*\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-f]{32})",
            RegexOptions.Compiled
        );

        private static readonly Regex MetaGuidRegex = new Regex(
            @"guid:\s*([0-9a-f]{32})",
            RegexOptions.Compiled
        );

        private static readonly Regex UdonSyncedRegex = new Regex(
            @"UdonSharp\.UdonSyncedAttribute,\s*UdonSharp\.Runtime",
            RegexOptions.Compiled
        );

        /// <summary>
        /// シーン YAML から MonoBehaviour ブロックをパースし、
        /// GameObject fileID → programSource GUID リストのマッピングを返す。
        /// </summary>
        public static Dictionary<string, List<string>> ExtractGameObjectToGuids(string sceneContent)
        {
            var result = new Dictionary<string, List<string>>();

            foreach (Match blockMatch in MonoBehaviourBlockRegex.Matches(sceneContent))
            {
                var block = blockMatch.Value;

                var guidMatch = ProgramSourceGuidRegex.Match(block);
                if (!guidMatch.Success) continue;

                var goMatch = GameObjectFileIDRegex.Match(block);
                if (!goMatch.Success) continue;

                var gameObjectFileID = goMatch.Groups[1].Value;
                var guid = guidMatch.Groups[1].Value;

                if (!result.TryGetValue(gameObjectFileID, out var guids))
                {
                    guids = new List<string>();
                    result[gameObjectFileID] = guids;
                }
                guids.Add(guid);
            }

            return result;
        }

        /// <summary>
        /// シーン YAML から stripped オブジェクト（プレハブインスタンス由来の GameObject）を抽出する。
        /// </summary>
        public static Dictionary<string, StrippedObjectInfo> ExtractStrippedObjects(string sceneContent)
        {
            var result = new Dictionary<string, StrippedObjectInfo>();

            foreach (Match blockMatch in StrippedObjectBlockRegex.Matches(sceneContent))
            {
                var fileID = blockMatch.Groups[1].Value;
                var block = blockMatch.Value;

                var sourceMatch = CorrespondingSourceRegex.Match(block);
                if (!sourceMatch.Success) continue;

                var sourceFileID = sourceMatch.Groups[1].Value;
                var prefabGuid = sourceMatch.Groups[2].Value;

                result[fileID] = new StrippedObjectInfo(prefabGuid, sourceFileID);
            }

            return result;
        }

        /// <summary>
        /// .meta ファイルの (パス, コンテンツ) リストから GUID → アセットパスのマッピングを構築する。
        /// </summary>
        public static Dictionary<string, string> BuildGuidToPathMap(List<(string path, string content)> metaFiles)
        {
            var result = new Dictionary<string, string>();

            foreach (var (path, content) in metaFiles)
            {
                var guidMatch = MetaGuidRegex.Match(content);
                if (!guidMatch.Success) continue;

                var assetPath = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    ? path.Substring(0, path.Length - 5)
                    : path;

                result[guidMatch.Groups[1].Value] = assetPath;
            }

            return result;
        }

        /// <summary>
        /// .asset ファイルの内容から UdonSyncedAttribute の有無と個数を検出する。
        /// </summary>
        public static PersistenceInfo DetectUdonSynced(string assetPath, string assetContent)
        {
            var count = UdonSyncedRegex.Matches(assetContent).Count;

            var fileName = Path.GetFileName(assetPath);
            var spaceIndex = fileName.IndexOf(' ');
            var componentName = spaceIndex > 0 ? fileName.Substring(0, spaceIndex) : fileName;

            return new PersistenceInfo(
                HasSyncedFields: count > 0,
                ComponentName: componentName,
                SyncedFieldCount: count
            );
        }

        /// <summary>
        /// シーン YAML、GUID→パスマップ、.asset コンテンツ、プレハブコンテンツを統合し、
        /// GameObject fileID → PersistenceInfo リストのマッピングを返す。
        /// </summary>
        public static Dictionary<string, List<PersistenceInfo>> Detect(
            string sceneContent,
            Dictionary<string, string> guidToPath,
            Dictionary<string, string> assetContents,
            Dictionary<string, string> prefabContents)
        {
            var result = new Dictionary<string, List<PersistenceInfo>>();

            var directMapping = ExtractGameObjectToGuids(sceneContent);

            foreach (var (gameObjectFileID, guids) in directMapping)
            {
                var infos = ResolveGuids(guids, guidToPath, assetContents);
                if (infos.Count > 0)
                {
                    result[gameObjectFileID] = infos;
                }
            }

            var strippedObjects = ExtractStrippedObjects(sceneContent);

            foreach (var (sceneFileID, stripped) in strippedObjects)
            {
                if (!guidToPath.TryGetValue(stripped.PrefabGuid, out var prefabPath)) continue;
                if (!prefabContents.TryGetValue(prefabPath, out var prefabContent)) continue;

                var prefabMapping = ExtractGameObjectToGuids(prefabContent);

                if (!prefabMapping.TryGetValue(stripped.SourceObjectFileID, out var prefabGuids)) continue;

                var infos = ResolveGuids(prefabGuids, guidToPath, assetContents);
                if (infos.Count > 0)
                {
                    result[sceneFileID] = infos;
                }
            }

            return result;
        }

        private static List<PersistenceInfo> ResolveGuids(
            List<string> guids,
            Dictionary<string, string> guidToPath,
            Dictionary<string, string> assetContents)
        {
            var infos = new List<PersistenceInfo>();

            foreach (var guid in guids)
            {
                if (!guidToPath.TryGetValue(guid, out var assetPath)) continue;
                if (!assetContents.TryGetValue(assetPath, out var assetContent)) continue;

                infos.Add(DetectUdonSynced(assetPath, assetContent));
            }

            return infos;
        }
    }
}
