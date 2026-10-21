# LocalSeek 系統分析與架構設計（草案 v0.1）

> 狀態：**已確認**（2026-10-20）。標示 🔶 的決策點 A–F 與「其他」皆採用文中建議方案。

---

## 1. 需求分析摘要

### 1.1 系統脈絡

```
 ┌──────────── 使用者（顧問工作室，2 人，單機）────────────┐
 │  輸入查詢 / 瀏覽結果 / 開啟檔案 / 設定根資料夾 / 看診斷 │
 └──────────────────────────┬──────────────────────────────┘
                            │
                   ┌────────▼────────┐        ┌──────────────────────┐
                   │   LocalSeek App │◄──────►│ %LocalAppData%\       │
                   │  (Uno, Windows) │        │   LocalSeek\          │
                   └────────┬────────┘        │   settings.json       │
                            │ 讀取 / 監看      │   index\ (自製格式)   │
             ┌──────────────▼──────────────┐  └──────────────────────┘
             │ 使用者的根資料夾（本機 / 網路磁碟）│
             │ ~10 萬個 txt/md/csv/log/json/xml/│
             │ html/htm/docx，含 Big5 舊檔      │
             └─────────────────────────────────┘
```

### 1.2 需求難點（真正決定架構的部分）

| # | 難點 | 影響的設計 |
|---|------|-----------|
| D1 | NFKC 會改變長度（㈱→(株)、ﬁ→fi），但高亮要回到**原始文字座標** | Tokenizer 必須攜帶「正規化字元 → 原始範圍」對照 |
| D2 | 改名/移動**不得重新抽取與分詞**，但檔名欄位本身會變 | 文件（路徑+檔名）與內容（內文 postings）必須分離 |
| D3 | 統計值（N、avgdl、df）必須只算現存文件，不可過時 | 統計在**快照開啟時**由整數精確重算，不在片段中預存 df |
| D4 | 不可變片段 + 墓碑 + 背景合併 + 快照隔離 + 任意時間點中止仍一致 | 類 Lucene 的 manifest generation 設計，所有 I/O 經介面 |
| D5 | 合併與增量索引並行，合併期間發生的刪除/改名不能遺失 | 合併提交時「帶入（carry）」期間的墓碑 |
| D6 | 參考解必須獨立實作分詞與計分 | Core 的 Parser 只產生「原始字串」的 AST，詞元化（binding）是另一步，參考解用自己的 tokenizer 做 binding |
| D7 | 效能：1 GB 文字 4 分鐘、查詢 P95 ≤ 100 ms、記憶體 ≤ 600 MB | 平行抽取 + 有記憶體上限的片段 buffer；查詢用 term-at-a-time + 稠密陣列 |

---

## 2. Solution 結構

```
LocalSeek.sln
├─ global.json                      (SDK 9.0.x、Uno.Sdk 版本)
├─ Directory.Build.props            (Nullable, TreatWarningsAsErrors, LangVersion)
├─ Directory.Packages.props         (Central Package Management)
├─ src/
│  ├─ LocalSeek.Core/               net9.0  — 分詞、查詢解析/執行、計分、片段格式、合併、摘要
│  ├─ LocalSeek.Indexing/           net9.0  — 爬檔、抽取器、編碼偵測、監看、差異掃描、glob、索引管線
│  ├─ LocalSeek.App.ViewModels/     net9.0  — Search/Preview/Status/Diagnostics/Settings VM
│  └─ LocalSeek.App/                Uno.Sdk, net9.0-windows10.0.19041, unpackaged
├─ tests/
│  ├─ LocalSeek.Tests/              xUnit — 單元 + 性質 + 故障注入 + 壓力
│  │   ├─ Reference/                參考解（獨立 tokenizer + 掃描式引擎 + 計分）
│  │   ├─ Fakes/                    FakeIndexStorage（故障注入）、FakeSourceFileSystem、FakeWatcher
│  │   └─ Properties/               性質測試 + PropertyRunner（固定種子）
│  └─ LocalSeek.Perf/               console — 效能量測（不在 CI 跑完整規模）
├─ tools/
│  └─ LocalSeek.CorpusGen/          console — 產生 50,000 份文件 / 混合語料
├─ samples/
│  ├─ corpus/                       ≥12 份小語料（Big5、UTF-16、BOM…）
│  └─ expected/                     ≥12 個查詢的手算結果（tokens/tf/df/idf/len/score）
└─ .github/workflows/ci.yml
```

相依方向（只能往左指）：

```
Core  ◄──  Indexing  ◄──  App.ViewModels  ◄──  App (Uno)
  ▲            ▲                ▲
  └────────────┴──── Tests ─────┘
```

---

## 3. Core 設計

### 3.1 正規化與分詞（Tokenizer）

**輸出資料型別**

```csharp
enum TokenKind { Latin, CjkBigram, CjkSingle }
readonly record struct Token(string Text, TokenKind Kind, int Position, int SourceStart, int SourceEnd);
// 另外輸出：每個 CJK 字元出現一次 → CharHit(codepoint, SourceStart, SourceEnd)
```

**正規化與位置還原（D1）**

1. 把原始字串切成「正規化單元」：一個 base code point + 其後的組合記號（Mn/Mc/Me）。代理對視為一個 code point。
2. 每個單元各自做 `NFKC → ToLowerInvariant → 臺→台`，產生的每個 char 都記錄來源範圍 `[unitStart, unitEnd)`。
3. **快速路徑**：ASCII 與 U+4E00–U+9FFF（NFKC 下為恆等）直接處理，不呼叫 `string.Normalize`；其他字元才走慢路徑。這是達成 4 分鐘目標的關鍵。
4. 詞元的原始範圍 = 第一個字元的 unitStart ～ 最後一個字元的 unitEnd。

🔶 **決策 A：驗收 #2「取出的子字串正規化後等於該詞元」的精確定義**

`㈱` 正規化成 `(株)`，詞元是 `株`，取回的子字串 `㈱` 正規化後是 `(株)` ≠ `株`；`㍻年` → `平成年`，詞元 `成年` 只能對應回 `㍻年`。因此逐字相等在數學上不可能對所有輸入成立。我建議的性質定義：

- **(a)** 取出的子字串正規化後**包含**該詞元（連續子字串）；
- **(b)** 範圍是**最小的**（從任一端再縮掉一個正規化單元就不再包含）；
- **(c)** 若詞元的每個字元都來自「獨立的、未被其他字元共享的」單元，則**完全相等**（涵蓋全形字、代理對、一般文字）。

單元測試會針對 ㈱、ﬁ、全形、𠮷 列出精確的期望範圍。

**詞元規則**：完全依照假設事項（Latin `[a-z0-9]+`；CJK 區段 L≥2 產生 L−1 個雙字詞元、L=1 產生單字詞元；位置規則；標點不影響位置）。

### 3.2 欄位與索引內容

每份文件有兩個欄位：`Name`（檔名含副檔名）與 `Body`（內文）。索引中存四種「詞項命名空間」：

| 命名空間 | 內容 | 是否存位置 |
|---------|------|-----------|
| `B:` body 詞元（Latin、CJK 雙字詞元） | tf + positions | ✅（片語） |
| `N:` name 詞元 | tf + positions | ✅ |
| `C:` body 的 CJK 單字詞頻 | tf | ❌ |
| `D:` name 的 CJK 單字詞頻 | tf | ❌ |

單字詞元（L=1）**不存 postings**：查詢單一 CJK 字時依規定用單字詞頻（`C:`/`D:`），而片語內含單字詞元是錯誤，所以它的 postings 永遠不會被用到，只需計入文件長度。

計分用的合成值（皆整數保存）：
- `len(d) = bodyLen + 2 × nameLen`
- `tf(d,t) = tf_body + 2 × tf_name`；片語 tf = body 起始位置數 + 2 × name 起始位置數（🔶 這是我對「檔名詞頻乘 2」套用到片語/前綴/單字詞頻的解讀，請確認）

### 3.3 查詢管線：Parse → Bind → Execute

```
 query string ──Lexer──► tokens ──Parser──► SyntaxTree（只含原始字串）
                                               │
                    Core 的 Binder（用 Core tokenizer）│  參考解的 Binder（用自己的 tokenizer）
                                               ▼
                                         BoundQuery（Term/Phrase/Prefix/CharTerm/Filter/And/Or/Not）
                                               │
                                     Executor（term-at-a-time）→ Scorer → TopK
```

- **文法**（遞迴下降，NOT > AND > OR）
  ```
  or      := and ( "OR" and )*
  and     := unary ( ["AND"] unary )*        // 相鄰即隱含 AND
  unary   := ("NOT" | "-") unary | primary
  primary := "(" or ")" | PHRASE | FILTER | WORD | PREFIX
  ```
- 錯誤回報：`QuerySyntaxException(position, length, message)`，UI 在搜尋框下方標示。
- 🔶 **決策 B：「只有 NOT 的查詢」判定規則**。我建議用「有界性」：詞項/片語/篩選為有界；`NOT x` 無界；`AND` 任一子節點有界即有界；`OR` 所有子節點有界才有界。根節點必須有界，否則報錯。例：`a -b` ✅、`-b` ❌、`-ext:txt` ❌、`ext:txt` ✅、`a OR -b` ❌。
- 篩選細節：`ext:` 前導句點容許並去除；`path:` 支援 `path:"C:\My Docs"`，OrdinalIgnoreCase 子字串、`/` 與 `\` 視為相同；`size:` 只接受整數 + 選用 KB/MB；`modified:` 的時區以注入的 `TimeZoneInfo` 計算（預設 `TimeZoneInfo.Local`，測試固定 UTC+8）。

### 3.4 執行與計分

- **Term-at-a-time + 稠密陣列**：快照內每份現存文件有一個全域序號（≤ 約 10 萬）。每個詞項產生 `int[] tf`（或稀疏表）+ bitset；布林運算在 bitset 上做；NOT 的全集 = live bitset。10 萬文件下 bitset 只有 12.5 KB，簡單且快。
- 片語：在 candidate 文件上做 positions 交集（Body、Name 分開算，再依 ×2 合成）。positions 與 doc/tf 分開存放，非片語查詢不解碼 positions。
- 統計（D3）：N、Σlen 在快照開啟時由 live 文件精確加總（long）；df 在查詢時由 postings 過濾 live 計算（整數），以 `(snapshot, term)` 快取。
- 分數：依規格 BM25 變形，正向詞項去重後依固定順序加總。同分依完整路徑 `OrdinalIgnoreCase` 由小到大；Top-K 用大小為 K 的 heap。
- 取消：Executor 每處理一段 postings 檢查 `CancellationToken`（確保 50 ms 內生效）。

### 3.5 摘要與高亮

- 預覽時**重新抽取該檔內容並重新分詞**，以 tokenizer 的原始座標取得命中範圍（索引本身只存位置，不存 offset，可省約一半索引空間）。用索引中保存的內容雜湊判斷檔案是否已變；已變則以當下內容高亮並觸發 reconcile。
- 片段：最多 3 段、每段 ≤ 120 字元（以 UTF-16 char 計，🔶 或以 text element 計？我建議 char 計但不切斷代理對），選擇涵蓋最多相異詞項的視窗，輸出依文件順序；重疊高亮合併。

---

## 4. 索引檔案格式與一致性（D2、D4、D5）

### 4.1 「文件」與「內容」分離（解決改名不重新分詞）

```
 Segment 內含兩張表：
   Contents[contentId]  : bodyLen, contentHash, rawSize   ← body postings 以 contentId 為鍵
   Docs[docId]          : path, size, mtime, attrs, nameLen, contentRef=(segId, contentId)
                                                          ← name postings 以 docId 為鍵
```

- 一般新增：同一片段寫入一筆 Content + 一筆 Doc（contentRef 指向自己）。
- **改名/移動（內容未變）**：舊 Doc 加墓碑；新片段只寫一筆 Doc（新路徑、新檔名 postings），`contentRef` 指向**舊片段的舊 Content**。內文完全不抽取、不分詞（以 `IExtractor` / `ITokenizer` 呼叫計數器斷言）。檔名分詞仍需執行（檔名改了），計數器會分開統計 body 與 name。
- Content 是否存活 = 是否有存活的 Doc 指向它。片段可刪除 = 沒有存活 Doc 且沒有被引用的 Content。
- 合併時，Content 的擁有 Doc 若在合併集合外，合併結果會「收養」該 Doc 並在同一次提交對原 Doc 加墓碑。

### 4.2 檔案佈局（`%LocalAppData%\LocalSeek\index\`）

| 檔案 | 說明 |
|------|------|
| `manifest_{gen:D10}.lsm` | 提交點。JSON 內容 + 長度 + CRC32 footer |
| `seg_{id:D8}.lss` | 不可變片段 |
| `seg_{id:D8}_del_{delGen}.lsd` | 不可變墓碑 bitset（每次刪除產生新 delGen） |
| `*.tmp` | 寫入中的暫存檔，開啟時清除 |

**片段檔結構（little-endian，所有整數 varint 或固定寬度整數）**

```
Header : magic "LSEKSEG\0" | formatVersion u16 | segmentId u64 | createdUtcTicks i64
Section ContentTable : count, [bodyLen, rawSize, contentHash u64]...          + CRC32
Section DocTable     : count, [path(UTF-8), size, mtimeTicks, attrs, nameLen, contentRef]... + CRC32
Section TermDict     : 依 (namespace, term) 排序，每 64 個詞一個 block，front-coding；
                       每詞：docCountInSeg, postingsOffset, positionsOffset   + CRC32
Section TermIndex    : 每個 block 的首詞與 offset（開啟時載入記憶體）       + CRC32
Section Postings     : doc delta + tf（varint）                               + CRC32
Section Positions    : position delta（varint）                              + CRC32
Footer : 各 section offset/length、整檔 CRC32、magic "LSEKEND\0"
```

**Manifest 內容**：formatVersion、generation、nextSegmentId、segments[{id, length, footerCrc, docCount, contentCount, delGen, delCount}]、settings 中根資料夾的指紋。

### 4.3 提交協定（crash-safe）

```
1. 寫入新片段/墓碑檔 → Flush(durable)
2. 寫 manifest_{N}.tmp → Flush(durable)
3. AtomicRename(manifest_{N}.tmp → manifest_{N}.lsm)      ← 提交點
4. 舊檔在「沒有任何 manifest 或開啟中的快照引用」時才刪除
```

開啟時：列舉 manifest，取**最高且校驗成功**的 generation；它引用的檔案必須存在且 header/footer 校驗成功；未被引用的檔案與 `.tmp` 清除。任何時刻中止 → 不是看到 N-1 就是看到 N，不需手動修復。

### 4.4 快照與檔案生命週期

- `IndexSnapshot`：開啟時固定一個 manifest generation，持有其所有片段/墓碑的引用計數（行程內 ref count）。`Dispose` 才釋放。
- `FileDeleter`：被取代檔案的引用數歸零才刪除（驗收 #14）。

### 4.5 損毀與版本處理（驗收 #19）

| 情況 | 處理 |
|------|------|
| manifest 版本不符/校驗失敗 | 退回前一個有效 generation；全部無效 → 全量重建 |
| 片段版本不符/校驗失敗/遺失 | 從 manifest 移除該片段（及指向其 Content 的 Doc），並觸發該根資料夾的**差異掃描**——差異掃描會自然把缺少的檔案補回，因此只重建受影響部分 |
| 開啟 2 秒限制 | 開啟時只驗證 header、footer、TermIndex、DocTable 的 CRC；Postings/Positions 的完整 CRC 在背景驗證，失敗則走上一列 |

UI 會以 InfoBar 明確提示發生了什麼。

### 4.6 I/O 抽象

```csharp
public interface IIndexStorage
{
    IIndexOutput Create(string name);            // Write(ReadOnlySpan<byte>), Flush(durable: bool), Dispose
    IIndexInput  OpenRead(string name);          // ReadAt(long offset, Span<byte>)，以 RandomAccess 實作
    void AtomicRename(string from, string to);
    void Delete(string name);
    IEnumerable<string> List();
}
```

測試用 `FakeIndexStorage`：每個檔案分「已 durable」與「未 flush」兩層；可在第 k 個操作注入例外；`SimulateCrash()` 丟棄未 flush 資料（可選擇保留隨機前綴模擬 torn write）。故障注入測試對提交與合併的**每一個** I/O 操作逐一注入 → 重開 → 斷言邏輯內容等於操作前或操作後（驗收 #15）。

### 4.7 合併

- Tiered 策略：同一層級（依大小 ×10 分層）累積 ≥ 10 個片段就合併 → 片段數量對數成長（驗收 #16）。
- 合併在背景執行緒以串流方式 k-way merge term dictionary，記憶體有上限。
- 合併提交取得 writer lock，**帶入**合併期間新增的墓碑（docId 映射到新片段）。合併後墓碑清除。

---

## 5. Indexing 設計

### 5.1 管線

```
 Crawler / Watcher / DiffScanner
          │  (path, reason)
          ▼
   Reconciler ── stat 檔案、對照索引目錄（path → size, mtime, hash） → 決定 Add / Update / Delete / Rename
          │
          ▼
   Channel<WorkItem> ──► N 個 Extract+Tokenize worker（ProcessorCount）
                                     │
                                     ▼
                       IndexWriter（單一寫入者）: RAM buffer（上限 128 MB）→ Flush 片段 → Commit
                                     │
                       MergeScheduler（背景）
```

所有外部依賴皆以介面注入：`ISourceFileSystem`、`IFileWatcherFactory`、`TimeProvider`（含計時器、`Task.Delay(…, TimeProvider)`）。

### 5.2 爬檔強健性（驗收 #13）

| 情況 | 處理 |
|------|------|
| 接合點/符號連結迴圈 | 🔶 **決策 C**：建議「跟隨，但以 (volume serial, file id) 記錄已拜訪資料夾，重複即略過並記錄」。替代方案：完全不跟隨 reparse point |
| > 260 字元路徑 | .NET 9 原生支援長路徑；開啟檔案 / 檔案總管的 shell 呼叫加 `\\?\` 前綴或降級提示 |
| 被鎖定 | sharing violation → 重試 3 次（1s、2s、4s，經 TimeProvider）→ 記為「稍後重試」，下次差異掃描再試 |
| 權限不足 | 記錄並略過該資料夾/檔案 |
| 讀取中被修改 | 讀前讀後比對 (size, mtime)，不一致 → 放回防抖佇列 |
| 隱藏/系統屬性 | 🔶 **決策 D**：建議預設略過帶 Hidden 或 System 屬性的項目（例如 Office 的 `~$x.docx` 暫存檔），記錄原因，設定可關閉 |

任何單檔失敗只寫入 `DiagnosticsLog`，不中止整體。

### 5.3 抽取器

| 類型 | 作法 |
|------|------|
| txt/md/csv/log/json/xml | 編碼偵測後原文即內容（xml 不去標籤，🔶 請確認） |
| html/htm | 自寫容錯 tokenizer：略過 `<script>`、`<style>`、註解、標籤；`WebUtility.HtmlDecode` 解實體 |
| docx | `ZipArchive` 讀 `word/document.xml`，`XmlReader` 串流：`w:t` 取文字、`w:tab`→`\t`、`w:br`→`\n`、段落結束→`\n`、略過 `w:delText`；頁首頁尾在不同 part，自然排除 |

**編碼偵測順序**：大小上限 → BOM（UTF-8 / UTF-16 LE/BE）→ NUL 比例（前 64 KB 中 > 5% 視為二進位）→ 嚴格 UTF-8 解碼成功 → 嚴格 Big5（CP950，DecoderExceptionFallback）成功 → 否則「無法判定編碼」跳過。

### 5.4 監看與差異掃描（驗收 #11、#12）

- 每個根資料夾一個 `FileSystemWatcher`（IncludeSubdirectories、64 KB buffer）。事件只把路徑標成「髒」，由 Reconciler 讀取實際狀態決定動作 → 事件順序錯亂或遺漏都不影響正確性。
- 防抖：每路徑靜默 500 ms，最大等待 1.5 s；批次後立即 commit → 存檔後 ≤ 3 秒可搜尋。
- 改名配對：`Renamed` 事件直接配對；跨資料夾移動在同一批次中把「消失路徑」與「新路徑」以 (size, 原始位元組雜湊) 配對。資料夾改名 → 以路徑前綴批次改名 Doc。
- `Error`（緩衝區溢位）或網路磁碟 → 對該根資料夾排程差異掃描；網路磁碟另以週期掃描（預設 5 分鐘）。
- 結果時效：顯示結果時對目前可見項目做存在性檢查，消失者立即標示並觸發 reconcile（watcher 延遲期間的保險）。

### 5.5 Glob 排除規則（驗收 #21）

自寫 matcher：對「相對於根資料夾、以 `/` 分隔」的**完整路徑**比對，不分大小寫；`*` 不跨 `/`、`**` 任意深度、`?` 單字元、`/` 結尾只比對資料夾（資料夾命中則整棵子樹略過）。
🔶 **決策 E**：依規格是完整路徑比對，所以 `*.tmp` 只排除根目錄下的檔案，要排除全部需寫 `**/*.tmp`。我建議嚴格依規格，並在設定頁提示；替代方案是仿 .gitignore「不含 `/` 的樣式可比對任意層」。

---

## 6. App.ViewModels 與 App

### 6.1 ViewModels

| VM | 重點 |
|----|------|
| `SearchViewModel` | `QueryText` 變更 → 防抖（🔶 建議 150 ms）→ 取消前一個 CTS → 以遞增版本號執行；回來的結果版本 ≠ 最新版本就丟棄 |
| `ResultItemViewModel` | 路徑、分數、修改時間；摘要延遲載入（只對可見/選取項目） |
| `PreviewViewModel` | 片段 + 高亮範圍 |
| `StatusViewModel` | 進度、速率、待處理數、暫停/恢復 |
| `DiagnosticsViewModel` | 索引統計、跳過/失敗清單與原因 |
| `SettingsViewModel` | 根資料夾、排除規則、大小上限；JSON 損毀 → 預設值 + 提示（保留 `settings.json.bad`） |

抽象：`IUiDispatcher`（假排程器）、`TimeProvider`（假時鐘）、`IShellService`（開啟檔案、在檔案總管中顯示）、`ISearchEngine`。
鍵盤命令：`Ctrl+K` 聚焦、方向鍵選取、`Enter` 開啟、`Alt+Enter` 開啟所在資料夾——命令放 VM，按鍵綁定在 View。

### 6.2 App（Uno）

單一頁面三欄：搜尋框 + 錯誤提示 / 虛擬化 `ListView` 結果 / 預覽窗格（`RichTextBlock` + Highlighter）；底部狀態列；另有設定與診斷頁。Composition root 在 `App.xaml.cs`。

---

## 7. 測試策略

| 類別 | 內容 | 預估案例數 |
|------|------|-----------|
| Tokenizer 單元 | 預期檔逐項比對（驗收 #1）、座標（#2） | ~40 |
| Query parser 單元 | 語法、優先序、錯誤位置、篩選（#4、#8） | ~60 |
| 抽取/編碼單元 | #3 | ~25 |
| 索引格式/快照/損毀 | #14、#19 | ~30 |
| 故障注入 | #15（每個 I/O 點） | 參數化，數百組 |
| Glob/設定 | #21 | ~30 |
| 爬檔（假 FS） | #13 | ~15 |
| 監看/改名 | #11、#12 | ~15 |
| ViewModel | #20 | ~20 |
| 性質測試（≥ 8 組） | #2 座標、#5 布林、#6 片語、#7 單字/前綴、#9 計分、#10 統計維護、#11 增量、#16 合併、#18 摘要 | 9 組 |
| 壓力（固定種子） | #17 | 2–3 |

- **性質測試**：自寫輕量 `PropertyRunner(seed, iterations)`，失敗時訊息包含 seed 與最小化前的輸入；可用環境變數 `LOCALSEEK_SEED` 重現。
- **參考解獨立性**：`tests/LocalSeek.Tests/Reference/` 內自行實作 tokenizer（以不同寫法：先整串 NFKC 再逐字掃描，不做座標）與 BM25；只共用 SyntaxTree / 結果 DTO 型別。README 說明此界線。

---

## 8. 效能預估與策略

| 目標 | 策略 |
|------|------|
| 建立 ≤ 4 分（約 1 GB 文字） | 8 worker 平行抽取+分詞；正規化快速路徑；varint postings；RAM buffer 128 MB |
| 查詢 P95 ≤ 100 ms | term-at-a-time 稠密陣列、positions 分離、TermIndex 常駐記憶體、`RandomAccess` 讀取 |
| 開啟 ≤ 2 秒 | 只載入 TermIndex、DocTable、墓碑；完整 CRC 背景驗證 |
| 記憶體 ≤ 600 MB | buffer 上限、合併串流化、陣列池化（`ArrayPool`） |
| 存檔可見 ≤ 3 秒 | 500 ms 防抖 + 1.5 s 最大等待 + 小批次即時 commit |

---

## 9. 套件清單（🔶 決策 F：需你同意引入）

| 套件 | 用途 | 授權 | 必要性 |
|------|------|------|--------|
| Uno.Sdk / Uno.WinUI | UI（題目指定） | Apache-2.0 | 必要 |
| System.Text.Encoding.CodePages | Big5 | MIT | 必要（題目允許） |
| xunit、xunit.runner.visualstudio、Microsoft.NET.Test.Sdk | 測試（題目指定） | Apache/MIT | 必要 |
| **System.IO.Hashing** | CRC32（校驗碼）、XxHash3（內容雜湊） | MIT，Microsoft | 建議；不同意則自寫 CRC32/FNV |
| **Microsoft.Extensions.TimeProvider.Testing** | `FakeTimeProvider` | MIT，Microsoft | 建議；不同意則自寫假時鐘 |
| **CommunityToolkit.Mvvm** | ObservableObject、RelayCommand | MIT | 可選；不同意則自寫 INotifyPropertyChanged 基底 |

不使用 FsCheck 等性質測試框架（自寫 runner 即可）；HTML 解析不引入 HtmlAgilityPack。

---

## 10. 開發里程碑（每步可編譯、可測試、可 commit）

| M | 內容 | 對應驗收 |
|---|------|---------|
| 0 | Solution 骨架、CPM、空 Uno App 可啟動、CI 綠燈 + 發佈 artifact | 26 |
| 1 | 正規化 + Tokenizer + 座標還原 | 1, 2 |
| 2 | Query Lexer/Parser/Binder | 4, 8 |
| 3 | 記憶體內索引 + Executor + Scorer + **參考解** + 性質測試 | 5, 6, 7, 9 |
| 4 | IIndexStorage、片段格式、manifest、快照、損毀處理、故障注入 | 14, 15, 19 |
| 5 | 墓碑、統計維護、合併、並行壓力 | 10, 16, 17 |
| 6 | 編碼偵測 + 抽取器 | 3 |
| 7 | Glob、設定、爬檔 + 假 FS | 13, 21 |
| 8 | Reconciler、Watcher、防抖、差異掃描、改名 | 11, 12 |
| 9 | 摘要與高亮 | 18 |
| 10 | ViewModels | 20 |
| 11 | Uno UI、診斷頁、平台服務 | 23 |
| 12 | CorpusGen、效能量測與調校 | 22 |
| 13 | 範例語料 + 手算結果、README、截圖 | 25、交付物 |

---

## 11. 風險與已知限制（初估）

1. **Big5 誤判**：嚴格 Big5 解碼對部分隨機位元組（如 GBK、Latin-1）也可能成功。會加上「CJK 比例」門檻降低誤判，仍列為已知限制。
2. **UTF-16 無 BOM**：依規格不支援，會被 NUL 檢查判為二進位。
3. **NFKC 分段近似**：以「base + 組合記號」為正規化單元，少數跨單元組合（如諺文 Jamo 組字）的座標會偏寬，但都屬分隔符，不影響詞元。
4. **Uno.Sdk 在 CI 的建置**：需在 windows runner 驗證 WinAppSDK unpackaged 發佈，列為 M0 首要風險。
5. **Watcher 延遲期間**：檔案刪除到 commit 之間（≤ ~2 秒）的查詢統計值仍含該檔；顯示層做存在性檢查補強。

---

## 12. 實作後的差異（2026-10-20 補記）

| 項目 | 設計草案 | 最終實作與原因 |
|---|---|---|
| Postings / Positions | 兩個獨立 section | 合併為一個 section，每個詞的 postings 後面緊接 positions。片段可以串流寫入（合併時記憶體有上限），AND 查詢仍只讀 postings 那一段 |
| 合併時的「收養」 | 合併結果收養集合外的擁有文件 | 改為 closure（選取片段時一併納入擁有文件所在的片段）；合併期間的新參照若還在 RAM 中就重新指向，若已提交則放棄這次合併、下次重試。邏輯較簡單，且不需要在別的片段重新分詞檔名 |
| 片語執行 | positions 交集 | 先用 postings 求候選文件，再只解碼候選文件的 positions；不同的詞平行解碼，文件分段平行比對（P95 從 118 ms 降到 43 ms） |
| 資料位置 | 固定 `%LocalAppData%\LocalSeek` | 預設不變；另外可用環境變數 `LOCALSEEK_DATA_DIR` 覆寫，供展示與效能量測 |
| 鎖定檔重試 | 1、2、4 秒 | 相同；三次後記為「持續被鎖定」，等下次變更或掃描時再試 |
