#nullable enable
// Pinned JSON ファイルのパース・シリアライズを担当する純粋関数モジュール
//
// NetworkIDValidatorCore.cs から JSON 入出力ロジックを分離したもの。
// ファイル I/O は呼び出し側の責任とし、このクラスは string → データ / データ → string の変換のみ行う。
// これにより、テストでファイルシステムのモックが不要になる。
//
// 対応するフォーマット:
//   networkids_pinned.json — LocalEntry（gameObject付き）と ReservedEntry（パスのみ）の混在配列
//   先方 partner JSON — {"10": "/Cooker", ...} 形式の ID→パスマッピング
//
// 技術的制約: System.Text.Json は Unity で使用できないため、正規表現による手動パースを行う。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VRCNetworkIDGuard
{
    /// <summary>
    /// networkids_pinned.json および先方 partner JSON のパース・シリアライズ。
    /// ファイル I/O を含まない純粋関数のみで構成し、テスタビリティを確保する。
    /// </summary>
    public static class PinnedFile
    {
        // --- Pinned JSON パース用正規表現 ---
        // インスタンス生成コストを避けるため static readonly で保持。

        // 技術的制約: このパターンは { } のネスト深度1までしか扱えない。
        // 現状は SerializedTypeNames が [] (角括弧) で囲まれているため問題ないが、
        // 将来 JSON に中括弧を含むフィールド（例: ネストされたオブジェクト）や
        // パスに { } を含むエントリが追加された場合、エントリ境界を誤認する。
        // System.Text.Json が Unity で使用可能になった時点でこの正規表現パーサーを置き換えること。
        private static readonly Regex PinnedEntryPattern = new Regex(
            @"\{[^{}]+\}", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex PinnedIdPattern = new Regex(
            @"""ID""\s*:\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex PinnedPathPattern = new Regex(
            @"""path""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        private static readonly Regex PinnedGameObjectPattern = new Regex(
            @"""gameObject""\s*:\s*""(\d+)""", RegexOptions.Compiled);
        private static readonly Regex PinnedTypeNamesBlockPattern = new Regex(
            @"""SerializedTypeNames""\s*:\s*\[(.*?)\]", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex PinnedTypeNamePattern = new Regex(
            @"""([^""]+)""", RegexOptions.Compiled);

        // ParsePartnerJson 用: {"10": "/Cooker", "11": "/Scanner"} 形式のキー→値パース。
        private static readonly Regex PartnerJsonPattern = new Regex(
            @"""(\d+)""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        /// <summary>
        /// networkids_pinned.json の JSON 文字列をパースして PinnedEntryBase のリストを返す。
        ///
        /// gameObject フィールドの有無で LocalEntry / ReservedEntry を判定する:
        ///   gameObject あり → LocalEntry（ローカル解決済み、シーン復元に使用可能）
        ///   gameObject なし → ReservedEntry（先方のみに存在する予約 ID）
        /// </summary>
        public static List<PinnedEntryBase> Parse(string json)
        {
            var entries = new List<PinnedEntryBase>();

            foreach (Match entryMatch in PinnedEntryPattern.Matches(json))
            {
                var entryJson = entryMatch.Value;

                var idMatch = PinnedIdPattern.Match(entryJson);
                if (!idMatch.Success) continue;

                if (!int.TryParse(idMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;

                var pathMatch = PinnedPathPattern.Match(entryJson);
                var rawPath = pathMatch.Success ? pathMatch.Groups[1].Value : "";
                var path = rawPath.Replace("\\\"", "\"").Replace("\\\\", "\\");

                var gameObjectMatch = PinnedGameObjectPattern.Match(entryJson);

                if (gameObjectMatch.Success)
                {
                    var typeNames = new List<string>();
                    var typeNamesBlockMatch = PinnedTypeNamesBlockPattern.Match(entryJson);
                    if (typeNamesBlockMatch.Success)
                    {
                        foreach (Match typeMatch in PinnedTypeNamePattern.Matches(typeNamesBlockMatch.Groups[1].Value))
                        {
                            typeNames.Add(typeMatch.Groups[1].Value);
                        }
                    }
                    entries.Add(new LocalEntry(id, path, gameObjectMatch.Groups[1].Value, typeNames.AsReadOnly()));
                }
                else
                {
                    entries.Add(new ReservedEntry(id, path));
                }
            }

            return entries;
        }

        /// <summary>
        /// PinnedEntryBase のリストを networkids_pinned.json 形式の JSON 文字列にシリアライズ。
        ///
        /// ID の昇順でソートし、1エントリ1行で出力する。
        /// 行単位で git diff が追えるようにするための設計。
        /// </summary>
        public static string Serialize(List<PinnedEntryBase> entries)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");

            var sorted = entries.OrderBy(e => e.ID).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var entry = sorted[i];
                var escapedPath = entry.Path.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var comma = i < sorted.Count - 1 ? "," : "";

                switch (entry)
                {
                    case LocalEntry local:
                        var typeNames = string.Join(", ", local.SerializedTypeNames.Select(t => $"\"{t}\""));
                        sb.Append($"  {{\"ID\": {local.ID}, \"path\": \"{escapedPath}\", \"gameObject\": \"{local.GameObjectFileID}\", \"SerializedTypeNames\": [{typeNames}]}}{comma}\n");
                        break;
                    case ReservedEntry:
                        sb.Append($"  {{\"ID\": {entry.ID}, \"path\": \"{escapedPath}\"}}{comma}\n");
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown PinnedEntryBase subtype: {entry.GetType().Name}");
                }
            }

            sb.Append("]\n");
            return sb.ToString();
        }

        /// <summary>
        /// 先方の partner JSON（{"10": "/Cooker", "11": "/Scanner"}）をパースして
        /// ID → パスの辞書を返す。
        /// </summary>
        public static Dictionary<int, string> ParsePartnerJson(string json)
        {
            var result = new Dictionary<int, string>();
            foreach (Match match in PartnerJsonPattern.Matches(json))
            {
                var rawPath = match.Groups[2].Value;
                var path = rawPath.Replace("\\\"", "\"").Replace("\\\\", "\\");
                if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    result[id] = path;
            }
            return result;
        }
    }
}
