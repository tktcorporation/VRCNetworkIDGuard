using System.Collections.Generic;
using System.Linq;
using VRCNetworkIDGuard;
using Xunit;

public class UpdatePinnedTests
{
    [Fact]
    public void UpdatePinnedFromScene_NoPriorPinned_CreatesFromScene()
    {
        var scene = MakeSceneEntries((10, "100"), (11, "200"));
        var result = Validator.UpdatePinnedFromScene(scene, null);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.IsType<LocalEntry>(e));
        Assert.Equal(2, result.NewIDsWithoutPath.Count);
    }

    [Fact]
    public void UpdatePinnedFromScene_WithExisting_InheritsPath()
    {
        var scene = MakeSceneEntries((10, "100"));
        var existing = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/Cooker", "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var result = Validator.UpdatePinnedFromScene(scene, existing);

        var entry = Assert.IsType<LocalEntry>(result.Entries.Single());
        Assert.Equal("/Cooker", entry.Path);
        Assert.Empty(result.NewIDsWithoutPath);
    }

    [Fact]
    public void UpdatePinnedFromScene_NewId_MarksUnknownPath()
    {
        var scene = MakeSceneEntries((10, "100"), (99, "999"));
        var existing = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/Cooker", "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var result = Validator.UpdatePinnedFromScene(scene, existing);

        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(99, result.NewIDsWithoutPath);
        var newEntry = result.Entries.OfType<LocalEntry>().First(e => e.ID == 99);
        Assert.Equal("(unknown)", newEntry.Path);
    }

    [Fact]
    public void UpdatePinnedFromScene_PreservesReservedEntries()
    {
        var scene = MakeSceneEntries((10, "100"));
        var existing = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/Cooker", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.UpdatePinnedFromScene(scene, existing);

        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, e => e is ReservedEntry r && r.ID == 732);
    }

    [Fact]
    public void UpdatePinnedFromScene_ReservedConflict_BecomesLocal()
    {
        var scene = MakeSceneEntries((732, "999"));
        var existing = new List<PinnedEntryBase>
        {
            new ReservedEntry(732, "/Wagon"),
        };

        var result = Validator.UpdatePinnedFromScene(scene, existing);

        var entry = Assert.IsType<LocalEntry>(result.Entries.Single());
        Assert.Equal(732, entry.ID);
        Assert.Equal("/Wagon", entry.Path);
    }

    private static List<NetworkIDEntry> MakeSceneEntries(params (int id, string fileId)[] items)
        => TestHelpers.MakeSceneEntries(items);
}
