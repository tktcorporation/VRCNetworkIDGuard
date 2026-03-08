# VRC NetworkID Guard

VRChat ワールドの NetworkID 変更を検知・防止し、プレイヤーのパーシステンス（セーブデータ）消失を防ぐツールです。

## 背景

VRChat ワールドでは、シーン内オブジェクトに割り当てられる **NetworkID** がパーシステンス（Udon のセーブデータ）のキーとして使われます。Unity エディタでの操作やシーンの変更により NetworkID が意図せず変わると、プレイヤーのセーブデータが消失します。

このツールは、NetworkID の割り当てを「Pinned ファイル」として記録し、変更を検知・復元することで、セーブデータ消失を防ぎます。

## 機能

- **ビルド前チェック** — VRChat アップロード時に NetworkID の変更を自動検知し、危険な変更があればダイアログで警告
- **CI チェック** — PR/push 時に GitHub Actions で NetworkID の整合性を検証
- **復元** — 意図しない変更を Pinned ファイルから自動復元
- **予約 ID 管理** — 複数環境（自社・先方）間での NetworkID 衝突を検知・解消
- **セーブデータ影響表示** — 変更された NetworkID がどのパーシステンス変数に影響するかを表示

## インストール

### VPM パッケージ（Unity Editor 拡張）

VRChat Creator Companion (VCC) から追加するか、`Packages/vpm-manifest.json` に直接追記:

```json
{
  "dependencies": {
    "com.tktcorporation.vrc-network-id-guard": "https://github.com/tktcorporation/VRCNetworkIDGuard.git"
  }
}
```

### CLI（dotnet tool）

```bash
dotnet tool install -g tktco.VRCNetworkIDGuard
```

## 使い方

### Unity Editor

パッケージをインストールすると、VRChat のアップロード前に自動で NetworkID チェックが実行されます。

- 危険な変更がある場合 → ダイアログが表示され、復元 or 中止を選択可能
- Settings は `Edit > Project Settings > NetworkID Guard` から設定

### CLI

```bash
# Pinned ファイルとシーンを比較（CI 用）
networkid-guard check

# シーンから Pinned ファイルを更新
networkid-guard update

# Pinned からシーンの NetworkID を復元
networkid-guard restore

# 予約 ID との衝突を再割り当てで解消
networkid-guard reassign

# 先方の JSON をインポートして Pinned ファイルを生成
networkid-guard import-pinned <json-path>

# 現在の状態を表示
networkid-guard show
networkid-guard show-pinned
```

#### オプション

```bash
--scene=<path>    # シーンファイルのパス（デフォルト: 自動検出）
--pinned=<path>   # Pinned ファイルのパス（デフォルト: 自動検出）
--yes, -y         # 確認プロンプトをスキップ（CI 用）
--force           # 危険な変更を含む update を許可
```

## Pinned ファイル

`networkids_pinned.json` に NetworkID の割り当てを記録します:

- **ローカルエントリ** (`gameObject` あり): シーン内で解決済み。復元と変更検知に使用
- **予約エントリ** (`gameObject` なし): 先方環境のみに存在する ID。衝突検知に使用

## ライセンス

[MIT](LICENSE)
