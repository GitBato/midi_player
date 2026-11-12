# MIDI Player（WPF 版）

レトロな MIDI 音源（Roland SC-88Pro などの GS、ヤマハ XG、MT-32、TX7）を、MIDI インタフェース経由で鳴らすための
Windows 用 MIDI プレイヤーです。表示は最低限で、出口（MIDI OUT）ごとに 16ch のアナログ VU メーターが振れます。

## ダウンロード

[Releases](../../releases) から `MidiPlayerWpf-vX.Y.Z-win-x64.zip` をダウンロードし、中の `MidiPlayerWpf.exe` を実行してください。
.NET を入れていない PC でも動きます（.NET 込みの 1 ファイル版）。

署名していない exe なので、「Windows によって PC が保護されました」と出たら「詳細情報」→「実行」で起動できます。

## できること

- **読める形式**：MID（SMF）/ RCP・R36・G36（レコンポーザ。同じフォルダの CM6 も送る）/ SNG（Ballade）/
  MM2・MMC（マイクロミュージシャン）/ CM6 / ZIP の中の曲（Shift_JIS のファイル名も可）
- **DXX**（TX7 の音色ファイル）を開くと、32 音色バルクダンプを TX7 へ送る
- **出口の振り分け**：GS・XG・MT-32・その他の 4 つの MIDI OUT へ、曲の種類・トラック名／ポート番号・SysEx の宛先で自動で振り分ける
- **音源の自動判別**：各 MIDI OUT に問い合わせを送り、MIDI IN に来た返事で、つながっている音源を見分けて出口を決める
  （音源の MIDI OUT を PC の MIDI IN につないでおく必要があります）
- **再生**：ドラッグ＆ドロップですぐ再生、Space で再生／停止、Ctrl+O で開く、位置バーで移動
- **音色の転送表示**：SNG・CM6 の音色を MT-32 へ、DXX を TX7 へ送っているあいだは進み具合を表示

## ビルド

.NET 8 SDK が要ります。外部のパッケージ（NuGet）は使っていません（MIDI は Windows の winmm を直接呼んでいます）。

```
dotnet build -c Release
```

.NET 込みの 1 ファイル版：

```
dotnet publish MidiPlayerWpf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`v` で始まるタグ（例：`v1.0.0`）を push すると、GitHub Actions が 1 ファイル版を作って Releases に載せます。

## 構成

- `MidiCore`：読み込み（Smf / Rcp / Sng / Mm2 / Mmc / Cm6 / Dxx / Archive）、振り分け（Routing）、音源の判別（Detect）、
  再生（Sequencer）。Windows に依存しない
- `MidiPlayerWpf`：画面（MainWindow・VuMeter）、MIDI OUT／IN（WinMm・MidiOutputs・MidiIn）、
  設定（`%APPDATA%\midi_player_wpf\ports.txt`）

## ライセンス

GNU General Public License v2.0（[LICENSE](LICENSE)）。

## 謝辞

- レコンポーザ（RCP / G36）の読み込みは、Valley Bell 版 [rcp2mid](https://github.com/ValleyBell/MidiConverters)（GPL-2.0）と
  公開されているフォーマット仕様を参考にしています。
- Ballade（SNG）の読み込みは、jmplayer の sngmidi.cpp（MIT License）と、その移植である glosa の SngConverter.cs を参考にしています。
- MM2・MMC（マイクロミュージシャン）、DXX（TX7 音色ディスク）は、手元のファイルから形式を読み取って実装しています。
