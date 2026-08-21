
# JustUnitypackageImporter2Folder 暫定仕様

## 1. 概要

**名称:** JustUnitypackageImporter2Folder
**通称:** JUI

UnityPackage形式のファイルをインポートする際に、**UnityPackage内部で指定されている配置先ではなく、任意のフォルダ配下へ配置できるUnity Editor拡張**。

通常のUnityPackage Importを完全に置き換えるものではなく、**JUI経由でインポートした場合のみ保存先変更機能を適用する**。

---

## 2. 主機能

* UnityPackageのインポート先フォルダを任意に変更
* UnityPackage内のファイル一覧をImport前に表示
* ファイル単位でImport対象を選択
* デフォルトImport先の設定・保持
* Import先の既存ファイル・フォルダとの競合確認
* JUIを使用しない通常Importを検出した場合の通知

---

## 3. JUIウィンドウ

Unity Editor上の以下から起動する。

`Tools > JUI`

JUIウィンドウでは以下を操作可能とする。

### UnityPackage指定

以下の2方式に対応。

* `.unitypackage` ファイルをJUIウィンドウへドラッグ＆ドロップ
* ファイル選択ダイアログから `.unitypackage` を指定

Package指定後、内部を解析してImport内容を表示する。

---

## 4. Import先設定

### 「Import先を変更する」

チェックボックスを設置する。

#### ON

指定されたフォルダを基準にImportする。

例:

```text
元Package

Assets/
├ Avatar/
├ Materials/
└ Textures/
```

Import先:

```text
Assets/_AvatarArchive/Alice/
```

結果:

```text
Assets/_AvatarArchive/Alice/
├ Avatar/
├ Materials/
└ Textures/
```

Package内の相対的なフォルダ構造は維持する。

#### OFF

UnityPackage内部に記録された元のパスへ通常通りImportする。

```text
Assets/Avatar/
Assets/Materials/
Assets/Textures/
```

など、Package作成時の配置先に従う。

---

## 5. デフォルトImport先

JUI上でデフォルトのImport先を設定可能とする。

例:

```text
Assets/_JUIImport/
```

設定値はUnity Editor終了後も保持する。

保存方法は以下を候補とする。

* `EditorPrefs`
* JUI専用Settings Asset

初期版では `EditorPrefs` でも十分とする。

---

## 6. Package内容一覧

UnityPackageを読み込んだ時点で、Package内部のファイル一覧を表示する。

例:

```text
☑ Assets/Alice/Alice.prefab
☑ Assets/Alice/Materials/Body.mat
☑ Assets/Alice/Textures/Body.png
☑ Assets/Alice/Textures/Face.png
☐ Assets/Alice/SampleScene.unity
☐ Assets/lilToon/...
```

各項目にチェックボックスを付与する。

### チェックON

Import対象。

### チェックOFF

Import対象から除外。

フォルダ単位の一括ON/OFFについては初期版では必須としないが、実装可能であれば追加する。

---

## 7. 除外時の扱い

ユーザーが依存Assetを除外した場合、JUIは基本的にその判断を尊重する。

例:

```text
Prefab
 └ Material
     └ Texture
```

でMaterialをImport対象外にした場合、Prefab側でMissing参照が発生する可能性がある。

初期版では、

> Import対象から除外したAssetによる参照切れは自動修復しない。

ものとする。

将来的にGUID参照を解析し、依存Asset除外時に警告を表示する機能は追加可能。

---

## 8. Import先の競合確認

「これでインポートする」を実行する前に、Import予定パスと既存Assetを比較する。

以下を検出対象とする。

### 同名ファイル

例:

```text
Import予定:
Assets/_AvatarArchive/Alice/Body.mat

既存:
Assets/_AvatarArchive/Alice/Body.mat
```

警告を表示する。

### 同名フォルダ

Import先に同名フォルダが存在する場合も通知対象とする。

ただし、フォルダが存在するだけでImport不能とはしない。

### GUID競合

可能であれば `.meta` を解析し、以下も検出する。

```text
同一Path + 同一GUID
同一Path + 異なるGUID
異なるPath + 同一GUID
```

特に、

```text
異なるPath + 同一GUID
```

はUnity上でAsset識別の競合となる可能性があるため、強めの警告対象とする。

---

## 9. Import実行

JUIウィンドウ下部に、

**「これでインポートする」**

ボタンを設置する。

押下時に、

1. 選択されたAssetのみ抽出
2. Import先変更ONの場合はpathnameを書き換え
3. 元の `.meta` およびGUIDを維持
4. 一時UnityPackageを生成
5. Unity標準のPackage Import処理を実行
6. 一時ファイルを削除

という順序で処理する。

元UnityPackage自体は変更しない。

---

## 10. UnityPackage内部の処理

UnityPackage内部の各Assetについて、

```text
GUID/
├ asset
├ asset.meta
└ pathname
```

を解析する。

Import先変更時は主に `pathname` を変更する。

例:

```text
Assets/Alice/Materials/Body.mat
```

↓

```text
Assets/_AvatarArchive/Alice/Alice/Materials/Body.mat
```

`asset.meta` に記録されたGUIDは維持する。

これにより、Prefab・Material・Texture等のGUIDベースの参照関係を可能な限り保持する。

---

## 11. 通常UnityPackage Import検知

JUIを使用しない通常のUnityPackage Importについては、**処理を阻止・変更しない**。

UnityのPackage Import開始イベントを検出し、通知のみ行う。

表示例:

> JUIを使用していないため、インポート先は変更されません。

ユーザーがOKを押した後は、そのままUnity標準Importを継続する。

### JUI経由の場合

JUI自身が開始したImportでは通知を表示しない。

内部フラグ等で判別する。

---

## 12. 通常Import通知設定

JUI Settingsに以下を設置する。

```text
☑ JUIを使用しないUnityPackage Import時に通知する
```

初期値:

```text
ON
```

OFFにした場合、通常Import時の通知を行わない。

---

## 13. JUIが行わない処理

初期仕様では以下は対象外とする。

* Unity標準Importのキャンセル
* Unity標準Importの強制的なJUIへの転送
* Shader内部名の自動変更
* Script namespaceの自動変更
* Asset依存関係の自動修復
* Missing参照の自動修正
* 外部Package依存関係の自動解決
* UPM PackageのImport先変更

JUIは基本的に、

**「UnityPackage内の配置パスを安全に変更してImportする」**

ことに限定する。

---

## 14. 想定UI

```text
────────────────────────────
 JustUnitypackageImporter2Folder
────────────────────────────

UnityPackage
[Alice_v1.4.unitypackage] [...]

またはここへD&D

☑ Import先を変更する

Import先
[Assets/_AvatarArchive/Alice/] [...]

Default:
[Assets/_AvatarArchive/]
[Defaultに設定]

────────────────────────────
Import内容
────────────────────────────

☑ Assets/Alice/Alice.prefab
☑ Assets/Alice/Materials/Body.mat
☑ Assets/Alice/Textures/Body.png
☐ Assets/Alice/SampleScene.unity
☐ Assets/lilToon/...

────────────────────────────

⚠ 競合:
Assets/_AvatarArchive/Alice/Body.mat

[これでインポートする]
────────────────────────────
```

---

## 15. 暫定開発方針

初期版では以下を優先する。

1. UnityPackage解析
2. ファイル一覧表示
3. Import対象選択
4. Import先リマップ
5. GUID維持
6. 競合警告
7. デフォルトImport先保存
8. 通常Import検知通知

高度な依存関係解析やShader競合解析については、基本機能完成後の追加機能とする。

---

## 16. 現時点での実現性

主要機能はUnity Editor拡張として実装可能。

特にJUIの中核となる、

```text
UnityPackage読込
↓
内容確認
↓
対象選択
↓
pathname変更
↓
一時Package生成
↓
Import
```

の構成は技術的に成立する。

通常Importについても、

**「JUIを使用していないためインポート先は変更されない」と通知するだけ**

であれば、Unity標準処理を妨害せず実装可能。

したがって、本暫定仕様であれば**開発着手可能な状態**と判断する。

## 【更新記録】





