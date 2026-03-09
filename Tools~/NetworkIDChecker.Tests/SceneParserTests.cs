// SceneParser の単体テスト
//
// テスト観点:
// - Parse: ブロック形式、空配列インライン形式、混在、セクションなしのケース
// - BuildSection: ID 昇順ソートの確認
// - ReplaceSection: ブロック形式・インライン形式の差し替え、セクションなしで例外

using System.Collections.Generic;
using VRCNetworkIDGuard;
using Xunit;

public class SceneParserTests
{
    [Fact]
    public void Parse_BlockFormat_ParsesEntries()
    {
        var content = SceneWith(
            "  - gameObject: {fileID: 587073134}\n" +
            "    ID: 10\n" +
            "    SerializedTypeNames:\n" +
            "    - VRC.Udon.UdonBehaviour\n" +
            "  - gameObject: {fileID: 994346170}\n" +
            "    ID: 12\n" +
            "    SerializedTypeNames:\n" +
            "    - VRC.Udon.UdonBehaviour\n");

        var entries = SceneParser.Parse(content);

        Assert.Equal(2, entries.Count);
        Assert.Equal(10, entries[0].ID);
        Assert.Equal("587073134", entries[0].GameObjectFileID);
        Assert.Equal(12, entries[1].ID);
        Assert.Equal("994346170", entries[1].GameObjectFileID);
    }

    [Fact]
    public void Parse_EmptyInlineArray_ReturnsEmptyList()
    {
        var content = SceneWithEmptyInline();
        var entries = SceneParser.Parse(content);
        Assert.Empty(entries);
    }

    [Fact]
    public void Parse_BlockFormatWithEmptyInlineElsewhere_ParsesBlockEntries()
    {
        var content =
            "SomeOtherComponent:\n" +
            "  NetworkIDs: []\n" +
            "  m_SomeField: 0\n" +
            "VRC_SceneDescriptor:\n" +
            "  m_UseHDR: 0\n" +
            "  NetworkIDs:\n" +
            "  - gameObject: {fileID: 587073134}\n" +
            "    ID: 10\n" +
            "    SerializedTypeNames:\n" +
            "    - VRC.Udon.UdonBehaviour\n" +
            "  m_TagString: Untagged\n";

        var entries = SceneParser.Parse(content);
        Assert.Single(entries);
        Assert.Equal(10, entries[0].ID);
    }

    [Fact]
    public void Parse_EmptyBlockFormat_ReturnsEmptyList()
    {
        // ブロック形式だがエントリなし: "NetworkIDs:\n" の直後に別セクションが続くケース。
        // インライン空配列 (NetworkIDs: []) とは異なる形式。
        var content = "PipetteSetting:\n  m_UseHDR: 0\n  NetworkIDs:\n  m_TagString: Untagged\n";
        var entries = SceneParser.Parse(content);
        Assert.Empty(entries);
    }

    [Fact]
    public void Parse_MalformedEntry_MissingID_Throws()
    {
        // gameObject 行はあるが ID 行がない不完全なエントリ。
        // Build() で検出されて例外が発生することを確認する。
        var content = SceneWith(
            "  - gameObject: {fileID: 587073134}\n" +
            "    SerializedTypeNames:\n" +
            "    - VRC.Udon.UdonBehaviour\n");

        Assert.Throws<System.InvalidOperationException>(() => SceneParser.Parse(content));
    }

    [Fact]
    public void Parse_MissingSection_Throws()
    {
        var content = "PipetteSetting:\n  m_UseHDR: 0\n";
        Assert.Throws<System.InvalidOperationException>(() => SceneParser.Parse(content));
    }

    [Fact]
    public void BuildSection_SortsById()
    {
        var entries = new List<NetworkIDEntry>
        {
            new NetworkIDEntry(20, "200", new[] { "VRC.Udon.UdonBehaviour" }),
            new NetworkIDEntry(10, "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };

        var section = SceneParser.BuildSection(entries);
        var idx10 = section.IndexOf("ID: 10");
        var idx20 = section.IndexOf("ID: 20");
        Assert.True(idx10 < idx20, "ID 10 should appear before ID 20");
    }

    [Fact]
    public void ReplaceSection_BlockFormat_ReplacesContent()
    {
        var original = SceneWith(
            "  - gameObject: {fileID: 999999}\n" +
            "    ID: 99\n" +
            "    SerializedTypeNames:\n" +
            "    - VRC.Udon.UdonBehaviour\n");

        var newEntries = new List<NetworkIDEntry>
        {
            new NetworkIDEntry(10, "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };
        var newSection = SceneParser.BuildSection(newEntries);
        var result = SceneParser.ReplaceSection(original, newSection);

        Assert.Contains("ID: 10", result);
        Assert.DoesNotContain("ID: 99", result);
    }

    [Fact]
    public void ReplaceSection_EmptyInline_ReplacesContent()
    {
        var original = SceneWithEmptyInline();

        var newEntries = new List<NetworkIDEntry>
        {
            new NetworkIDEntry(10, "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };
        var newSection = SceneParser.BuildSection(newEntries);
        var result = SceneParser.ReplaceSection(original, newSection);

        Assert.Contains("ID: 10", result);
        Assert.DoesNotContain("[]", result);
    }

    [Fact]
    public void ReplaceSection_EmptyInlineBeforeBlock_ReplacesBlockNotInline()
    {
        // 別コンポーネントの "NetworkIDs: []" がブロック形式より先に出現するケース。
        // ReplaceSection はブロック形式を優先的に差し替えるべき。
        var content =
            "SomeOtherComponent:\n" +
            "  NetworkIDs: []\n" +
            "  m_SomeField: 0\n" +
            "VRC_SceneDescriptor:\n" +
            "  m_UseHDR: 0\n" +
            "  NetworkIDs:\n" +
            "  - gameObject: {fileID: 999999}\n" +
            "    ID: 99\n" +
            "    SerializedTypeNames:\n" +
            "    - VRC.Udon.UdonBehaviour\n" +
            "  m_TagString: Untagged\n";

        var newEntries = new List<NetworkIDEntry>
        {
            new NetworkIDEntry(10, "100", new[] { "VRC.Udon.UdonBehaviour" }),
        };
        var newSection = SceneParser.BuildSection(newEntries);
        var result = SceneParser.ReplaceSection(content, newSection);

        // ブロック形式が差し替えられていること
        Assert.Contains("ID: 10", result);
        Assert.DoesNotContain("ID: 99", result);
        // インライン空配列はそのまま残っていること
        Assert.Contains("NetworkIDs: []", result);
    }

    [Fact]
    public void ReplaceSection_MissingSection_Throws()
    {
        var content = "PipetteSetting:\n  m_UseHDR: 0\n";
        Assert.Throws<System.InvalidOperationException>(
            () => SceneParser.ReplaceSection(content, "  NetworkIDs:\n"));
    }

    // Helpers
    private static string SceneWith(string networkIDsBlock)
    {
        return $"PipetteSetting:\n  m_UseHDR: 0\n  NetworkIDs:\n{networkIDsBlock}\n  m_TagString: Untagged\n";
    }

    private static string SceneWithEmptyInline()
    {
        return "PipetteSetting:\n  m_UseHDR: 0\n  NetworkIDs: []\n  m_TagString: Untagged\n";
    }
}
