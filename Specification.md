
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
* 複数のトップレベル項目を専用フォルダへ集約
* GUID競合ファイルの事前検査と自動除外
* 上書き対象のバックアップおよび復元
* JUIによる操作・警告のUnity Console出力

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

### ドラッグ＆ドロップ領域

通常時は暗色で表示する。

有効な `.unitypackage` を領域上へドラッグしている間は明色へ変更し、ドロップ可能であることを視覚的に示す。対応していないファイルは受け付けない。

### 入力クリア

UnityPackage入力欄に「クリア」ボタンを設置する。

押下時は以下を初期化する。

* UnityPackage入力パス
* Import内容一覧
* 読み込みエラー
* トップレベル項目の集約設定と専用フォルダ名
* 内容一覧のスクロール位置

Import先と保存済みのDefault設定は維持する。

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

### 複数のトップレベル項目

UnityPackageの `Assets` 直下にフォルダまたはファイルが2項目以上ある場合、次の確認を表示する。

> UnityPackage内に複数のフォルダ・ファイルがあります。一つのフォルダにまとめますか？

選択肢は以下とする。

* 「まとめる」
* 「そのまま」

「まとめる」を選択した場合、現在のImport先直下へ専用フォルダを1階層追加し、その配下へPackage内容を配置する。

専用フォルダ名の初期値は、読み込んだ `.unitypackage` の拡張子を除いたファイル名とする。ユーザーはImport実行前に任意の名前へ変更できる。

専用フォルダ名が10文字以上の場合は、フォルダ名が長いことをUI上のテキストで注意する。

---

## 5. デフォルトImport先

JUI上でデフォルトのImport先を設定可能とする。

例:

```text
Assets/_JUIImport/
```

設定値はUnity Editor終了後も保持する。

保存には `EditorUserSettings` を使用する。

設定値はプロジェクト単位で保持し、別のUnityプロジェクトへ同じImport先や通知設定を引き継がない。

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

### ツリー表示

内容一覧は `Assets` をルートとしたフォルダ階層形式で表示する。親子関係は罫線で示す。

以下の操作に対応する。

* フォルダの展開・折りたたみ
* フォルダ単位で配下を一括選択・解除
* 配下の一部だけが選択されているフォルダの中間選択状態
* 「すべてON」「すべてOFF」
* 「すべて展開」「すべて折りたたむ」

### 読込時のGUID競合検査

UnityPackageがJUIへ追加された時点で、各ファイルの `.meta` GUIDを現在のプロジェクトと照合する。

既存AssetとGUIDが競合するファイルがあった場合は、以下の処理を行う。

* 該当ファイルのImportチェックを自動的に外す
* 内容一覧の該当行を薄い黄色でハイライトする
* ファイル名の先頭に警告記号 `⚠` を表示する
* マウスオーバー時に競合する既存Assetのパスを表示する
* Package内パス、GUID、既存AssetパスをUnity Consoleへ警告出力する

自動除外後も、ユーザーは該当ファイルを手動で再選択できる。

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

競合警告ダイアログには検出件数のみ表示し、対象パス、GUIDなどの詳細はUnity Consoleへ出力する。

### 上書き時の選択

既存ファイルが上書き対象の場合、以下の選択肢を表示する。

* 「バックアップして上書き」
* 「キャンセル」
* 「バックアップしないでインポートする」

「バックアップしないでインポートする」を選択した場合、バックアップを作成せずにAssetを上書きする。この場合、JUIの復元機能では元のファイルへ戻せない。

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

### 長いTARエントリ名

通常のTAR `name` フィールド上限を超えるエントリ名を切り詰めずに解析する。

以下の形式に対応する。

* GNU LongLink
* POSIX PAX拡張ヘッダーの `path`
* USTARの `prefix` と `name`

拡張ヘッダーから取得した名前は次の実エントリへ適用し、指定Import先へ同じ相対パスで展開する。

### Import前のパス長検査

Import計画作成後、実際のファイル書き込みや上書き確認より前に全出力パスを検査する。

以下はエラーとしてImportを停止する。

* OSの絶対パス上限を超えるパス
* ファイル名またはフォルダ名単位の上限を超えるパス
* OSで使用できない文字や末尾表現を含むパス
* `Assets` 外を指すパス

Windowsの従来の260文字境界を超えるパス、およびUnityで互換性に注意が必要な非常に長いAssetパスは警告対象とする。警告の詳細はUnity Consoleへ出力し、ユーザーが続行またはキャンセルを選択できるようにする。

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

---

## 17. バックアップと復元

### バックアップ作成

「バックアップして上書き」を選択した場合、上書き対象のAssetおよび対応する `.meta` をImport開始前に複製する。

バックアップはUnityプロジェクト直下の `JUI_BAK` へ、実行日時ごとのフォルダに分けて保存する。

```text
UnityProject/
└ JUI_BAK/
  └ yyyyMMdd_HHmmss_fff/
    └ Assets/
      └ ...
```

JUIウィンドウに「バックアップフォルダを開く」ボタンを設置し、押下時に `JUI_BAK` をファイルブラウザーで開く。

### バックアップからの復元

JUIウィンドウに「バックアップから復元する」ボタンを設置する。

押下後、ユーザーが `JUI_BAK` 内のバックアップ世代を選択し、次の確認を表示する。

> 本当に復元しますか？

確認後、バックアップ内のAssetおよび `.meta` を元の `Assets/...` へ上書きコピーし、UnityのAssetDatabaseを更新する。

復元処理も現在のファイルを上書きするため、選択したバックアップ世代と対象ファイル数を事前に表示する。

---

## 18. Unity Consoleへの出力

JUIが行った操作および警告は、`[JUI]` プレフィックス付きでUnity Consoleへ出力する。

主な出力対象は以下とする。

* UnityPackageの読み込みと入力クリア
* Importの開始、完了、失敗
* トップレベル項目の集約選択
* GUID競合による自動除外
* Import先の競合内容と上書き対象
* バックアップの作成
* バックアップからの復元
* Default Import先と通常Import通知の設定変更
* JUIを使用しない通常UnityPackage Importの検出

UI上の警告内容もConsoleへミラー出力し、ダイアログに詳細を表示しない場合でも確認できるようにする。

---

## 19. Import完了時の状態

Importが正常に完了した場合は、次の入力状態をクリアする。

* UnityPackage入力欄
* Import内容一覧
* 読み込みエラー
* トップレベル項目の集約設定と専用フォルダ名
* 内容一覧のスクロール位置

Import先および保存済みのDefault設定は維持する。

---

## 20. 展開後サイズと圧縮倍率

UnityPackage解析時に以下を算出し、JUI上へ表示する。

* TAR内の通常ファイルを合計した展開後サイズ
* 展開後サイズを元の `.unitypackage` ファイルサイズで割った圧縮倍率

展開後サイズの集計対象には原則として以下を含む。

* `asset`
* `asset.meta`
* `pathname`
* `preview.png`
* その他Package内部に存在する通常ファイル

GNU LongLinkやPAXなどの制御用拡張ヘッダー、およびディレクトリエントリは展開後サイズへ含めない。

### 警告表示

展開後サイズが設定された警告値以上の場合、赤色テキストで注意を表示する。

圧縮倍率が設定された警告値以上の場合も、独立した赤色テキストで注意を表示する。

両方が警告値以上の場合は、展開後サイズと圧縮倍率についてそれぞれ個別に警告する。警告内容はUnity Consoleにも出力する。

### JUI Settings

JUI Settingsへ以下を追加する。

```text
展開後サイズ警告値: [512] MB
展開倍率警告値: [10] 倍
```

各設定値は1以上の整数とし、`EditorUserSettings`へ保存する。JUI終了後もプロジェクト単位で保持する。

## 【更新記録】


### 2026-08-21

* Import完了時のUnityPackage入力クリアを追加
* 上書き時のバックアップ有無選択を追加
* バックアップフォルダ表示および復元機能を追加
* JUI操作と警告のUnity Console出力を追加
* 競合ダイアログを件数表示とし、詳細をConsoleへ出力
* 内容一覧をツリー・罫線表示へ変更
* 複数トップレベル項目の専用フォルダ集約を追加
* D&D領域の通常時・ドラッグ時の色変更を追加
* UnityPackage読込時のGUID競合検査、自動除外、黄色ハイライトを追加
* UnityPackage入力の手動クリアボタンを追加
* UnityPackageの展開後サイズ、圧縮倍率、設定可能な警告値を追加
