// NetworkID Checker CLI
//
// システム全体の背景・データフローは Validator.cs / FileOperations.cs を参照。
//
// このCLIの役割:
//   Unity API に依存しないヘッドレス環境（CI・ターミナル）向けのインターフェース。
//   ビルド前のダイアログ確認（Unity Editor）とは独立して動作する。
//
//   [CI での使用]
//     check-networkids.yml が PR/push 時に `check` を実行。
//     危険な変更を検出したら exit 1 で CI を失敗させ、PR へのコメントで警告する。
//
//   [ローカル開発での使用]
//     Unity Editor のダイアログからビルドをキャンセルした後、
//     ターミナルで restore や show-pinned を使って状態を確認・修正する。
//
// 使用方法:
//   dotnet run -- check                       # Pinnedと比較（CI用）
//   dotnet run -- update                      # シーンからPinnedを更新
//   dotnet run -- restore                     # PinnedからNetworkIDsを復元
//   dotnet run -- reassign                    # 予約ID衝突を再割り当てで解消
//   dotnet run -- show                        # 現在のNetworkIDsを表示
//   dotnet run -- import-pinned <json-path>   # 外部環境JSONをインポート
//   dotnet run -- show-pinned                 # Pinnedの状態を表示
//
// オプション:
//   --scene=<path>   シーンファイルのパスを指定（デフォルト: FileOperations.DefaultScenePath）
//   --pinned=<path>  Pinnedファイルのパスを指定（デフォルト: FileOperations.DefaultPinnedPath）

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VRCNetworkIDGuard;

class Program
{
    /// <summary>
    /// シーンコンテンツとプロジェクトルートから PersistenceDetector の影響マップを構築する。
    ///
    /// エラー時は null を返す（CLI は影響情報なしでも動作する）。
    /// FileOperations.BuildPersistenceMap に委譲し、エラーハンドリングのみ CLI 固有。
    /// </summary>
    private static Dictionary<string, List<PersistenceInfo>>? BuildPersistenceMap(
        string content, string projectRoot)
    {
        try
        {
            return FileOperations.BuildPersistenceMap(content, projectRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: セーブデータ影響検知中にエラーが発生しました: {ex.Message}");
            return null;
        }
    }

    static int Main(string[] args)
    {
        // --scene と --pinned オプションを解析し、FileOperations のデフォルトパスを上書きする。
        // これにより CLI からプロジェクト固有のパスを指定できる。
        var scenePath = args.FirstOrDefault(a => a.StartsWith("--scene="))?.Split('=', 2)[1];
        var pinnedPathArg = args.FirstOrDefault(a => a.StartsWith("--pinned="))?.Split('=', 2)[1];
        // オプション引数をコマンド処理用の配列から除去する
        args = args.Where(a => !a.StartsWith("--scene=") && !a.StartsWith("--pinned=")).ToArray();

        if (scenePath != null) FileOperations.DefaultScenePath = scenePath;
        if (pinnedPathArg != null) FileOperations.DefaultPinnedPath = pinnedPathArg;

        var projectRoot = FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: プロジェクトルートが見つかりません。");
            return 1;
        }

        var resolvedScenePath = Path.Combine(projectRoot, FileOperations.DefaultScenePath);
        var resolvedPinnedPath = Path.Combine(projectRoot, FileOperations.DefaultPinnedPath);

        var command = args.Length > 0 ? args[0] : "check";
        // --yes/-y: 確認プロンプトをスキップ（CI/スクリプトでの非対話実行用）
        var autoConfirm = args.Any(a => a == "--yes" || a == "-y");
        // --force: 危険な変更を含む update を許可する（データ消失リスクを承知の上で実行）
        var force = args.Any(a => a == "--force");

        try
        {
            return command switch
            {
                "check" => RunCheck(resolvedScenePath, resolvedPinnedPath, projectRoot),
                "update" => RunUpdate(resolvedScenePath, resolvedPinnedPath, projectRoot, autoConfirm, force),
                "restore" => RunRestore(resolvedScenePath, resolvedPinnedPath, projectRoot),
                "reassign" => RunReassign(resolvedScenePath, resolvedPinnedPath, projectRoot),
                "show" => RunShow(resolvedScenePath),
                "import-pinned" => RunImportPinned(args, projectRoot, resolvedScenePath, resolvedPinnedPath),
                "show-pinned" => RunShowPinned(resolvedPinnedPath),
                "--help" or "-h" => ShowHelp(),
                _ => ShowUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// プロジェクトルートを検出する。
    ///
    /// .git ディレクトリ、ProjectSettings/ProjectSettings.asset（Unity プロジェクト）、
    /// または package.json（Unity パッケージ）のいずれかが存在するディレクトリをルートとみなす。
    /// </summary>
    private static string? FindProjectRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current, ".git")) ||
                File.Exists(Path.Combine(current, "ProjectSettings", "ProjectSettings.asset")) ||
                File.Exists(Path.Combine(current, "package.json")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        // .git も ProjectSettings も package.json も見つからなかった場合は null を返す。
        // カレントディレクトリをフォールバックにすると、意図しないディレクトリで
        // シーンファイルを探してしまうリスクがある。
        return null;
    }

    /// <summary>
    /// CI チェック: Pinnedファイルとシーンを比較
    /// - ローカルエントリの消失・変更 → エラー
    /// - 予約IDとの衝突 → エラー
    /// - 安全な追加 → OK
    /// </summary>
    private static int RunCheck(string scenePath, string pinnedPath, string projectRoot)
    {
        if (!File.Exists(scenePath))
        {
            Console.Error.WriteLine($"Error: シーンファイルが見つかりません: {scenePath}");
            return 1;
        }

        var pinned = FileOperations.LoadPinned(pinnedPath);
        if (pinned == null)
        {
            Console.Error.WriteLine("Error: Pinnedファイルが存在しません。");
            Console.Error.WriteLine("`dotnet run -- import-pinned <json>` でインポートしてください。");
            return 1;
        }

        var content = FileOperations.ReadSceneNormalized(scenePath);
        var current = SceneParser.Parse(content);
        var persistenceMap = BuildPersistenceMap(content, projectRoot);
        var result = Validator.Validate(current, pinned, persistenceMap);

        var localCount = pinned.OfType<LocalEntry>().Count();
        var reservedCount = pinned.OfType<ReservedEntry>().Count();
        Console.WriteLine($"Pinned: {pinned.Count}件 (ローカル: {localCount}, 予約: {reservedCount}), シーン: {current.Count}件");
        Console.WriteLine();

        if (result.IsValid)
        {
            if (result.HasSafeAdditionsOnly)
            {
                Console.WriteLine("OK: 安全な追加のみ検出されました。");
                Console.WriteLine(result.GetDetailedMessage());
            }
            else if (!result.HasAnyChanges)
            {
                Console.WriteLine("OK: NetworkIDs に変更はありません。");
            }
            return 0;
        }

        Console.WriteLine("NG: 危険な NetworkID の変更が検出されました！");
        Console.WriteLine();
        Console.WriteLine(result.GetDetailedMessage());
        Console.WriteLine();
        Console.WriteLine("対処方法:");
        Console.WriteLine("  意図しない変更:   dotnet run -- restore");
        Console.WriteLine("  予約ID衝突の解消: dotnet run -- reassign");
        Console.WriteLine("  意図的な変更:     dotnet run -- update");
        return 1;
    }

    /// <summary>
    /// シーンの現在状態でPinnedファイルのローカルエントリを更新する。
    ///
    /// 安全性ポリシー:
    ///   - 新規IDの追加: 常に許可（安全な変更）
    ///   - 既存IDの削除・fileID変更・予約衝突: --force なしではエラー（データ消失リスク）
    /// </summary>
    private static int RunUpdate(string scenePath, string pinnedPath, string projectRoot, bool autoConfirm = false, bool force = false)
    {
        if (!File.Exists(scenePath))
        {
            Console.Error.WriteLine($"Error: シーンファイルが見つかりません: {scenePath}");
            return 1;
        }

        var content = FileOperations.ReadSceneNormalized(scenePath);
        var current = SceneParser.Parse(content);
        var existing = FileOperations.LoadPinned(pinnedPath);

        if (existing != null)
        {
            var persistenceMap = BuildPersistenceMap(content, projectRoot);
            var checkResult = Validator.Validate(current, existing, persistenceMap);

            if (!checkResult.HasAnyChanges)
            {
                Console.WriteLine("Pinnedファイルに変更はありません。");
                return 0;
            }

            // 危険な変更がある場合: --force なしでは拒否
            if (!checkResult.IsValid)
            {
                if (!force)
                {
                    Console.Error.WriteLine("ERROR: 危険な変更が含まれているため、updateを拒否しました。");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(checkResult.GetDetailedMessage());
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("この状態で update すると、意図しない NetworkID のペア変更が Pinned に保存され、");
                    Console.Error.WriteLine("アップロード時にプレイヤーのセーブデータが消失する危険性があります。");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("対処方法:");
                    Console.Error.WriteLine("  意図しない変更の場合:   dotnet run -- restore");
                    Console.Error.WriteLine("  予約ID衝突の場合:       dotnet run -- reassign");
                    Console.Error.WriteLine("  本当に更新する場合:     dotnet run -- update --force");
                    return 1;
                }

                // --force 指定あり: 強い警告を出して最終確認
                Console.WriteLine("========================================");
                Console.WriteLine("  WARNING: データ消失リスクのある操作");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("以下の危険な変更を Pinned に保存しようとしています:");
                Console.WriteLine(checkResult.GetDetailedMessage());
                Console.WriteLine();
                Console.WriteLine("この変更を保存すると:");
                Console.WriteLine("  - 次回アップロード時にプレイヤーのセーブデータが消失する可能性があります");
                Console.WriteLine("  - restore で復元する際の「正」の状態が書き換わります");
                Console.WriteLine();

                if (!autoConfirm)
                {
                    Console.Write("本当に更新しますか？ \"yes\" と入力してください: ");
                    var input = Console.ReadLine()?.Trim();
                    if (input != "yes")
                    {
                        Console.WriteLine("更新をキャンセルしました。");
                        return 1;
                    }
                }
            }
            else
            {
                // 安全な追加のみ
                Console.WriteLine("変更内容:");
                Console.WriteLine(checkResult.GetDetailedMessage());
                Console.WriteLine();
            }
        }

        var updateResult = Validator.UpdatePinnedFromScene(current, existing);

        if (updateResult.NewIDsWithoutPath.Count > 0)
        {
            Console.WriteLine($"NOTE: 以下の新規IDはパス情報がありません（path=\"(unknown)\"）:");
            Console.WriteLine($"      {string.Join(", ", updateResult.NewIDsWithoutPath)}");
            Console.WriteLine("      パスを設定するには外部環境のJSONを取得後 import-pinned コマンドを使用してください。");
            Console.WriteLine();
        }

        FileOperations.SavePinned(pinnedPath, updateResult.Entries);

        var localCount = updateResult.Entries.OfType<LocalEntry>().Count();
        var reservedCount = updateResult.Entries.OfType<ReservedEntry>().Count();
        Console.WriteLine($"OK: Pinnedファイルを更新しました: {pinnedPath}");
        Console.WriteLine($"   ローカル: {localCount}件, 予約: {reservedCount}件");
        return 0;
    }

    /// <summary>
    /// PinnedファイルからシーンのNetworkIDsを復元
    /// </summary>
    private static int RunRestore(string scenePath, string pinnedPath, string projectRoot)
    {
        if (!File.Exists(scenePath))
        {
            Console.Error.WriteLine($"Error: シーンファイルが見つかりません: {scenePath}");
            return 1;
        }

        var pinned = FileOperations.LoadPinned(pinnedPath);
        if (pinned == null)
        {
            Console.Error.WriteLine("Error: Pinnedファイルが存在しません。");
            return 1;
        }

        var content = FileOperations.ReadSceneNormalized(scenePath);
        var current = SceneParser.Parse(content);
        var persistenceMap = BuildPersistenceMap(content, projectRoot);
        var result = Validator.Validate(current, pinned, persistenceMap);

        if (!result.HasAnyChanges)
        {
            Console.WriteLine("OK: NetworkIDs に変更はありません。復元は不要です。");
            return 0;
        }

        Console.WriteLine("復元する変更内容:");
        Console.WriteLine(result.GetDetailedMessage());
        Console.WriteLine();

        FileOperations.RestoreScene(scenePath, pinned);

        var localCount = pinned.OfType<LocalEntry>().Count();
        Console.WriteLine($"OK: NetworkIDsをPinnedから復元しました。（ローカルエントリ: {localCount}件）");
        return 0;
    }

    /// <summary>
    /// 予約IDと衝突しているシーンエントリに新しいIDを採番して衝突を解消する。
    /// </summary>
    private static int RunReassign(string scenePath, string pinnedPath, string projectRoot)
    {
        if (!File.Exists(scenePath))
        {
            Console.Error.WriteLine($"Error: シーンファイルが見つかりません: {scenePath}");
            return 1;
        }

        var pinned = FileOperations.LoadPinned(pinnedPath);
        if (pinned == null)
        {
            Console.Error.WriteLine("Error: Pinnedファイルが存在しません。");
            return 1;
        }

        var content = FileOperations.ReadSceneNormalized(scenePath);
        var sceneEntries = SceneParser.Parse(content);
        var persistenceMap = BuildPersistenceMap(content, projectRoot);
        var result = Validator.Validate(sceneEntries, pinned, persistenceMap);

        var conflicts = result.Changes.Where(c => c.Kind == ChangeKind.ReservedConflict).ToList();
        if (conflicts.Count == 0)
        {
            Console.WriteLine("OK: 予約IDとの衝突はありません。再割り当ては不要です。");
            return 0;
        }

        Console.WriteLine($"予約IDとの衝突: {conflicts.Count}件");
        Console.WriteLine($"  衝突ID: {string.Join(", ", conflicts.Select(c => c.ID).OrderBy(x => x))}");
        Console.WriteLine();

        var reassignments = FileOperations.ReassignScene(scenePath, pinned);

        Console.WriteLine("再割り当て結果:");
        foreach (var kvp in reassignments.OrderBy(r => r.Key))
        {
            Console.WriteLine($"  ID {kvp.Key} → {kvp.Value}");
        }
        Console.WriteLine();
        Console.WriteLine($"OK: {reassignments.Count}件のIDを再割り当てしました。（シーンのみ更新）");
        Console.WriteLine("Pinnedファイルへの反映: dotnet run -- update");
        return 0;
    }

    /// <summary>
    /// 外部環境のJSONをインポートしてPinnedファイルを生成
    /// </summary>
    private static int RunImportPinned(string[] args, string projectRoot, string scenePath, string pinnedPath)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Error: インポートするJSONファイルのパスを指定してください。");
            Console.Error.WriteLine("例: dotnet run -- import-pinned \"path/to/network_ids.json\"");
            return 1;
        }

        var sourcePath = args[1];
        if (!Path.IsPathRooted(sourcePath))
        {
            sourcePath = Path.Combine(projectRoot, sourcePath);
        }

        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Error: ファイルが見つかりません: {sourcePath}");
            return 1;
        }

        var partnerMapping = FileOperations.LoadPartnerJson(sourcePath);
        if (partnerMapping == null || partnerMapping.Count == 0)
        {
            Console.Error.WriteLine("Error: JSONファイルからNetworkIDを読み込めませんでした。");
            return 1;
        }

        Console.WriteLine($"インポート元: {sourcePath}");
        Console.WriteLine($"外部エントリ数: {partnerMapping.Count}");

        List<NetworkIDEntry> sceneEntries;
        if (File.Exists(scenePath))
        {
            var content = FileOperations.ReadSceneNormalized(scenePath);
            sceneEntries = SceneParser.Parse(content);
            Console.WriteLine($"シーンエントリ数: {sceneEntries.Count}");
        }
        else
        {
            Console.WriteLine("WARNING: シーンファイルが見つかりません。すべて予約エントリになります。");
            sceneEntries = new List<NetworkIDEntry>();
        }

        var merged = Validator.MergePartnerWithScene(partnerMapping, sceneEntries);
        var localCount = merged.OfType<LocalEntry>().Count();
        var reservedCount = merged.OfType<ReservedEntry>().Count();

        Console.WriteLine();
        Console.WriteLine($"ローカル解決済み: {localCount}件");
        Console.WriteLine($"予約（外部環境のみ）: {reservedCount}件");

        var existing = FileOperations.LoadPinned(pinnedPath);
        if (existing != null)
        {
            var existingIds = new HashSet<int>(existing.Select(e => e.ID));
            var newIds = new HashSet<int>(merged.Select(e => e.ID));
            var removed = existingIds.Except(newIds).ToList();

            if (removed.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: 既存Pinnedから削除されるID: {removed.Count}件");
                Console.Write("続行しますか？ (y/N): ");
                var input = Console.ReadLine()?.Trim().ToLower();
                if (input != "y" && input != "yes")
                {
                    Console.WriteLine("インポートをキャンセルしました。");
                    return 1;
                }
            }
        }

        FileOperations.SavePinned(pinnedPath, merged);

        Console.WriteLine();
        Console.WriteLine($"OK: Pinnedファイルを保存しました: {pinnedPath}");
        Console.WriteLine($"   合計: {merged.Count}件 (ローカル: {localCount}, 予約: {reservedCount})");
        return 0;
    }

    private static int RunShowPinned(string pinnedPath)
    {
        var pinned = FileOperations.LoadPinned(pinnedPath);
        if (pinned == null)
        {
            Console.WriteLine("Pinnedファイルが存在しません。");
            Console.WriteLine("インポート: dotnet run -- import-pinned <json-path>");
            return 0;
        }

        var local = 0;
        var reserved = 0;
        foreach (var entry in pinned.OrderBy(e => e.ID))
        {
            switch (entry)
            {
                case LocalEntry localEntry:
                    Console.WriteLine($"  ID {localEntry.ID,5}: {localEntry.Path}  [local: {localEntry.GameObjectFileID}]");
                    local++;
                    break;
                case ReservedEntry reservedEntry:
                    Console.WriteLine($"  ID {reservedEntry.ID,5}: {reservedEntry.Path}  [reserved]");
                    reserved++;
                    break;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"合計: {pinned.Count}件 (ローカル: {local}, 予約: {reserved})");
        return 0;
    }

    private static int RunShow(string scenePath)
    {
        if (!File.Exists(scenePath))
        {
            Console.Error.WriteLine($"Error: シーンファイルが見つかりません: {scenePath}");
            return 1;
        }

        var content = FileOperations.ReadSceneNormalized(scenePath);
        var current = SceneParser.Parse(content);
        Console.WriteLine($"NetworkIDs エントリ数: {current.Count}");
        Console.WriteLine();

        foreach (var entry in current)
        {
            Console.WriteLine($"ID {entry.ID}: gameObject={entry.GameObjectFileID} [{string.Join(", ", entry.SerializedTypeNames)}]");
        }
        return 0;
    }

    private static int ShowHelp()
    {
        Console.WriteLine(@"NetworkID Checker - VRChat NetworkID変更検知ツール

使用方法:
  dotnet run -- <command> [options]

コマンド:
  check                Pinnedファイルとシーンを比較（CI用）
                       消失・変更・予約衝突 → エラー、追加のみ → OK
  update               シーンの現在状態でPinnedファイルを更新
  restore              PinnedファイルからシーンのNetworkIDsを復元
  reassign             予約IDと衝突したエントリに新IDを採番して解消
  show                 シーンの現在のNetworkIDsを表示
  import-pinned <path> 外部環境のID→パスJSONをインポートしてPinnedファイルを生成
  show-pinned          Pinnedファイルの内容を表示
  --help               このヘルプを表示

パス指定オプション:
  --scene=<path>       シーンファイルのパスを指定（プロジェクトルートからの相対パス）
  --pinned=<path>      Pinnedファイルのパスを指定（プロジェクトルートからの相対パス）

オプション:
  --yes, -y            確認プロンプトをスキップ（CI/スクリプトでの非対話実行用）
  --force              updateで危険な変更（既存ペア変更・削除等）を許可する
                       データ消失リスクがあるため、通常は使用しないこと

統合Pinnedファイル (networkids_pinned.json):
  gameObject付き → ローカルで解決済み。シーン復元と変更検知に使用。
  gameObjectなし → 外部環境のみの予約ID。衝突検知に使用。

安全性ポリシー:
  update は新規追加のみ自動許可。既存ペアの変更・削除は --force が必須。
  ビルド前ダイアログでは update 相当の操作は提供しない（restore/reassign のみ）。");

        return 0;
    }

    private static int ShowUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Error: 不明なコマンド: {command}");
        Console.Error.WriteLine("'--help' で使用方法を確認してください。");
        return 1;
    }
}
