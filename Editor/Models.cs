#nullable enable
// NetworkID検証で使用するデータモデル定義
//
// NetworkIDValidatorCore.cs（743行）を機能別に分割するリファクタリングの最初のステップ。
// 全モジュールが共有するデータ型をここに集約する。
//
// 旧設計との違い:
//   - record 型で不変性を保証（パース後のデータは変更されるべきでない）
//   - PinnedEntry の「GameObjectFileID == null なら予約エントリ」という暗黙の規約を
//     LocalEntry / ReservedEntry の型階層で明示化（型で区別できるためバグが減る）
//   - 4つの List<int> で表現していた検証結果を ValidationChange に統合
//
// NetworkIDValidatorCore.cs は削除済み。旧型はこのファイルの型に完全に置き換えられた。

using System.Collections.Generic;

#if UNITY_EDITOR
// Unity 2022.3 の C# 9 サポートでは record の positional properties（init アクセサ）に
// IsExternalInit が必要だが、Unity のランタイムには含まれていないためここで定義する。
// .NET 5+ や CLI ビルドでは標準ライブラリに含まれるため UNITY_EDITOR ガードで囲む。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif

namespace VRCNetworkIDGuard
{
    /// <summary>
    /// シーンファイルから抽出した NetworkID エントリ（ランタイムの現在状態）。
    ///
    /// シーン YAML をパースして得られる生データを表す。
    /// Pinned データとの比較で変更検知に使用される。
    /// record にすることでパース後の意図しない書き換えを防ぐ。
    /// </summary>
    public sealed record NetworkIDEntry(
        int ID,
        string GameObjectFileID,
        IReadOnlyList<string> SerializedTypeNames
    );

    /// <summary>
    /// Pinned エントリの sealed 階層ベース。
    ///
    /// 旧 PinnedEntry は GameObjectFileID の null/非null でローカル解決済みか予約かを
    /// 区別していたが、呼び出し側で毎回 null チェックが必要でバグの温床だった。
    /// sealed 階層にすることで、パターンマッチで網羅的に処理でき、
    /// 「予約エントリに GameObjectFileID を誤って設定する」ような型エラーをコンパイル時に検出できる。
    /// </summary>
    public abstract record PinnedEntryBase(int ID, string Path);

    /// <summary>
    /// ローカルで解決済みの Pinned エントリ。
    ///
    /// fileID でシーン内の GameObject を同定済みで、シーン復元・変更検知に使用できる。
    /// GameObjectFileID が型レベルで非null を保証するため、使用側で null チェック不要。
    /// </summary>
    public sealed record LocalEntry(
        int ID, string Path, string GameObjectFileID,
        IReadOnlyList<string> SerializedTypeNames
    ) : PinnedEntryBase(ID, Path);

    /// <summary>
    /// 先方（パートナー）のみに存在する予約エントリ。
    ///
    /// ローカルのシーンには対応する GameObject がないが、先方のアップロード済みワールドには
    /// この ID が使われている。ローカルで同じ ID が新規発生した場合の衝突検知に使用する。
    /// GameObjectFileID フィールド自体を持たないことで、誤ったアクセスを型レベルで防ぐ。
    /// </summary>
    public sealed record ReservedEntry(
        int ID, string Path
    ) : PinnedEntryBase(ID, Path);

    /// <summary>
    /// 検証で検出された変更の種別。
    ///
    /// 旧設計では addedIDs / removedIDs / gameObjectChangedIDs / reservedConflictIDs と
    /// 4つの別リストで管理していたが、変更種別が増えるたびにフィールド追加が必要だった。
    /// enum に統合することで拡張が容易になり、変更一覧の統一的な表示も可能になる。
    /// </summary>
    public enum ChangeKind
    {
        /// <summary>Pinned に存在しない新規 ID がシーンに追加された</summary>
        Added,

        /// <summary>Pinned にあったローカル解決済み ID がシーンから消えた（データ消失リスク）</summary>
        Removed,

        /// <summary>同じ ID の fileID が変わった（別オブジェクトへのデータ誤紐付けリスク）</summary>
        GameObjectChanged,

        /// <summary>新規 ID が先方の予約 ID と衝突した（アップロード時の ID 競合リスク）</summary>
        ReservedConflict,
    }

    /// <summary>
    /// 変更の危険度。CI やビルド前チェックで判定に使用する。
    ///
    /// Safe な変更（新規追加）は情報表示のみ、Dangerous な変更（削除・衝突等）は
    /// PR ブロックやビルド中断の判断材料になる。
    /// </summary>
    public enum ChangeSeverity
    {
        /// <summary>安全な変更（新規 ID の追加など）</summary>
        Safe,

        /// <summary>危険な変更（ID 削除、fileID 変更、予約衝突など。データ消失・誤紐付けのリスク）</summary>
        Dangerous,
    }

    /// <summary>
    /// 検証で検出された1件の変更を表す統合型。
    ///
    /// 旧 PinnedValidationResult は変更種別ごとに別の List&lt;int&gt; を持っていたため、
    /// 「全変更を時系列で表示」「変更をフィルタリング」といった操作が煩雑だった。
    /// 1件1レコードに統合することで、LINQ での集計・フィルタが自然に書ける。
    /// </summary>
    public sealed record ValidationChange(
        int ID, ChangeKind Kind, ChangeSeverity Severity, string Detail)
    {
        /// <summary>
        /// セーブデータ影響情報。デフォルトは Unknown（検知未実行）。
        /// Validator.Validate が persistenceMap を受け取った場合に設定される。
        /// </summary>
        public PersistenceImpactResult PersistenceImpact { get; init; } = PersistenceImpactResult.Unknown;
    }

    /// <summary>
    /// セーブデータ影響検知の結果を表す sealed 階層。
    ///
    /// 旧設計では IReadOnlyList&lt;PersistenceInfo&gt;? の3状態（null/空/あり）で
    /// 「検知未実行」「UdonBehaviour なし」「検知済み」を暗黙に表現していたが、
    /// 下流の表示ロジックが状態解釈コードで膨れる原因になっていた。
    /// sealed 階層にすることでパターンマッチで網羅的に処理でき、状態の解釈漏れを防ぐ。
    /// </summary>
    public abstract record PersistenceImpactResult
    {
        private PersistenceImpactResult() { }

        /// <summary>検知未実行（persistenceMap が null / エラー時）。表示上は何も出力しない。</summary>
        public static readonly PersistenceImpactResult Unknown = new UnknownResult();

        /// <summary>検知済みだが UdonBehaviour なし。「セーブデータ影響なし」と表示する。</summary>
        public static readonly PersistenceImpactResult NoUdonBehaviour = new NoUdonBehaviourResult();

        /// <summary>検知済みで UdonBehaviour あり。コンポーネント名とフィールド数を表示する。</summary>
        public static PersistenceImpactResult Detected(IReadOnlyList<PersistenceInfo> infos) => new DetectedResult(infos);

        /// <summary>UdonSynced フィールドを持つコンポーネントがあるかどうか。</summary>
        public virtual bool HasSyncedFields => false;

        private sealed record UnknownResult : PersistenceImpactResult;
        private sealed record NoUdonBehaviourResult : PersistenceImpactResult;

        /// <summary>UdonBehaviour が見つかった場合の検知結果。</summary>
        public sealed record DetectedResult(IReadOnlyList<PersistenceInfo> Infos) : PersistenceImpactResult
        {
            public override bool HasSyncedFields
            {
                get
                {
                    for (var i = 0; i < Infos.Count; i++)
                        if (Infos[i].HasSyncedFields) return true;
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// UpdatePinnedFromScene の戻り値。
    ///
    /// シーンの現在状態で Pinned リストを更新した結果を返す。
    /// NewIDsWithoutPath は、シーンに新規追加されたがパス情報が未設定の ID のリスト。
    /// 呼び出し元が Unity API でパスを解決して補完するために使用する。
    /// </summary>
    public sealed record UpdateResult(
        List<PinnedEntryBase> Entries,
        List<int> NewIDsWithoutPath
    );

    /// <summary>
    /// ReassignReservedConflicts の戻り値。
    ///
    /// 予約IDと衝突したシーンエントリに新しいIDを採番した結果を返す。
    /// ReassignedSceneEntries は衝突解消後のシーンエントリリスト（衝突しないエントリはそのまま）。
    /// Reassignments は旧ID→新IDのマッピング（ログ表示・確認ダイアログ用）。
    /// </summary>
    public sealed record ReassignResult(
        List<NetworkIDEntry> ReassignedSceneEntries,
        Dictionary<int, int> Reassignments
    );

    /// <summary>
    /// NetworkID に紐づく GameObject のセーブデータ影響情報。
    ///
    /// UdonBehaviour の .asset ファイルに UdonSyncedAttribute が含まれていれば、
    /// その NetworkID の変更はプレイヤーのセーブデータ消失リスクがある。
    /// HasSyncedFields == false でも NetworkID 変更自体は Dangerous 分類のまま
    /// （SynchronizePosition 等の他の同期機構も存在するため安全側に倒す）。
    /// </summary>
    public sealed record PersistenceInfo(
        bool HasSyncedFields,
        string ComponentName,
        int SyncedFieldCount
    );
}
