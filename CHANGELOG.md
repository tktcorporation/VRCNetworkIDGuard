# Changelog

## 0.3.0

### Minor Changes

- [#4](https://github.com/tktcorporation/VRCNetworkIDGuard/pull/4) [`a1f32b7`](https://github.com/tktcorporation/VRCNetworkIDGuard/commit/a1f32b7fb6a49f4bebdb38134e0484e545a7e771) Thanks [@tktcorporation](https://github.com/tktcorporation)! - Tools/ を Tools~/ にリネームして VPM パッケージに CLI を同梱、dotnet tool としても配布可能に

## 0.2.0

### Minor Changes

- [#1](https://github.com/tktcorporation/VRCNetworkIDGuard/pull/1) [`910419a`](https://github.com/tktcorporation/VRCNetworkIDGuard/commit/910419ae4f8760ba76ad75baa709ecd379e54800) Thanks [@tktcorporation](https://github.com/tktcorporation)! - NetworkID 固定機能を VPM パッケージとして初期構築

- [#3](https://github.com/tktcorporation/VRCNetworkIDGuard/pull/3) [`55cd363`](https://github.com/tktcorporation/VRCNetworkIDGuard/commit/55cd3632c35b941651cc031978fbc501181fb20e) Thanks [@tktcorporation](https://github.com/tktcorporation)! - Tools/ を Tools~/ にリネームして VPM パッケージに CLI を同梱、dotnet tool としても配布可能に

## [0.1.0] - 2026-03-08

### Added

- NetworkID 固定 (pinning) 機能
- シーンファイルの NetworkID 変更検知
- ビルド前の自動検証 (VRChat SDK / Unity 標準ビルド / Play モード)
- 予約 ID 衝突検知と自動再割り当て
- Pinned からのシーン復元
- セーブデータ影響検知 (UdonSynced フィールド)
- 外部環境の JSON インポート機能
- CLI ツール (check / update / restore / reassign / import-pinned / show / show-pinned)
- Project Settings UI でパス設定
