// ReassignReservedConflicts の単体テスト
//
// テスト観点:
// - 衝突なし: そのまま返る
// - 単一衝突: 新IDが全IDの最大値+1
// - 複数衝突: 連番で採番される
// - 衝突+非衝突混在: 非衝突エントリは変更されない
// - 全IDが予約と衝突: 全エントリが再割り当てされる

using System.Collections.Generic;
using System.Linq;
using VRCNetworkIDGuard;
using Xunit;

public class ReassignTests
{
    [Fact]
    public void ReassignReservedConflicts_NoConflicts_ReturnsUnchanged()
    {
        var scene = MakeSceneEntries((10, "100"), (11, "200"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/B", "200", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.ReassignReservedConflicts(scene, pinned);

        Assert.Empty(result.Reassignments);
        Assert.Equal(2, result.ReassignedSceneEntries.Count);
        Assert.Equal(10, result.ReassignedSceneEntries[0].ID);
        Assert.Equal(11, result.ReassignedSceneEntries[1].ID);
    }

    [Fact]
    public void ReassignReservedConflicts_SingleConflict_AssignsNextAvailableId()
    {
        var scene = MakeSceneEntries((10, "100"), (732, "999"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.ReassignReservedConflicts(scene, pinned);

        Assert.Single(result.Reassignments);
        Assert.True(result.Reassignments.ContainsKey(732));

        // 新IDは全ID（10, 732）の最大値+1 = 733
        Assert.Equal(733, result.Reassignments[732]);

        // シーンエントリが更新されている
        Assert.DoesNotContain(result.ReassignedSceneEntries, e => e.ID == 732);
        Assert.Contains(result.ReassignedSceneEntries, e => e.ID == 733);

        // 非衝突エントリは変更なし
        Assert.Contains(result.ReassignedSceneEntries, e => e.ID == 10 && e.GameObjectFileID == "100");
    }

    [Fact]
    public void ReassignReservedConflicts_MultipleConflicts_AssignsSequentialIds()
    {
        var scene = MakeSceneEntries((10, "100"), (732, "300"), (733, "400"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon/A"),
            new ReservedEntry(733, "/Wagon/B"),
        };

        var result = Validator.ReassignReservedConflicts(scene, pinned);

        Assert.Equal(2, result.Reassignments.Count);
        // 全ID: 10, 732, 733 → 最大733 → 新IDは734, 735
        Assert.Equal(734, result.Reassignments[732]);
        Assert.Equal(735, result.Reassignments[733]);

        // 元のIDがシーンエントリに残っていないこと
        var ids = result.ReassignedSceneEntries.Select(e => e.ID).ToHashSet();
        Assert.DoesNotContain(732, ids);
        Assert.DoesNotContain(733, ids);
        Assert.Contains(734, ids);
        Assert.Contains(735, ids);
        Assert.Contains(10, ids);
    }

    [Fact]
    public void ReassignReservedConflicts_PreservesGameObjectFileID()
    {
        // 再割り当て後も GameObjectFileID は維持される（同じオブジェクトの ID が変わるだけ）
        var scene = MakeSceneEntries((732, "12345"));
        var pinned = new List<PinnedEntryBase>
        {
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.ReassignReservedConflicts(scene, pinned);

        var reassigned = result.ReassignedSceneEntries.Single();
        Assert.Equal(733, reassigned.ID);
        Assert.Equal("12345", reassigned.GameObjectFileID);
    }

    [Fact]
    public void ReassignReservedConflicts_PreservesSerializedTypeNames()
    {
        var scene = new List<NetworkIDEntry>
        {
            new NetworkIDEntry(732, "100", new[] { "VRC.Udon.UdonBehaviour", "VRC.SDK3.Components.VRCPickup" }),
        };
        var pinned = new List<PinnedEntryBase>
        {
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.ReassignReservedConflicts(scene, pinned);

        var reassigned = result.ReassignedSceneEntries.Single();
        Assert.Equal(2, reassigned.SerializedTypeNames.Count);
        Assert.Contains("VRC.Udon.UdonBehaviour", reassigned.SerializedTypeNames);
        Assert.Contains("VRC.SDK3.Components.VRCPickup", reassigned.SerializedTypeNames);
    }

    [Fact]
    public void ReassignReservedConflicts_NewIdDoesNotConflictWithExistingPinnedIds()
    {
        // pinned に ID 734 のローカルエントリがある場合、
        // 新IDは 734 を飛ばして 735 から採番される
        var scene = MakeSceneEntries((10, "100"), (732, "300"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon"),
            new LocalEntry(734, "/D", "400", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var result = Validator.ReassignReservedConflicts(scene, pinned);

        // 全ID: 10, 732, 734 → 最大734 → 新IDは735
        Assert.Equal(735, result.Reassignments[732]);
    }

    [Fact]
    public void ReassignReservedConflicts_DoesNotMutatePinnedEntries()
    {
        // 再割り当てはシーンエントリのみ変更し、Pinned リストには一切触れない。
        // Pinned の更新は CLI の update コマンドの責務であり、
        // ビルドダイアログや reassign コマンドから Pinned が変わると
        // 意図しない状態が永続化するリスクがある。
        var scene = MakeSceneEntries((10, "100"), (732, "999"), (733, "888"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon/A"),
            new ReservedEntry(733, "/Wagon/B"),
        };

        // Pinned の状態を記録
        var pinnedCountBefore = pinned.Count;
        var pinnedIdsBefore = pinned.Select(p => p.ID).ToList();

        var result = Validator.ReassignReservedConflicts(scene, pinned);

        // 再割り当てが実行されたことを確認（テストの前提条件）
        Assert.Equal(2, result.Reassignments.Count);

        // Pinned リストが変更されていないこと
        Assert.Equal(pinnedCountBefore, pinned.Count);
        Assert.Equal(pinnedIdsBefore, pinned.Select(p => p.ID).ToList());

        // Pinned の各エントリの型も変わっていないこと
        Assert.IsType<LocalEntry>(pinned[0]);
        Assert.IsType<ReservedEntry>(pinned[1]);
        Assert.IsType<ReservedEntry>(pinned[2]);
    }

    private static List<NetworkIDEntry> MakeSceneEntries(params (int id, string fileId)[] items)
        => TestHelpers.MakeSceneEntries(items);
}
