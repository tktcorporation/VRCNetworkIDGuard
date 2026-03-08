using System.Collections.Generic;
using System.Linq;
using VRCNetworkIDGuard;
using Xunit;

public class ValidatorTests
{
    [Fact]
    public void Validate_NoChanges_IsValid()
    {
        var scene = MakeSceneEntries((10, "100"), (11, "200"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/B", "200", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/C"),
        };

        var result = Validator.Validate(scene, pinned);

        Assert.True(result.IsValid);
        Assert.False(result.HasAnyChanges);
    }

    [Fact]
    public void Validate_SafeAddition_IsValid()
    {
        var scene = MakeSceneEntries((10, "100"), (99, "999"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/C"),
        };

        var result = Validator.Validate(scene, pinned);

        Assert.True(result.IsValid);
        Assert.True(result.HasSafeAdditionsOnly);
        Assert.Contains(result.SafeAdditions, c => c.ID == 99);
    }

    [Fact]
    public void Validate_ObjectRemoved_IsInvalid()
    {
        var scene = MakeSceneEntries((10, "100"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/B", "200", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var result = Validator.Validate(scene, pinned);

        Assert.False(result.IsValid);
        Assert.Contains(result.DangerousChanges, c => c.Kind == ChangeKind.Removed && c.ID == 11);
    }

    [Fact]
    public void Validate_FileIdChanged_IsInvalid()
    {
        var scene = MakeSceneEntries((10, "999"), (11, "200"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/B", "200", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var result = Validator.Validate(scene, pinned);

        Assert.False(result.IsValid);
        var change = Assert.Single(result.DangerousChanges);
        Assert.Equal(10, change.ID);
        Assert.Equal(ChangeKind.GameObjectChanged, change.Kind);
    }

    [Fact]
    public void Validate_ReservedIdConflict_IsInvalid()
    {
        var scene = MakeSceneEntries((10, "100"), (732, "999"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.Validate(scene, pinned);

        Assert.False(result.IsValid);
        Assert.Contains(result.DangerousChanges, c => c.Kind == ChangeKind.ReservedConflict && c.ID == 732);
    }

    [Fact]
    public void Validate_MixedProblems()
    {
        var scene = MakeSceneEntries((10, "999"), (732, "300"), (99, "999"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/B", "200", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.Validate(scene, pinned);

        Assert.False(result.IsValid);
        Assert.Contains(result.DangerousChanges, c => c.Kind == ChangeKind.Removed && c.ID == 11);
        Assert.Contains(result.DangerousChanges, c => c.Kind == ChangeKind.GameObjectChanged && c.ID == 10);
        Assert.Contains(result.DangerousChanges, c => c.Kind == ChangeKind.ReservedConflict && c.ID == 732);
        Assert.Contains(result.SafeAdditions, c => c.ID == 99);
    }

    [Fact]
    public void Validate_EmptyScene_AllLocalEntriesReportedRemoved()
    {
        var scene = new List<NetworkIDEntry>();
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/B", "200", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/C"),
        };

        var result = Validator.Validate(scene, pinned);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.DangerousChanges.Count(c => c.Kind == ChangeKind.Removed));
        // 予約エントリは Removed にならない
        Assert.DoesNotContain(result.Changes, c => c.ID == 732 && c.Kind == ChangeKind.Removed);
    }

    [Fact]
    public void GetDetailedMessage_ShowsAllCategories()
    {
        var scene = MakeSceneEntries((10, "999"), (732, "300"), (99, "999"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/B", "200", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.Validate(scene, pinned);
        var message = result.GetDetailedMessage();

        Assert.Contains("[NG]", message);
        Assert.Contains("[OK]", message);
        Assert.Contains("消失", message);
        Assert.Contains("予約済みID", message);
        Assert.Contains("新規追加", message);
    }

    [Fact]
    public void GetDetailedMessage_NoChanges()
    {
        var scene = MakeSceneEntries((10, "100"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var result = Validator.Validate(scene, pinned);
        Assert.Contains("変更なし", result.GetDetailedMessage());
    }

    [Fact]
    public void MergePartnerWithScene_CreatesLocalAndReserved()
    {
        var partner = new Dictionary<int, string>
        {
            { 10, "/Cooker" },
            { 11, "/Scanner" },
            { 732, "/Wagon" },
        };
        var scene = MakeSceneEntries((10, "100"), (11, "200"));

        var result = Validator.MergePartnerWithScene(partner, scene);

        Assert.Equal(3, result.Count);
        var byId = result.ToDictionary(e => e.ID);

        var local10 = Assert.IsType<LocalEntry>(byId[10]);
        Assert.Equal("100", local10.GameObjectFileID);
        Assert.Equal("/Cooker", local10.Path);

        Assert.IsType<LocalEntry>(byId[11]);
        Assert.IsType<ReservedEntry>(byId[732]);
    }

    [Fact]
    public void GetDetailedMessage_WithPersistenceImpact_ShowsImpactInfo()
    {
        var changes = new List<ValidationChange>
        {
            new ValidationChange(42, ChangeKind.GameObjectChanged, ChangeSeverity.Dangerous,
                "ID 42: gameObject 変更 12345 -> 67890")
            { PersistenceImpact = PersistenceImpactResult.Detected(
                new List<PersistenceInfo> { new PersistenceInfo(true, "MoguUserData", 18) }) },
            new ValidationChange(99, ChangeKind.ReservedConflict, ChangeSeverity.Dangerous,
                "予約済みIDとの衝突: 99")
            { PersistenceImpact = PersistenceImpactResult.Detected(
                new List<PersistenceInfo> { new PersistenceInfo(false, "Clock", 0) }) },
        };
        var result = new ValidationResult(changes);

        var message = result.GetDetailedMessage();

        Assert.Contains("セーブデータ影響あり", message);
        Assert.Contains("MoguUserData", message);
        Assert.Contains("18", message);
        Assert.Contains("セーブデータ影響なし", message);
    }

    [Fact]
    public void GetDetailedMessage_WithUnknownImpact_NoImpactLine()
    {
        // Unknown（検知未実行）の場合はセーブデータ関連の表示が出ない
        var changes = new List<ValidationChange>
        {
            new ValidationChange(42, ChangeKind.GameObjectChanged, ChangeSeverity.Dangerous,
                "ID 42: gameObject 変更 12345 -> 67890"),
                // PersistenceImpact はデフォルトの Unknown
        };
        var result = new ValidationResult(changes);

        var message = result.GetDetailedMessage();

        Assert.DoesNotContain("セーブデータ", message);
    }

    [Fact]
    public void GetSummaryMessage_WithPersistenceImpact_ShowsCount()
    {
        var changes = new List<ValidationChange>
        {
            new ValidationChange(42, ChangeKind.Removed, ChangeSeverity.Dangerous, "消失: 42")
            { PersistenceImpact = PersistenceImpactResult.Detected(
                new List<PersistenceInfo> { new PersistenceInfo(true, "MoguUserData", 18) }) },
            new ValidationChange(99, ChangeKind.ReservedConflict, ChangeSeverity.Dangerous, "衝突: 99")
            { PersistenceImpact = PersistenceImpactResult.Detected(
                new List<PersistenceInfo> { new PersistenceInfo(false, "Clock", 0) }) },
        };
        var result = new ValidationResult(changes);

        var message = result.GetSummaryMessage();

        Assert.Contains("セーブデータ影響: 1件", message);
    }

    [Fact]
    public void Validate_WithPersistenceMap_AttachesInfoToChanges()
    {
        // シーン: ID 42 の fileID が 888 → 999 に変更
        var scene = TestHelpers.MakeSceneEntries((42, "999"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(42, "/Mogu", "888", new[] { "VRC.Udon.UdonBehaviour" }),
        };
        var persistenceMap = new Dictionary<string, List<PersistenceInfo>>
        {
            ["888"] = new List<PersistenceInfo> { new PersistenceInfo(true, "MoguUserData", 18) },
        };

        var result = Validator.Validate(scene, pinned, persistenceMap);

        var change = Assert.Single(result.DangerousChanges);
        Assert.Equal(ChangeKind.GameObjectChanged, change.Kind);
        var detected = Assert.IsType<PersistenceImpactResult.DetectedResult>(change.PersistenceImpact);
        Assert.True(detected.Infos[0].HasSyncedFields);
        Assert.Equal("MoguUserData", detected.Infos[0].ComponentName);
    }

    [Fact]
    public void Validate_WithoutPersistenceMap_LeavesImpactUnknown()
    {
        var scene = TestHelpers.MakeSceneEntries((42, "999"));
        var pinned = new List<PinnedEntryBase>
        {
            new LocalEntry(42, "/Mogu", "888", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var result = Validator.Validate(scene, pinned);

        var change = Assert.Single(result.DangerousChanges);
        Assert.Same(PersistenceImpactResult.Unknown, change.PersistenceImpact);
    }

    private static List<NetworkIDEntry> MakeSceneEntries(params (int id, string fileId)[] items)
        => TestHelpers.MakeSceneEntries(items);
}
