#nullable enable
// NetworkID 検証ロジックと Pinned 更新ロジック
//
// NetworkIDValidatorCore.cs から検証・更新の純粋ロジックを抽出したモジュール。
// ファイル I/O や Unity API への依存はなく、入力リストに対して変更検知・マージを行う。
//
// ValidationResult: 変更リストを保持し、安全性判定やメッセージ生成を提供する。
// Validator: シーン状態と Pinned 状態を比較して ValidationResult を生成する静的メソッド群。

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VRCNetworkIDGuard
{
    /// <summary>
    /// バリデーション結果を保持し、安全性判定・メッセージ生成を提供する。
    ///
    /// 全変更を ValidationChange のリストとして統一的に管理することで、
    /// フィルタリング・集計・表示が LINQ で自然に書ける。
    /// IsValid は「Dangerous な変更が1件もないか」で判定する。
    /// </summary>
    public sealed class ValidationResult
    {
        public IReadOnlyList<ValidationChange> Changes { get; }

        public ValidationResult(List<ValidationChange> changes)
        {
            Changes = changes.AsReadOnly();
        }

        /// <summary>危険な変更がなければ true（新規追加のみ、または変更なし）</summary>
        public bool IsValid => !Changes.Any(c => c.Severity == ChangeSeverity.Dangerous);

        /// <summary>何らかの変更（追加含む）があれば true</summary>
        public bool HasAnyChanges => Changes.Count > 0;

        /// <summary>安全な追加のみで構成されている場合 true（変更なしは false）</summary>
        public bool HasSafeAdditionsOnly => IsValid && Changes.Any(c => c.Kind == ChangeKind.Added);

        public IEnumerable<ValidationChange> DangerousChanges =>
            Changes.Where(c => c.Severity == ChangeSeverity.Dangerous);
        public IEnumerable<ValidationChange> SafeAdditions =>
            Changes.Where(c => c.Kind == ChangeKind.Added);

        /// <summary>
        /// カテゴリ別に [NG] / [OK] タグ付きの詳細メッセージを生成する。
        /// CI ログやエディタウィンドウでの表示用。
        /// </summary>
        public string GetDetailedMessage()
        {
            var sb = new StringBuilder();

            var removed = Changes.Where(c => c.Kind == ChangeKind.Removed).ToList();
            if (removed.Count > 0)
            {
                sb.AppendLine($"[NG] シーンから消失した NetworkID: {string.Join(", ", removed.Select(c => c.ID).OrderBy(x => x))}");
                sb.AppendLine("     → 対応するオブジェクトが削除された可能性があります");
                AppendGroupedPersistenceImpact(sb, removed);
            }

            foreach (var change in Changes.Where(c => c.Kind == ChangeKind.GameObjectChanged).OrderBy(c => c.ID))
            {
                sb.AppendLine($"[NG] {change.Detail}");
                AppendSinglePersistenceImpact(sb, change);
            }

            var conflicts = Changes.Where(c => c.Kind == ChangeKind.ReservedConflict).ToList();
            if (conflicts.Count > 0)
            {
                sb.AppendLine($"[NG] 予約済みIDとの衝突: {string.Join(", ", conflicts.Select(c => c.ID).OrderBy(x => x))}");
                sb.AppendLine("     → 先方環境で使用中のIDがローカルで新規割り当てされています");
                AppendGroupedPersistenceImpact(sb, conflicts);
            }

            var added = Changes.Where(c => c.Kind == ChangeKind.Added).ToList();
            if (added.Count > 0)
            {
                sb.AppendLine($"[OK] 新規追加 NetworkID: {string.Join(", ", added.Select(c => c.ID).OrderBy(x => x))}");
            }

            if (sb.Length == 0)
            {
                sb.AppendLine("変更なし");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 1行サマリーメッセージを生成する。PR コメントやステータスバー向け。
        /// </summary>
        public string GetSummaryMessage()
        {
            var parts = new List<string>();
            var removedCount = Changes.Count(c => c.Kind == ChangeKind.Removed);
            var modifiedCount = Changes.Count(c => c.Kind == ChangeKind.GameObjectChanged);
            var conflictCount = Changes.Count(c => c.Kind == ChangeKind.ReservedConflict);
            var addedCount = Changes.Count(c => c.Kind == ChangeKind.Added);

            if (removedCount > 0) parts.Add($"消失: {removedCount}件");
            if (modifiedCount > 0) parts.Add($"変更: {modifiedCount}件");
            if (conflictCount > 0) parts.Add($"予約衝突: {conflictCount}件");
            if (addedCount > 0) parts.Add($"追加: {addedCount}件");

            var persistenceCount = Changes.Count(c => c.PersistenceImpact.HasSyncedFields);
            if (persistenceCount > 0) parts.Add($"セーブデータ影響: {persistenceCount}件");

            if (parts.Count == 0) return "NetworkIDs に変更はありません。";
            return $"NetworkIDs: {string.Join(", ", parts)}";
        }

        private static void AppendGroupedPersistenceImpact(StringBuilder sb, List<ValidationChange> changes)
        {
            var withImpact = new List<(int ID, PersistenceInfo Info)>();
            var hasAnyDetected = false;

            foreach (var change in changes.OrderBy(c => c.ID))
            {
                if (change.PersistenceImpact == PersistenceImpactResult.Unknown) continue;
                hasAnyDetected = true;

                if (change.PersistenceImpact is PersistenceImpactResult.DetectedResult detected)
                {
                    foreach (var info in detected.Infos)
                    {
                        if (info.HasSyncedFields)
                            withImpact.Add((change.ID, info));
                    }
                }
            }

            if (!hasAnyDetected) return;

            if (withImpact.Count > 0)
            {
                foreach (var (id, info) in withImpact)
                {
                    sb.AppendLine($"     ⚠ ID {id}: セーブデータ影響あり: {info.ComponentName} (UdonSynced: {info.SyncedFieldCount}フィールド)");
                }
            }
            else
            {
                sb.AppendLine("     セーブデータ影響なし");
            }
        }

        private static void AppendSinglePersistenceImpact(StringBuilder sb, ValidationChange change)
        {
            switch (change.PersistenceImpact)
            {
                case PersistenceImpactResult.DetectedResult detected:
                    foreach (var info in detected.Infos)
                    {
                        if (info.HasSyncedFields)
                            sb.AppendLine($"     ⚠ セーブデータ影響あり: {info.ComponentName} (UdonSynced: {info.SyncedFieldCount}フィールド)");
                        else
                            sb.AppendLine("     セーブデータ影響なし");
                    }
                    break;
                case var r when r == PersistenceImpactResult.NoUdonBehaviour:
                    sb.AppendLine("     セーブデータ影響なし");
                    break;
            }
        }
    }

    /// <summary>
    /// シーン状態と Pinned 状態を比較して変更を検出する静的メソッド群。
    ///
    /// 全メソッドは純粋関数で、ファイル I/O や Unity API に依存しない。
    /// これにより xUnit での単体テストが可能。
    /// </summary>
    public static class Validator
    {
        /// <summary>
        /// シーンの現在状態と Pinned エントリを比較し、変更を検出する。
        /// </summary>
        public static ValidationResult Validate(
            List<NetworkIDEntry> currentScene,
            List<PinnedEntryBase> pinnedEntries,
            Dictionary<string, List<PersistenceInfo>>? persistenceMap = null)
        {
            var changes = new List<ValidationChange>();
            var currentByID = currentScene
                .GroupBy(e => e.ID)
                .ToDictionary(g => g.Key, g => g.Last());
            var allPinnedIDs = new HashSet<int>(pinnedEntries.Select(e => e.ID));

            foreach (var pinned in pinnedEntries)
            {
                switch (pinned)
                {
                    case LocalEntry local:
                        if (!currentByID.TryGetValue(local.ID, out var current))
                        {
                            changes.Add(new ValidationChange(local.ID, ChangeKind.Removed, ChangeSeverity.Dangerous,
                                $"シーンから消失した NetworkID: {local.ID}")
                            { PersistenceImpact = ResolvePersistenceImpact(persistenceMap, local.GameObjectFileID) });
                        }
                        else if (current.GameObjectFileID != local.GameObjectFileID)
                        {
                            changes.Add(new ValidationChange(local.ID, ChangeKind.GameObjectChanged, ChangeSeverity.Dangerous,
                                $"ID {local.ID}: gameObject 変更 {local.GameObjectFileID} -> {current.GameObjectFileID}")
                            { PersistenceImpact = ResolvePersistenceImpact(persistenceMap, local.GameObjectFileID) });
                        }
                        break;

                    case ReservedEntry reserved:
                        if (currentByID.TryGetValue(reserved.ID, out var conflicting))
                        {
                            changes.Add(new ValidationChange(reserved.ID, ChangeKind.ReservedConflict, ChangeSeverity.Dangerous,
                                $"予約済みIDとの衝突: {reserved.ID}")
                            { PersistenceImpact = ResolvePersistenceImpact(persistenceMap, conflicting.GameObjectFileID) });
                        }
                        break;
                }
            }

            foreach (var id in currentByID.Keys)
            {
                if (!allPinnedIDs.Contains(id))
                {
                    changes.Add(new ValidationChange(id, ChangeKind.Added, ChangeSeverity.Safe,
                        $"新規追加 NetworkID: {id}")
                    { PersistenceImpact = ResolvePersistenceImpact(persistenceMap, currentByID[id].GameObjectFileID) });
                }
            }

            return new ValidationResult(changes);
        }

        private static PersistenceImpactResult ResolvePersistenceImpact(
            Dictionary<string, List<PersistenceInfo>>? persistenceMap, string gameObjectFileID)
        {
            if (persistenceMap == null) return PersistenceImpactResult.Unknown;
            return persistenceMap.TryGetValue(gameObjectFileID, out var infos)
                ? PersistenceImpactResult.Detected(infos.AsReadOnly())
                : PersistenceImpactResult.NoUdonBehaviour;
        }

        /// <summary>
        /// パートナーのマッピング情報とシーンエントリをマージして Pinned リストを生成する。
        /// </summary>
        public static List<PinnedEntryBase> MergePartnerWithScene(
            Dictionary<int, string> partnerMapping,
            List<NetworkIDEntry> sceneEntries)
        {
            var sceneByID = sceneEntries
                .GroupBy(e => e.ID)
                .ToDictionary(g => g.Key, g => g.Last());
            var result = new List<PinnedEntryBase>();

            foreach (var kvp in partnerMapping.OrderBy(k => k.Key))
            {
                if (sceneByID.TryGetValue(kvp.Key, out var sceneEntry))
                {
                    result.Add(new LocalEntry(kvp.Key, kvp.Value, sceneEntry.GameObjectFileID,
                        new List<string>(sceneEntry.SerializedTypeNames).AsReadOnly()));
                }
                else
                {
                    result.Add(new ReservedEntry(kvp.Key, kvp.Value));
                }
            }

            return result;
        }

        /// <summary>
        /// 予約IDと衝突しているシーンエントリに新しいIDを採番して衝突を解消する。
        /// </summary>
        public static ReassignResult ReassignReservedConflicts(
            List<NetworkIDEntry> sceneEntries,
            List<PinnedEntryBase> pinnedEntries)
        {
            var reservedIDs = new HashSet<int>(
                pinnedEntries.OfType<ReservedEntry>().Select(e => e.ID));

            var conflictingIDs = sceneEntries
                .Where(e => reservedIDs.Contains(e.ID))
                .Select(e => e.ID)
                .ToList();

            if (conflictingIDs.Count == 0)
            {
                return new ReassignResult(
                    new List<NetworkIDEntry>(sceneEntries),
                    new Dictionary<int, int>());
            }

            var allIDs = new HashSet<int>(sceneEntries.Select(e => e.ID));
            allIDs.UnionWith(pinnedEntries.Select(e => e.ID));
            var nextID = allIDs.Count > 0 ? allIDs.Max() + 1 : 1;

            var reassignments = new Dictionary<int, int>();

            foreach (var oldID in conflictingIDs.OrderBy(id => id))
            {
                reassignments[oldID] = nextID;
                nextID++;
            }

            var reassigned = sceneEntries.Select(e =>
            {
                if (reassignments.TryGetValue(e.ID, out var newID))
                {
                    return new NetworkIDEntry(newID, e.GameObjectFileID, e.SerializedTypeNames);
                }
                return e;
            }).ToList();

            return new ReassignResult(reassigned, reassignments);
        }

        /// <summary>
        /// シーンの現在状態で Pinned を更新する。
        /// </summary>
        public static UpdateResult UpdatePinnedFromScene(
            List<NetworkIDEntry> currentScene,
            List<PinnedEntryBase>? existingPinned)
        {
            var currentByID = currentScene
                .GroupBy(e => e.ID)
                .ToDictionary(g => g.Key, g => g.Last());
            var existingByID = new Dictionary<int, PinnedEntryBase>();
            if (existingPinned != null)
            {
                foreach (var e in existingPinned)
                    existingByID[e.ID] = e;
            }

            var newEntries = new List<PinnedEntryBase>();
            var newIDsWithoutPath = new List<int>();

            foreach (var entry in currentScene)
            {
                string path;
                if (existingByID.TryGetValue(entry.ID, out var existing))
                {
                    path = existing.Path;
                }
                else
                {
                    path = "(unknown)";
                    newIDsWithoutPath.Add(entry.ID);
                }

                newEntries.Add(new LocalEntry(
                    entry.ID, path, entry.GameObjectFileID,
                    new List<string>(entry.SerializedTypeNames).AsReadOnly()));
            }

            if (existingPinned != null)
            {
                foreach (var entry in existingPinned)
                {
                    if (entry is ReservedEntry reserved && !currentByID.ContainsKey(reserved.ID))
                    {
                        newEntries.Add(reserved);
                    }
                }
            }

            return new UpdateResult(newEntries, newIDsWithoutPath);
        }
    }
}
