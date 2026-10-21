# 第 9 章　爬檔、編碼偵測與增量索引

## 學習目標

- 設計一個多階段的文字編碼偵測流程，並用實驗數據決定判斷門檻。
- 寫出能承受接合點迴圈、長路徑、鎖定檔、權限不足的爬檔程式。
- 理解「事件只標記、實際動作由現況決定」的 reconcile 模式，為什麼能容忍遺失或亂序的事件。
- 實作防抖（debounce）與最大延遲。
- 偵測改名與移動，避免重新分詞。

相關程式：`src/LocalSeek.Indexing/`（`Extraction/TextDecoder.cs`、`Crawling/Crawler.cs`、`FileSystem/SourceFileReader.cs`、`Pipeline/IndexingEngine.cs`、`Pipeline/IndexingService.cs`）

---

## 9.1 編碼偵測

文字檔沒有標記自己的編碼。給一串位元組，要怎麼知道它是 UTF-8、UTF-16 還是 Big5？

### 流程

```
1. 有 BOM？         EF BB BF → UTF-8；FF FE → UTF-16 LE；FE FF → UTF-16 BE
2. NUL 比例過高？   視為二進位檔，跳過
3. 嚴格 UTF-8 解碼成功？  → UTF-8
4. Big5 結構正確，且嚴格 Big5 解碼成功？
   └─ 漢字 ≥ 10 個且常用字比例 < 85%？ → 疑似 GBK，跳過
   └─ 否則 → Big5
5. 都不是 → 無法判定編碼，跳過並記錄
```

### 為什麼 UTF-8 要先試？

UTF-8 的結構非常嚴格：多位元組字元的每個後續 byte 都必須是 `10xxxxxx`。隨機的 Big5 內容幾乎不可能剛好是合法的 UTF-8。反過來，Big5 的結構寬鬆得多，很多 UTF-8 內容也可能被當成 Big5 解碼。所以**嚴格的先試**。

「嚴格」指的是解碼器遇到不合法序列時丟例外，而不是用 `?` 替代：

```csharp
// src/LocalSeek.Indexing/Extraction/TextDecoder.cs（節錄）
private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
...
StrictBig5 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
```

### 為什麼 Big5 還要另外檢查結構？

開發時發現：Windows 的 code page 950 會把單獨的 `0x80` 與 `0xFF` 位元組對應到某些字元，所以即使用了「嚴格」解碼器，一些明顯不是 Big5 的內容也能「成功」解碼。因此另寫了結構檢查：每個 byte 必須是 ASCII，或者是「首位元組 0x81–0xFE 加上尾位元組 0x40–0x7E、0xA1–0xFE」的組合。

```csharp
// src/LocalSeek.Indexing/Extraction/TextDecoder.cs（節錄）
/// Windows code page 950 maps stray single bytes 0x80 and 0xFF to characters, so structure is checked
/// separately: every byte is ASCII or part of a lead (0x81–0xFE) + trail (0x40–0x7E, 0xA1–0xFE) pair.
private static bool IsWellFormedBig5(ReadOnlySpan<byte> bytes) { ... }
```

**教學重點**：不要假設函式庫的行為完全符合你的預期。「嚴格解碼器」在某些編碼下仍然寬鬆，這要靠測試（`Bytes_valid_in_neither_utf8_nor_big5_are_skipped`）才會發現。

---

## 9.2 案例研究：簡體 GBK 被誤判為 Big5

第 1 章提過，審查時發現簡體中文的 GBK 檔案常常**同時是合法的 Big5**。GBK 與 Big5 都是雙位元組編碼，位元組範圍大量重疊。「这是一份简体中文的报告」的 GBK 位元組，用 Big5 解讀會變成「涴岆珨爺潠极笢恅腔惆豢」。結構檢查與嚴格解碼都會通過。

### 觀察：真正的繁體文字幾乎都是常用字

Big5 把漢字分成兩級：

- **第一級（常用字）**：0xA440–0xC67E，5,401 字。
- **第二級（次常用字）**：0xC940–0xF9D5，7,652 字。

真實的繁體文章，絕大多數字都在第一級。GBK 被當成 Big5 解讀時，位元組會隨機落在兩級之中，常用字的比例大約只有一半。所以「常用字比例」是一個可以區分兩者的特徵。

### 用實驗決定門檻，而不是用猜的

門檻設多少？漢字至少要有幾個才判斷？這些不能憑感覺決定。實驗方法：

1. 把 repo 中的繁體中文文件切成每段 10、20、30、40 個漢字的樣本。
2. 繁體樣本直接編成 Big5，計算常用字比例。
3. 同樣的文字用 Windows 內建的繁轉簡轉成簡體，編成 GBK，再當成 Big5 計算比例。

| 每段漢字數 | 繁體被誤擋（門檻 85%） | GBK 被誤收（門檻 85%） | 繁體最低比例 | GBK 最高比例 |
|---|---|---|---|---|
| 10 | 0 / 1462 | 16 / 1462（1.1%） | 90.0% | 100% |
| 20 | 0 / 730 | 2 / 730（0.3%） | 95.0% | 90.0% |
| 30 | 0 / 486 | 0 / 486 | 96.7% | 80.0% |
| 40 | 0 / 363 | 0 / 363 | 97.5% | 72.5% |

結論：門檻 85%、至少 10 個漢字。在這組資料上，繁體完全沒有被誤擋；GBK 在 30 字以上全部被擋下。

```csharp
// src/LocalSeek.Indexing/Extraction/TextDecoder.cs（節錄）
if (IsWellFormedBig5(bytes) && TryDecode(StrictBig5, bytes, out text))
{
    var (common, ideographs) = CountBig5Ideographs(bytes);
    if (ideographs >= MinIdeographsToJudge && common < ideographs * MinCommonRatio)
    {
        return new ExtractionResult.Skipped(
            SkipReason.UnknownEncoding,
            $"無法判定編碼（Big5 常用字只占 {(double)common / ideographs:P0}，疑似簡體 GBK 或其他編碼）");
    }

    return new ExtractionResult.Success(text, "Big5");
}
```

常數的註解寫明了數據來源，讓未來的維護者知道這個數字從哪裡來、能不能改：

```csharp
/// Real Traditional Chinese text in Big5 is almost entirely level-1 (common) characters: at least 90%
/// even in 10-character samples of our documents. GBK text read as Big5 lands half in level 2 or the
/// reserved areas (median 50%, at most 72% in 40-character samples).
private const double MinCommonRatio = 0.85;
```

### 修正的連帶影響

效能測試用的語料產生工具，原本從**所有** Big5 漢字中平均抽字，常用字只佔約 40%。新規則會把這些合成檔案全部擋下。所以工具也要改成只用常用字（這樣反而更接近真實文字），並重新產生語料、重新量測效能。

**教學重點**：

1. 修一個 bug 前，先寫一個能重現它的測試，並確認它在修正前**失敗**。本例的測試還額外斷言「這段 GBK 位元組確實能通過 Big5 嚴格解碼」，確保測到的是新規則，而不是被舊的結構檢查擋下。
2. 啟發式規則（heuristic）一定有誤判。要把剩下的限制寫清楚：少於 10 個漢字的檔案無法判斷；大量使用罕用字的繁體檔案（例如古籍）可能被擋。被擋的檔案會出現在診斷頁，不會無聲遺失。

---

## 9.3 爬檔的強健性

### 迴圈：接合點與符號連結

Windows 的接合點（junction）可以讓資料夾指向自己的上層，形成無窮迴圈。處理方式（決策 C）：

- 跟隨連結，但記錄每個走過的資料夾的 **canonical 路徑**（連結解析後的真實路徑）。走過的不再進入。
- 連結目標在根資料夾內的，直接略過（那些檔案本來就會從正常路徑被索引）。

```csharp
// src/LocalSeek.Indexing/Crawling/Crawler.cs（節錄）
if (entry.IsReparsePoint)
{
    var target = fileSystem.GetLinkTarget(entry.FullPath);
    ...
    canonical = Canonical(target);
    if (IsSameOrUnder(canonical, rootCanonical))
    {
        diagnostics.Record(entry.FullPath, DiagnosticKind.LoopDetected, $"連結指向根資料夾內的 {target}，略過以避免迴圈或重複");
        return false;
    }
}

if (!visited.Add(canonical))
{
    diagnostics.Record(entry.FullPath, DiagnosticKind.LoopDetected, $"資料夾 {canonical} 已經索引過（迴圈或重複連結）");
    return false;
}
```

爬檔用**明確的堆疊**（`Stack`）而不是遞迴，所以很深的資料夾結構也不會耗盡呼叫堆疊。

### 其他情況

| 情況 | 處理 |
|---|---|
| 超過 260 字元的路徑 | .NET 9 原生支援，直接讀取；有實機測試 `Real_long_path_is_read` |
| 權限不足的資料夾 | 記錄 `AccessDenied`，兄弟資料夾繼續 |
| 被鎖定的檔案 | 1、2、4 秒後重試；三次都失敗記為 `LockedGaveUp`，等檔案下次變更再試 |
| 讀取中被修改 | 讀取前後比對大小與修改時間，不一致就放回待處理佇列 |
| 隱藏或系統檔 | 預設略過並記錄（決策 D） |

「讀取中被修改」的偵測：

```csharp
// src/LocalSeek.Indexing/FileSystem/SourceFileReader.cs（節錄）
var before = fileSystem.GetEntry(path);
bytes = fileSystem.ReadAllBytes(path);
var after = fileSystem.GetEntry(path);
if (after.Size != before.Size || after.LastWriteUtc != before.LastWriteUtc || bytes.LongLength != after.Size)
{
    return new ReadOutcome.Changed();      // 引擎收到後會把路徑重新標記為待處理
}
```

### 用假檔案系統測試，再實機驗證

上面每一種情況都很難在真實磁碟上穩定地製造（尤其是「讀取中被修改」）。所以 `CrawlerTests` 用 `FakeSourceFileSystem` 測試所有情況；另外 `PhysicalFileSystemTests` 在真實磁碟上建立 `mklink /J` 的接合點迴圈、超長路徑與獨占鎖定的檔案，確認假實作的行為與真實情況一致。

開發時，實機接合點測試的**清理程式**曾經自己陷入迴圈：它用遞迴刪除資料夾，而遞迴刪除會跟隨接合點。修正方式是先手動移除接合點，再刪除資料夾。這說明測試程式本身也要考慮它所測試的邊界情況。

---

## 9.4 監看與 reconcile

### 事件不可靠

`FileSystemWatcher` 的事件有很多問題：

- 緩衝區溢位時，事件會**遺失**。
- 一次存檔可能觸發多個事件（建立暫存檔、改名、刪除、修改）。
- 事件可能**亂序**。
- 跨資料夾移動可能被報告成「刪除 + 新增」。
- 網路磁碟可能完全收不到事件。

如果系統「依事件直接動作」（收到刪除就刪、收到新增就加），任何一個事件遺失或亂序都會讓索引出錯。

### 解法：事件只標記「髒」

```csharp
// src/LocalSeek.Indexing/Pipeline/IndexingEngine.cs（節錄）
public void OnFileChange(FileChange change)
{
    lock (_lock)
    {
        if (change is { Kind: FileChangeKind.Renamed, OldPath: not null })
        {
            _renameHints[change.Path] = change.OldPath;   // 改名事件只留下「提示」
            Touch(change.OldPath);
        }

        Touch(change.Path);
    }
}
```

事件**不決定**要做什麼，只是說「這個路徑可能變了，請檢查」。真正處理時，引擎去讀**磁碟上的實際狀態**，與索引中的狀態比對：

| 磁碟上 | 索引中 | 動作 |
|---|---|---|
| 不存在 | 存在 | 刪除 |
| 存在 | 不存在 | 新增（或認出是移動，見 9.6 節） |
| 存在，大小或時間不同 | 存在 | 重新索引；若位元組雜湊相同，只更新中繼資料 |
| 存在，相同 | 存在 | 不動 |

這個模式叫 **reconcile**（調和）：比對「期望狀態」與「實際狀態」，然後做出讓兩者一致的最小動作。Kubernetes 等系統也採用同樣的思路。它的特性是**冪等**（idempotent）：同一個路徑被標記十次，處理結果與標記一次相同；事件亂序也沒關係，因為最後看的都是現況。

事件遺失怎麼辦？監看回報錯誤（例如緩衝區溢位）時，對整個根資料夾排程**差異掃描**：把整棵目錄樹當成「全部都髒了」重新 reconcile 一次。網路磁碟則改用週期性的差異掃描。

驗收 #11 的性質測試（`IncrementalProperties`）隨機產生新增、修改、改名、移動、刪除與溢位的序列，最後斷言增量更新的結果**與從零重建完全相同**。

---

## 9.5 防抖與最大延遲

一次存檔可能產生好幾個事件，而且使用者可能在幾秒內連續存檔。每個事件都立刻處理太浪費，所以需要**防抖**（debounce）：路徑安靜 500 ms 後才處理。

但純粹的防抖有一個問題：如果檔案每 400 ms 就被寫一次（例如持續寫入的 log 檔），它永遠不會「安靜 500 ms」，也就永遠不會被索引。所以還要加上**最大延遲**：從第一個事件算起，最多等 1.5 秒。

```csharp
// src/LocalSeek.Indexing/Pipeline/IndexingEngine.cs
/// Marks a path dirty: due after the debounce interval, but no later than MaxDelay after its first event.
private void Touch(string path)
{
    var now = _time.GetUtcNow();
    var due = now + Options.Debounce;
    _pending[path] = _pending.TryGetValue(path, out var existing)
        ? existing with { Due = Min(due, existing.FirstSeen + Options.MaxDelay), LockAttempts = 0 }
        : new PendingPath(due, now, 0);
}
```

每個待處理路徑記錄兩個時間：`FirstSeen`（第一次被標記）與 `Due`（預計處理時間）。新事件把 `Due` 往後推，但不超過 `FirstSeen + MaxDelay`。

所有時間都來自注入的 `TimeProvider`，測試用 `FakeTimeProvider` 精確推進時間：

```csharp
// tests/LocalSeek.Tests/Pipeline/IndexingEngineTests.cs（節錄）
f.Event(FileChangeKind.Changed, path);
f.Time.Advance(TimeSpan.FromMilliseconds(400));
await f.Engine.ProcessAsync();
Assert.Equal(1, f.Engine.PendingCount);           // 還沒安靜 500 ms，不處理

// Keep "saving" every 400 ms: the quiet period never elapses, the 1.5 s cap still triggers.
for (var i = 0; i < 3; i++)
{
    f.Event(FileChangeKind.Changed, path);
    f.Time.Advance(TimeSpan.FromMilliseconds(400));
    await f.Engine.ProcessAsync();
}

Assert.Equal(0, f.Engine.PendingCount);           // 最大延遲到了，一定處理
```

測試在幾毫秒內跑完，完全不需要真的等待。

這個設計讓「存檔到可被搜尋」的時間落在 0.5 到 1.5 秒加上處理時間，效能量測的實測最大值約 0.56 秒（559 ms），遠低於 3 秒的限制。

---

## 9.6 改名與移動：不重新分詞

需求要求內容沒變的改名與移動，不得重新抽取與分詞。引擎用兩種方式認出「這是同一份檔案」：

### 1. 改名提示（watcher 回報的改名）

```csharp
// src/LocalSeek.Indexing/Pipeline/IndexingEngine.cs（節錄）
/// Watcher-reported renames: same size and time means the bytes did not change.
if (work.TryGetValue(newPath, out var target) && target.Indexed is null
    && gone.TryGetValue(oldPath, out var old)
    && old.Size == target.Entry.Size && old.LastWriteUtc == target.Entry.LastWriteUtc)
{
    _writer.Rename(oldPath, newPath, target.Entry.Size, target.Entry.LastWriteUtc);
    ...
}
```

舊路徑已經不在磁碟上，新路徑還不在索引中，而且大小與修改時間相同，就視為改名。不需要讀檔。

### 2. 推斷的移動（刪除 + 新增，或差異掃描發現的）

跨資料夾移動常被報成「刪除 + 新增」，差異掃描也只看得到「舊的不見了、新的出現了」。這時用**內容雜湊**配對：

1. 把「消失的文件」依大小分組。
2. 對每個新檔案，如果有大小相同的消失文件，就讀檔並計算 XxHash3。
3. 雜湊與某個消失文件的雜湊（索引時已存在 Content 中）相同，就是同一份檔案，呼叫 `Rename`。

讀檔是必要的（才能算雜湊），但讀到的位元組如果配對失敗，會留給後續的正常索引流程使用，不會讀第二次。

### 3. 資料夾改名

資料夾改名時，watcher 只回報資料夾本身的改名。引擎爬新資料夾，為底下每個檔案產生「新路徑 ← 舊資料夾 + 相同的相對路徑」的改名提示，再走上面第 1 種流程。

### 用計數器證明

```csharp
/// How many document bodies were tokenized (renames must not increase it).
public long BodyAnalysisCount => Interlocked.Read(ref _bodyAnalyses);
```

`IndexingEngineTests` 中的改名、跨資料夾移動、資料夾改名、差異掃描發現移動等測試，都斷言 `BodyAnalysisCount` 沒有增加，同時路徑已經更新（驗收 #12）。

---

## 練習

1. 為什麼「先試 Big5、再試 UTF-8」會出問題？舉一個例子。
2. 9.2 節的實驗只用了技術文件作為繁體樣本。如果工作室的檔案大多是古文或含有大量人名，實驗結果可能怎麼變？你會怎麼改進實驗？
3. 在 reconcile 模式下，下面的事件序列最後會得到什麼結果：「修改 a.txt」、「刪除 a.txt」、「新增 a.txt（內容不同）」，而且三個事件都在 500 ms 內發生？
4. 如果把 `MaxDelay` 拿掉，寫一個測試證明持續寫入的檔案永遠不會被索引。
5. 改名提示用「大小與修改時間相同」判斷內容沒變。這個判斷可能出錯嗎？出錯時會發生什麼事？
