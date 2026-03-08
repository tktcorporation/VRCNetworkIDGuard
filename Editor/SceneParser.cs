#nullable enable
// シーン YAML のパースと再構築を担当する純粋関数モジュール
//
// NetworkIDValidatorCore.cs からシーンパース関連のロジックを抽出したもの。
// I/O を一切持たず、文字列の入出力のみで動作するため、テストが容易。
//
// 主な公開 API:
//   Parse(content)         — シーン YAML 文字列から NetworkIDEntry のリストを抽出
//   BuildSection(entries)  — NetworkIDEntry のリストからシーン YAML のセクション文字列を生成
//   ReplaceSection(content, newSection) — シーン YAML 内の NetworkIDs セクションを差し替え

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VRCNetworkIDGuard
{
    /// <summary>
    /// シーン YAML の NetworkIDs セクションをパース・再構築する純粋関数群。
    ///
    /// VRChat のシーンファイル（Unity YAML）には NetworkIDs セクションがあり、
    /// ブロック形式（エントリあり）とインライン空配列形式（NetworkIDs: []）の2種類がある。
    /// このクラスは両方の形式を透過的に扱う。
    /// </summary>
    public static class SceneParser
    {
        // --- シーンファイルパース用正規表現 ---

        // エントリあり・なしのブロック形式: "  NetworkIDs:\n" の後にエントリが続く（または続かない）
        // キャプチャグループ1 = エントリ本体（空文字の場合もある）
        private static readonly Regex NetworkIDsSectionRegex = new Regex(
            @"^  NetworkIDs:\n((?:  -.*\n(?:    .*\n)*)*)",
            RegexOptions.Multiline | RegexOptions.Compiled
        );

        // NetworkIDs: [] のインライン空配列形式: Unityがエントリなし時に出力するフォーマット
        private static readonly Regex NetworkIDsEmptyInlineRegex = new Regex(
            @"^  NetworkIDs: \[\]\n?",
            RegexOptions.Multiline | RegexOptions.Compiled
        );

        // ReplaceSection 用: 両形式どちらでも置換できる（".*" でインライン [] も吸収）
        // キャプチャグループなし。Match.Index と Match.Length で置換範囲を特定する
        private static readonly Regex NetworkIDsReplaceRegex = new Regex(
            @"^  NetworkIDs:.*\n((?:  -.*\n(?:    .*\n)*)*)",
            RegexOptions.Multiline | RegexOptions.Compiled
        );

        private static readonly Regex EntryStartRegex = new Regex(
            @"^\s{2}-\s+gameObject:",
            RegexOptions.Compiled
        );

        // Unity は通常正の fileID を使うが、負の値が出現する可能性も考慮してマイナス記号を許容する。
        private static readonly Regex FileIDRegex = new Regex(
            @"fileID:\s*(-?\d+)",
            RegexOptions.Compiled
        );

        private static readonly Regex IDRegex = new Regex(
            @"^\s{4}ID:\s*(\d+)",
            RegexOptions.Compiled
        );

        // "    SerializedTypeNames:" ヘッダ行。この行以降のリスト項目のみタイプ名としてキャプチャする。
        // TypeNameRegex を SerializedTypeNames セクション内に限定することで、
        // 将来追加されうる同インデントの別リストフィールドとの誤マッチを防ぐ。
        private static readonly Regex SerializedTypeNamesHeaderRegex = new Regex(
            @"^\s{4}SerializedTypeNames:\s*$",
            RegexOptions.Compiled
        );

        private static readonly Regex TypeNameRegex = new Regex(
            @"^\s{4}-\s+(.+)$",
            RegexOptions.Compiled
        );

        /// <summary>
        /// パース中に NetworkIDEntry の各フィールドを段階的に組み立てるためのビルダー。
        ///
        /// NetworkIDEntry は record（不変型）のため、全フィールドが揃うまでインスタンスを
        /// 作れない。YAML を行ごとに読む都合上、gameObject 行で ID はまだ不明であり、
        /// 可変なビルダーで一時的に値を蓄積してから Build() で不変の record に変換する。
        /// </summary>
        private class EntryBuilder
        {
            public string GameObjectFileID = "";
            public int ID = 0;
            public bool IDWasParsed = false;
            public bool InSerializedTypeNames = false;
            public List<string> SerializedTypeNames = new List<string>();

            /// <summary>
            /// ビルダーの内容を検証してから NetworkIDEntry を生成する。
            /// ID 行が見つからなかった不完全なエントリは例外で弾く。
            /// これにより、不正な YAML から ID=0 のゴーストエントリが
            /// 下流に流出するのを防ぐ。
            /// </summary>
            public NetworkIDEntry Build()
            {
                if (!IDWasParsed)
                    throw new InvalidOperationException(
                        $"NetworkIDs entry for gameObject fileID={GameObjectFileID} has no ID field");
                return new NetworkIDEntry(
                    ID, GameObjectFileID, SerializedTypeNames.AsReadOnly());
            }
        }

        /// <summary>
        /// シーン YAML 文字列から NetworkIDEntry のリストを抽出する。
        ///
        /// ブロック形式を先にチェックする。空配列チェックを先に行うと、
        /// シーンファイル内の別コンポーネントに "  NetworkIDs: []" があった場合に
        /// 本来のエントリ付きセクションが無視されてしまうため。
        /// </summary>
        /// <exception cref="InvalidOperationException">NetworkIDs セクションが見つからない場合</exception>
        public static List<NetworkIDEntry> Parse(string content)
        {
            // ブロック形式を先にチェックする
            var match = NetworkIDsSectionRegex.Match(content);
            if (match.Success)
            {
                var networkIDsSection = match.Groups[1].Value;

                // ブロック形式だがエントリなし（"NetworkIDs:\n" の直後が別セクション）
                if (string.IsNullOrWhiteSpace(networkIDsSection))
                {
                    return new List<NetworkIDEntry>();
                }

                return ParseNetworkIDsSection(networkIDsSection);
            }

            // ブロック形式が見つからない → インライン空配列形式を探す
            if (NetworkIDsEmptyInlineRegex.IsMatch(content))
            {
                return new List<NetworkIDEntry>();
            }

            throw new InvalidOperationException("NetworkIDs section not found in scene file");
        }

        /// <summary>
        /// NetworkIDs セクションの本体（エントリ部分）をパースしてエントリリストを返す。
        ///
        /// パース戦略:
        /// - "  - gameObject:" 行で新エントリのビルダーを開始
        /// - "    ID:" 行で ID を取得（IDWasParsed フラグで取得済みを記録）
        /// - "    SerializedTypeNames:" 行でタイプ名リスト読み取りモードに入る
        /// - "    - ..." 行はタイプ名モード中のみキャプチャ（#1: 別フィールドの誤マッチ防止）
        /// - ID 行がないエントリは Build() で例外（#2: ID=0 ゴーストエントリの流出防止）
        /// </summary>
        private static List<NetworkIDEntry> ParseNetworkIDsSection(string networkIDsSection)
        {
            var entries = new List<NetworkIDEntry>();
            EntryBuilder? currentBuilder = null;

            foreach (var line in networkIDsSection.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (EntryStartRegex.IsMatch(line))
                {
                    if (currentBuilder != null) entries.Add(currentBuilder.Build());

                    var fileIDMatch = FileIDRegex.Match(line);
                    currentBuilder = new EntryBuilder
                    {
                        GameObjectFileID = fileIDMatch.Success ? fileIDMatch.Groups[1].Value : "",
                        ID = 0,
                        IDWasParsed = false,
                        InSerializedTypeNames = false,
                        SerializedTypeNames = new List<string>()
                    };
                }
                else if (currentBuilder != null)
                {
                    var idMatch = IDRegex.Match(line);
                    if (idMatch.Success)
                    {
                        if (!int.TryParse(
                            idMatch.Groups[1].Value,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsedID))
                        {
                            // int 範囲外の値は不正な YAML として無視（IDWasParsed が false のまま）
                            continue;
                        }
                        currentBuilder.ID = parsedID;
                        currentBuilder.IDWasParsed = true;
                        currentBuilder.InSerializedTypeNames = false;
                        continue;
                    }

                    // "    SerializedTypeNames:" ヘッダ行を検出してモード切替
                    if (SerializedTypeNamesHeaderRegex.IsMatch(line))
                    {
                        currentBuilder.InSerializedTypeNames = true;
                        continue;
                    }

                    // タイプ名は SerializedTypeNames セクション内でのみキャプチャする。
                    // これにより、将来 YAML に同じインデントの別リストフィールドが追加されても
                    // 誤ってタイプ名として取り込むことを防ぐ。
                    if (currentBuilder.InSerializedTypeNames)
                    {
                        var typeNameMatch = TypeNameRegex.Match(line);
                        if (typeNameMatch.Success)
                        {
                            currentBuilder.SerializedTypeNames.Add(typeNameMatch.Groups[1].Value);
                        }
                        else
                        {
                            // リスト項目でない行が来たら SerializedTypeNames セクション終了
                            currentBuilder.InSerializedTypeNames = false;
                        }
                    }
                }
            }

            if (currentBuilder != null) entries.Add(currentBuilder.Build());
            return entries;
        }

        /// <summary>
        /// NetworkIDEntry のリストからシーン YAML の NetworkIDs セクション文字列を生成する。
        /// エントリは ID の昇順でソートされる。
        /// </summary>
        public static string BuildSection(List<NetworkIDEntry> entries)
        {
            // LF固定で出力する。シーン YAML は LF 前提で処理するため、
            // ここで CRLF を出力すると改行コードが混在してしまう。
            var sb = new StringBuilder();
            sb.Append("  NetworkIDs:\n");

            foreach (var entry in entries.OrderBy(e => e.ID))
            {
                sb.Append($"  - gameObject: {{fileID: {entry.GameObjectFileID}}}\n");
                sb.Append($"    ID: {entry.ID}\n");
                sb.Append("    SerializedTypeNames:\n");
                foreach (var typeName in entry.SerializedTypeNames)
                {
                    sb.Append($"    - {typeName}\n");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// シーン YAML 内の NetworkIDs セクションを新しいセクション文字列で差し替える。
        ///
        /// ブロック形式（エントリあり）とインライン空配列形式（NetworkIDs: []）の
        /// どちらの形式でもマッチして差し替えられる。
        ///
        /// Parse と同様にブロック形式を優先する。別コンポーネントの "NetworkIDs: []" が
        /// 先に出現するシーンでも、エントリ付きのブロックセクションを正しく差し替える。
        ///
        /// マッチ戦略（Parse と一貫）:
        ///   1. 全マッチを走査し、エントリ付き（グループ1が非空）のブロックを優先的に採用
        ///   2. エントリ付きブロックがなければ、最初にマッチした空セクションをフォールバックとして使用
        ///   3. VRChat シーンでは VRC_SceneDescriptor に1つだけ NetworkIDs ブロックがある前提
        /// </summary>
        /// <exception cref="InvalidOperationException">NetworkIDs セクションが見つからない場合</exception>
        public static string ReplaceSection(string sceneContent, string newSection)
        {
            // Parse と同じ戦略: ブロック形式（エントリ付き）を先に探す。
            // 別コンポーネントの "NetworkIDs: []" が先にマッチするのを防ぐため、
            // エントリを含むマッチを優先的に採用する。
            Match? bestMatch = null;
            var match = NetworkIDsReplaceRegex.Match(sceneContent);
            while (match.Success)
            {
                if (bestMatch == null)
                    bestMatch = match;

                // エントリ付きブロック（グループ1が空でない）を見つけたら即採用
                if (match.Groups[1].Length > 0)
                {
                    bestMatch = match;
                    break;
                }
                match = match.NextMatch();
            }

            if (bestMatch == null)
                throw new InvalidOperationException("NetworkIDs section not found in scene file");

            return sceneContent.Substring(0, bestMatch.Index) +
                   newSection +
                   sceneContent.Substring(bestMatch.Index + bestMatch.Length);
        }
    }
}
