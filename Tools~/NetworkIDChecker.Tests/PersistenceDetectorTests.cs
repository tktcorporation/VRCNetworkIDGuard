// PersistenceDetector の単体テスト
//
// テスト観点:
// - ExtractGameObjectToGuids: 直接 MonoBehaviour からの GUID 抽出、非 Udon のスキップ
// - ExtractStrippedObjects: stripped オブジェクトのソース情報抽出
// - BuildGuidToPathMap: .meta ファイルからの GUID→パス解決
// - DetectUdonSynced: UdonSynced あり/なしの検出
// - Detect: 直接オブジェクトと stripped オブジェクトの統合テスト

using System.Collections.Generic;
using VRCNetworkIDGuard;
using Xunit;

public class PersistenceDetectorTests
{
    [Fact]
    public void ExtractGameObjectToGuids_DirectMonoBehaviour_ReturnsMapping()
    {
        var sceneContent =
            "--- !u!114 &100001\n" +
            "MonoBehaviour:\n" +
            "  m_GameObject: {fileID: 200001}\n" +
            "  m_Script: {fileID: 11500000, guid: 45115577ef41a5b4ca741ed302693907, type: 3}\n" +
            "  programSource: {fileID: 11400000, guid: aaa111bbb222ccc333ddd444eee555ff, type: 2}\n" +
            "--- !u!114 &100002\n" +
            "MonoBehaviour:\n" +
            "  m_GameObject: {fileID: 200002}\n" +
            "  m_Script: {fileID: 11500000, guid: 45115577ef41a5b4ca741ed302693907, type: 3}\n" +
            "  programSource: {fileID: 11400000, guid: bbb222ccc333ddd444eee555fff66600, type: 2}\n";

        var result = PersistenceDetector.ExtractGameObjectToGuids(sceneContent);

        Assert.Equal(2, result.Count);
        Assert.Single(result["200001"]);
        Assert.Equal("aaa111bbb222ccc333ddd444eee555ff", result["200001"][0]);
        Assert.Single(result["200002"]);
        Assert.Equal("bbb222ccc333ddd444eee555fff66600", result["200002"][0]);
    }

    [Fact]
    public void ExtractGameObjectToGuids_SkipsNonUdonMonoBehaviour()
    {
        // programSource がない MonoBehaviour は非 Udon なのでスキップされるべき
        var sceneContent =
            "--- !u!114 &100001\n" +
            "MonoBehaviour:\n" +
            "  m_GameObject: {fileID: 200001}\n" +
            "  m_Script: {fileID: 11500000, guid: 45115577ef41a5b4ca741ed302693907, type: 3}\n" +
            "  m_Name: SomeComponent\n" +
            "--- !u!114 &100002\n" +
            "MonoBehaviour:\n" +
            "  m_GameObject: {fileID: 200002}\n" +
            "  m_Script: {fileID: 11500000, guid: 45115577ef41a5b4ca741ed302693907, type: 3}\n" +
            "  programSource: {fileID: 11400000, guid: aaa111bbb222ccc333ddd444eee555ff, type: 2}\n";

        var result = PersistenceDetector.ExtractGameObjectToGuids(sceneContent);

        Assert.Single(result);
        Assert.True(result.ContainsKey("200002"));
        Assert.False(result.ContainsKey("200001"));
    }

    [Fact]
    public void ExtractStrippedObjects_ReturnsSourceObjectAndPrefabGuid()
    {
        var sceneContent =
            "--- !u!1 &587073134 stripped\n" +
            "GameObject:\n" +
            "  m_CorrespondingSourceObject: {fileID: 4263809777859289560, guid: 560f7e5088cee314081030c0843a149f, type: 3}\n" +
            "  m_PrefabInstance: {fileID: 1285995826}\n";

        var result = PersistenceDetector.ExtractStrippedObjects(sceneContent);

        Assert.Single(result);
        Assert.True(result.ContainsKey("587073134"));
        Assert.Equal("560f7e5088cee314081030c0843a149f", result["587073134"].PrefabGuid);
        Assert.Equal("4263809777859289560", result["587073134"].SourceObjectFileID);
    }

    [Fact]
    public void BuildGuidToPathMap_ParsesMetaFiles()
    {
        var metaFiles = new List<(string path, string content)>
        {
            ("Assets/_c_r_5/Scripts/Foo.asset.meta",
             "fileFormatVersion: 2\nguid: aaa111bbb222ccc333ddd444eee555ff\n"),
            ("Assets/_c_r_5/Scripts/Bar.asset.meta",
             "fileFormatVersion: 2\nguid: bbb222ccc333ddd444eee555fff66600\n"),
        };

        var result = PersistenceDetector.BuildGuidToPathMap(metaFiles);

        Assert.Equal(2, result.Count);
        Assert.Equal("Assets/_c_r_5/Scripts/Foo.asset", result["aaa111bbb222ccc333ddd444eee555ff"]);
        Assert.Equal("Assets/_c_r_5/Scripts/Bar.asset", result["bbb222ccc333ddd444eee555fff66600"]);
    }

    [Fact]
    public void DetectUdonSynced_WithSyncedFields_ReturnsPersistenceInfo()
    {
        var assetPath = "Assets/_c_r_5/Scripts/UdonProgramSources/MoguUserData Udon C# Program Asset.asset";
        var assetContent =
            "some serialized data\n" +
            "UdonSharp.UdonSyncedAttribute, UdonSharp.Runtime\n" +
            "more data\n" +
            "UdonSharp.UdonSyncedAttribute, UdonSharp.Runtime\n" +
            "UdonSharp.UdonSyncedAttribute, UdonSharp.Runtime\n";

        var result = PersistenceDetector.DetectUdonSynced(assetPath, assetContent);

        Assert.True(result.HasSyncedFields);
        Assert.Equal("MoguUserData", result.ComponentName);
        Assert.Equal(3, result.SyncedFieldCount);
    }

    [Fact]
    public void DetectUdonSynced_WithoutSyncedFields_ReturnsNoImpact()
    {
        var assetPath = "Assets/_c_r_5/Scripts/UdonProgramSources/Clock Udon C# Program Asset.asset";
        var assetContent =
            "some serialized data\n" +
            "no synced attributes here\n";

        var result = PersistenceDetector.DetectUdonSynced(assetPath, assetContent);

        Assert.False(result.HasSyncedFields);
        Assert.Equal("Clock", result.ComponentName);
        Assert.Equal(0, result.SyncedFieldCount);
    }

    [Fact]
    public void Detect_IntegrationTest_ReturnsPersistenceInfoPerGameObject()
    {
        // シーン: 2つの GameObject に UdonBehaviour が直接アタッチされている
        var sceneContent =
            "--- !u!114 &100001\n" +
            "MonoBehaviour:\n" +
            "  m_GameObject: {fileID: 200001}\n" +
            "  m_Script: {fileID: 11500000, guid: 45115577ef41a5b4ca741ed302693907, type: 3}\n" +
            "  programSource: {fileID: 11400000, guid: aaa111bbb222ccc333ddd444eee555ff, type: 2}\n" +
            "--- !u!114 &100002\n" +
            "MonoBehaviour:\n" +
            "  m_GameObject: {fileID: 200002}\n" +
            "  m_Script: {fileID: 11500000, guid: 45115577ef41a5b4ca741ed302693907, type: 3}\n" +
            "  programSource: {fileID: 11400000, guid: bbb222ccc333ddd444eee555fff66600, type: 2}\n";

        var guidToPath = new Dictionary<string, string>
        {
            ["aaa111bbb222ccc333ddd444eee555ff"] = "Assets/Scripts/MoguUserData Udon C# Program Asset.asset",
            ["bbb222ccc333ddd444eee555fff66600"] = "Assets/Scripts/Clock Udon C# Program Asset.asset",
        };

        var assetContents = new Dictionary<string, string>
        {
            ["Assets/Scripts/MoguUserData Udon C# Program Asset.asset"] =
                "UdonSharp.UdonSyncedAttribute, UdonSharp.Runtime\nUdonSharp.UdonSyncedAttribute, UdonSharp.Runtime\n",
            ["Assets/Scripts/Clock Udon C# Program Asset.asset"] =
                "no synced fields\n",
        };

        var prefabContents = new Dictionary<string, string>();

        var result = PersistenceDetector.Detect(sceneContent, guidToPath, assetContents, prefabContents);

        Assert.Equal(2, result.Count);

        // MoguUserData — UdonSynced あり
        Assert.Single(result["200001"]);
        Assert.True(result["200001"][0].HasSyncedFields);
        Assert.Equal("MoguUserData", result["200001"][0].ComponentName);
        Assert.Equal(2, result["200001"][0].SyncedFieldCount);

        // Clock — UdonSynced なし
        Assert.Single(result["200002"]);
        Assert.False(result["200002"][0].HasSyncedFields);
        Assert.Equal("Clock", result["200002"][0].ComponentName);
    }

    [Fact]
    public void Detect_StrippedObject_ResolvesThroughPrefab()
    {
        // シーン: stripped オブジェクトがプレハブ経由で UdonBehaviour を持つ
        var sceneContent =
            "--- !u!1 &587073134 stripped\n" +
            "GameObject:\n" +
            "  m_CorrespondingSourceObject: {fileID: 300001, guid: 560f7e5088cee314081030c0843a149f, type: 3}\n" +
            "  m_PrefabInstance: {fileID: 1285995826}\n";

        var guidToPath = new Dictionary<string, string>
        {
            // プレハブ自体の GUID → パス
            ["560f7e5088cee314081030c0843a149f"] = "Assets/Prefabs/MyPrefab.prefab",
            // UdonBehaviour の programSource GUID → パス
            ["aaa111bbb222ccc333ddd444eee555ff"] = "Assets/Scripts/DishUserData Udon C# Program Asset.asset",
        };

        // プレハブ内に MonoBehaviour が存在し、sourceObjectFileID=300001 の GameObject を参照
        var prefabContents = new Dictionary<string, string>
        {
            ["Assets/Prefabs/MyPrefab.prefab"] =
                "--- !u!114 &400001\n" +
                "MonoBehaviour:\n" +
                "  m_GameObject: {fileID: 300001}\n" +
                "  m_Script: {fileID: 11500000, guid: 45115577ef41a5b4ca741ed302693907, type: 3}\n" +
                "  programSource: {fileID: 11400000, guid: aaa111bbb222ccc333ddd444eee555ff, type: 2}\n",
        };

        var assetContents = new Dictionary<string, string>
        {
            ["Assets/Scripts/DishUserData Udon C# Program Asset.asset"] =
                "UdonSharp.UdonSyncedAttribute, UdonSharp.Runtime\n",
        };

        var result = PersistenceDetector.Detect(sceneContent, guidToPath, assetContents, prefabContents);

        // stripped オブジェクトのシーン内 fileID で結果が返る
        Assert.Single(result);
        Assert.True(result.ContainsKey("587073134"));
        Assert.Single(result["587073134"]);
        Assert.True(result["587073134"][0].HasSyncedFields);
        Assert.Equal("DishUserData", result["587073134"][0].ComponentName);
        Assert.Equal(1, result["587073134"][0].SyncedFieldCount);
    }
}
