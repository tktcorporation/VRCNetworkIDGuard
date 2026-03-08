// テスト共通ヘルパー
//
// 複数のテストクラスで使われるファクトリメソッドを集約する。

using System.Collections.Generic;
using System.Linq;
using VRCNetworkIDGuard;

internal static class TestHelpers
{
    /// <summary>
    /// (id, fileId) タプルから NetworkIDEntry のリストを生成する。
    /// SerializedTypeNames は全エントリ共通で "VRC.Udon.UdonBehaviour" を設定する。
    /// </summary>
    internal static List<NetworkIDEntry> MakeSceneEntries(params (int id, string fileId)[] items)
    {
        return items.Select(i => new NetworkIDEntry(i.id, i.fileId, new[] { "VRC.Udon.UdonBehaviour" })).ToList();
    }
}
