using System.Collections.Generic;
using System.Linq;
using VRCNetworkIDGuard;
using Xunit;

public class PinnedFileTests
{
    [Fact]
    public void Parse_LocalEntries_RoundTrips()
    {
        var original = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/Cooker", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new LocalEntry(11, "/Scanner", "200", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var json = PinnedFile.Serialize(original);
        var loaded = PinnedFile.Parse(json);

        Assert.Equal(2, loaded.Count);
        var first = Assert.IsType<LocalEntry>(loaded[0]);
        Assert.Equal(10, first.ID);
        Assert.Equal("/Cooker", first.Path);
        Assert.Equal("100", first.GameObjectFileID);
        Assert.Contains("VRC.Udon.UdonBehaviour", first.SerializedTypeNames);
    }

    [Fact]
    public void Parse_ReservedEntries_RoundTrips()
    {
        var original = new List<PinnedEntryBase>
        {
            new ReservedEntry(732, "/Wagon/Something"),
            new ReservedEntry(733, "/Wagon/Other"),
        };

        var json = PinnedFile.Serialize(original);
        var loaded = PinnedFile.Parse(json);

        Assert.Equal(2, loaded.Count);
        var first = Assert.IsType<ReservedEntry>(loaded[0]);
        Assert.Equal(732, first.ID);
        Assert.Equal("/Wagon/Something", first.Path);
    }

    [Fact]
    public void Parse_MixedEntries_RoundTrips()
    {
        var original = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/Cooker", "100", new[] { "VRC.Udon.UdonBehaviour" }),
            new ReservedEntry(732, "/Wagon/Something"),
            new LocalEntry(11, "/Scanner", "200", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var json = PinnedFile.Serialize(original);
        var loaded = PinnedFile.Parse(json);

        Assert.Equal(3, loaded.Count);
        var sorted = loaded.OrderBy(e => e.ID).ToList();
        Assert.IsType<LocalEntry>(sorted[0]);   // 10
        Assert.IsType<LocalEntry>(sorted[1]);   // 11
        Assert.IsType<ReservedEntry>(sorted[2]); // 732
    }

    [Fact]
    public void Serialize_SortsById()
    {
        var entries = new List<PinnedEntryBase>
        {
            new ReservedEntry(732, "/Z"),
            new LocalEntry(10, "/A", "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var json = PinnedFile.Serialize(entries);
        var idx10 = json.IndexOf("\"ID\": 10");
        var idx732 = json.IndexOf("\"ID\": 732");
        Assert.True(idx10 < idx732, "ID 10 should appear before ID 732");
    }

    [Fact]
    public void Parse_EscapedPath_RoundTrips()
    {
        var original = new List<PinnedEntryBase>
        {
            new LocalEntry(10, "/Path\\With\"Quotes", "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var json = PinnedFile.Serialize(original);
        var loaded = PinnedFile.Parse(json);

        var entry = Assert.IsType<LocalEntry>(loaded.Single());
        Assert.Equal("/Path\\With\"Quotes", entry.Path);
    }

    [Fact]
    public void ParsePartnerJson_ParsesIdPathMapping()
    {
        var json = @"{""10"":""/Cooker"", ""11"":""/Scanner""}";
        var result = PinnedFile.ParsePartnerJson(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("/Cooker", result[10]);
        Assert.Equal("/Scanner", result[11]);
    }

    [Fact]
    public void ParsePartnerJson_EscapedQuotesInPath()
    {
        // パスにエスケープされた引用符が含まれるケース
        var json = @"{""10"":""/Path\\With\""Quotes""}";
        var result = PinnedFile.ParsePartnerJson(json);

        Assert.Single(result);
        Assert.Equal("/Path\\With\"Quotes", result[10]);
    }
}
