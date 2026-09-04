# PGAssetTool

English version: [README.md](README.md)

ドット絵調シューターの Unity アセットデータを解析・改変するための Windows 向けツールです。
AssetBundle と `.assets` を読み取り、多数のバンドルに散らばったアセット同士の参照を解決して
1つのアイテムに紐づくデータをツリーとして提示し、テクスチャの差し替えを宣言的な MOD パックとして
適用します。

## 背景

アイテムのデータは数百のバンドルに分散しています。1つの武器のモデル・マテリアル・アイコン・
スキン・エフェクト・表示名はそれぞれ別の場所にあり、しかもそれらは単一の仕組みではなく
3種類の異なる方法で結び付いています。

| 参照の種類 | 例 |
| --- | --- |
| `PPtr` によるバイナリ参照 | `Material` → `Texture2D` |
| 実行時に解決される文字列パス | `"Weapons/Weapon25"` を保持するフィールド |
| 命名規則 | `Weapon687` ⇔ `Ray687` |

`PPtr` だけを追うとツリーは途中で切れてしまいます。本ツールは3種類すべてを追跡します。

## 状況

開発初期です。現時点の実装状況は以下の通りです。

| フェーズ | 範囲 | 状態 |
| --- | --- | --- |
| P0 | 基盤構築、インストール先の検出、AB と `.assets` の読み取り | 完了 |
| P1 | CLI でのアイテムツリー表示：カタログ、関連名前空間、アイコン | 進行中 |
| P2 | テクスチャの抽出と差し替え、MOD パック、バックアップと有効/無効切り替え | 予定 |
| P3 | Avalonia GUI（テクスチャ・モデルプレビュー付き） | 予定 |
| P4 | `.assets` への書き込み、スプライトアトラス | 予定 |

## 必要なもの

- Windows x64
- .NET 10 SDK（ビルド時）

Unity のエンジン標準クラスの型定義である `classdata.tpk` は `third_party/` に同梱しており、
ビルド時に実行ファイルの隣へコピーされます。`*_Data` 配下のファイルは TypeTree を持たずに
ビルドされているため読み取りに必要です。Unity 標準クラスの定義のみを含むもので、ゲーム独自の
コードとは無関係です。[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## ビルド

```
dotnet build -c Release
```

## 使い方

リポジトリのルートで `dotnet run` を使うと、出力先のパスを打たずに実行できます。

```
dotnet run --project src/PGAssetTool.Cli -- <コマンド>
```

ビルドされた実行ファイルを直接呼びたい場合は
`src/PGAssetTool.Cli/bin/Release/net10.0-windows/win-x64/pgassettool.exe` にあります。

| コマンド | 内容 |
| --- | --- |
| `info` | 検出したインストール先とバージョンを表示します。 |
| `weapons [<絞り込み>]` | 武器を一覧表示します。スラッグ・タグ・プレハブ名で絞り込めます。 |
| `show <武器>` | 武器1件と、それが参照している全てを表示します。番号・プレハブ名（`Weapon25`）・スラッグ（`Beretta`）のいずれでも指定できます。 |

```
dotnet run --project src/PGAssetTool.Cli -- info
dotnet run --project src/PGAssetTool.Cli -- weapons crystal
dotnet run --project src/PGAssetTool.Cli -- show 25
```

`--game <ディレクトリ>` には想定の構成を持つ任意の場所を指定できます。実際のインストール先では
なくコピーしたデータを対象にして安全に検証できます。`--language <バンドル>` で名前を読み取る
ローカライズバンドルを選べます（既定は `l_en-gb`）。

## ライセンス

MIT です。[LICENSE](LICENSE) を参照してください。

本プロジェクトはゲームのデータを一切同梱しません。MOD パックは変更内容を宣言的に記述するのみで、
利用者自身のインストール環境にあるアセットを参照します。
