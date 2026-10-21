# 第 12 章　效能分析與調校

## 學習目標

- 建立可重複的效能量測：語料、環境、指標、統計方式。
- 理解 P95 與「最壞情境」的差異，以及為什麼隨機抽樣的查詢集可能不夠。
- 用「先量測、找出瓶頸、再優化」的流程改善效能。
- 在不改資料格式的前提下，找出可以平行化的部分，並確保結果不變。
- 誠實地報告效能數據，包括起伏、餘裕與未驗證的推論。

相關程式：`tools/LocalSeek.CorpusGen/Program.cs`、`tests/LocalSeek.Perf/Program.cs`、`src/LocalSeek.Core/Search/QueryExecutor.cs`；結果見 [perf-results.md](../perf-results.md)。

---

## 12.1 效能需求

| 項目 | 限制 |
|---|---|
| 完整建立索引（5 萬份、平均 20 KB） | ≤ 4 分鐘 |
| 兩詞 AND 與片語查詢的 P95 | ≤ 100 ms |
| 開啟既有索引 | ≤ 2 秒 |
| 記憶體峰值 | ≤ 600 MB |
| 存檔後可被搜尋 | ≤ 3 秒（含防抖） |

這些限制以「8 核心的開發機」為前提。

---

## 12.2 建立可重複的量測

### 語料：用工具產生，不放進 repo

5 萬份文件約 1 GB，不能放進 repo。`CorpusGen` 用**固定種子**產生語料，任何人執行同一個指令都得到相同的檔案：

```powershell
dotnet run -c Release --project tools/LocalSeek.CorpusGen -- --out D:\perf\corpus --count 50000 --mixed
```

語料設計的重點：

- **詞頻符合 Zipf 分佈**：少數詞非常常見、大多數詞很少見，和真實文字一樣。均勻分佈的隨機詞會讓查詢的行為很不真實。
- **混合格式與編碼**（`--mixed`）：70% UTF-8 文字、8% Big5、4% UTF-16、5% HTML，以及 docx、CSV、Markdown 等，讓抽取與編碼偵測的成本也被量到。

### 量測環境：模擬 8 核心

開發機是 i7-13700（16 核心 24 執行緒），比題目的「8 核心」強。量測程式把行程限制在前 16 個邏輯處理器（8 個效能核心加上超執行緒），索引平行度設為 8：

```csharp
// tests/LocalSeek.Perf/Program.cs（節錄）
if (options.Cores > 0 && options.Cores < Environment.ProcessorCount)
{
    process.ProcessorAffinity = (nint)((1L << options.Cores) - 1);
}
```

報告中寫明 CPU、記憶體、作業系統、.NET 版本與語料大小。沒有環境資訊的效能數據沒有意義。

### 指標與統計

| 指標 | 量法 |
|---|---|
| 建立時間 | 從空索引到所有檔案提交、背景合併完成 |
| 記憶體峰值 | 每 50 ms 取樣一次 Working Set，取最大值 |
| 開啟時間 | `IndexWriter.Open` 加上取得第一個快照 |
| 查詢延遲 | 300 條查詢，先跑 5 條暖機，再逐條計時；報 P50、P95、最大值 |
| 存檔到可搜尋 | 啟動完整的索引服務（含 watcher 與防抖），輪流新增檔案與修改既有檔案，寫入後每 20 ms 查詢一次直到查得到；重複 10 次 |

**為什麼要暖機？** .NET 第一次執行某段程式碼要做 JIT 編譯。不暖機，第一條查詢會特別慢，量到的是編譯時間而不是查詢時間。

**為什麼報 P95 而不只報平均？** 平均會被大量很快的查詢稀釋。P95 代表「每 20 次查詢中最慢的那 1 次」，更接近使用者感受到的「偶爾卡住」。

---

## 12.3 第一次調校：片語查詢從 118 ms 到 43 ms

第一次完整量測時，片語查詢的 P95 是 118 ms，超過限制。

當時的做法是對片語中的每個詞，解碼它在**所有**文件中的位置，再逐份比對。但大部分文件根本不可能含有這個片語。

改進（第 7 章已詳細說明）：

1. 先只讀 postings（不含位置），求出所有詞都出現的**候選文件**。
2. 解碼位置時，非候選文件的位置資料用 `SkipVarInts` 跳過，不做加總。
3. 把每個詞的位置存成緊湊的陣列（`PositionLists`），比對時用單向合併。

P95 降到 43 ms。這是**演算法層面**的改進：減少不必要的工作，而不是讓同樣的工作做得更快。

---

## 12.4 最壞情境：被審查者指出的盲點

量測的查詢是「從文件中隨機抽詞」組成的。審查時有人指出：

> 這實際上是「各種頻率的混合」，沒有刻意涵蓋最高頻詞。建議加一組「最高頻 N 個詞」的查詢，讓「P95 ≤ 100 ms」的結論不依賴語料的偶然。

這是對的。對片語查詢來說，最壞的情況是兩個詞都出現在**每一份**文件中：候選文件是全部，每份文件的位置都要解碼與比對。

### 加入最壞情境查詢

1. 從 3,000 份文件抽樣，統計每個詞的文件頻率，取前 200 名作為候選。
2. 對這 200 個候選，從索引中查出**精確**的 df，取最高的 25 個。
3. 25 個詞兩兩配對，得到 300 組，分別組成 AND 與片語查詢。

報告中同時列出詞頻分佈，說明兩組查詢的差別：

| | df / N |
|---|---|
| 第 1 到第 25 高的詞 | 全部 100%（出現在每一份文件） |
| 隨機查詢用詞的中位數 | 60% 到 78%（每次重建索引抽到的詞不同） |
| 隨機查詢用詞的第 95 百分位 | 100% |

### 結果

| 查詢 | P95（兩次量測） | 限制 |
|---|---|---|
| 隨機片語 | 48.8 / 54.0 ms | ≤ 100 ms |
| **最高頻片語** | **81.2 / 98.0 ms** | ≤ 100 ms |
| 最高頻 AND | 1.9 / 2.0 ms | ≤ 100 ms |

最壞情境的片語**符合規格，但餘裕只剩 2 到 19 ms**，最慢的單一查詢達 162 ms。換到稍慢的電腦就可能超標。

**教學重點**：

- 效能測試的查詢集要包含最壞情境，否則「符合規格」可能只是運氣。
- 同一份語料、同一台機器，兩次量測的 P95 就差了 17 ms。只量一次就下結論是危險的。

---

## 12.5 第二次調校：先量測，再平行化

### 不要猜，先拆解時間

在動手之前，先把最壞情境的片語查詢拆成幾個階段，分別計時。做法是在 `AccumulatePhrase` 的三個階段暫時加上 `Stopwatch`（量完就移除，不 commit），對 4 條最高頻片語各跑 10 次：

| 階段 | 每條平均 | 比例 |
|---|---|---|
| 讀 postings、建立候選 bitset | 0.6 ms | 2% |
| 解碼候選文件的位置 | 19.8 ms | 54% |
| 逐份文件比對起始位置 | 16.1 ms | 44% |
| 合計 | 36.9 ms | |

如果沒有這一步，很可能會去優化 postings 的讀取，但它只佔 2%。

### 哪些部分可以平行？

- **解碼位置**：每個詞的位置是一整串變長編碼，**一個詞之內**只能從頭依序解碼。但**不同的詞**彼此獨立，可以同時解碼。兩個詞的片語約可快 2 倍。
- **比對起始位置**：每份文件的比對只讀位置、只寫自己的 tf 欄位，彼此獨立。可以把文件切成多段同時處理。

這兩點都不需要改索引格式。

### 確認共用資源是安全的

平行讀取前，要確認讀取路徑上沒有共用的可變狀態：

- `IIndexInput.ReadAt` 的介面契約寫著「必須可以並行呼叫」（第 3 章）。正式實作用 `RandomAccess.Read`，測試的假實作用 `lock`，兩者都滿足。
- 快照與片段讀取器在建立後都是唯讀的，沒有延遲填入的快取。
- 每段比對使用自己的游標陣列；`tf` 陣列的每一格只會被一段寫入。

### 實作

```csharp
// src/LocalSeek.Core/Search/QueryExecutor.cs（節錄）
// A term's positions are one sequential stream, but different terms decode independently.
var decoded = new PositionLists[distinct.Count];
RunInParallel(distinct.Count, i =>
{
    _cancellation.ThrowIfCancellationRequested();
    var list = new PositionLists(_index.DocCount);
    _index.ForEachPositions(space, distinct[i], list.Add, doc => (candidates![doc >> 6] & (1UL << doc)) != 0);
    decoded[i] = list;
});

// Documents are independent: each range writes only its own tf entries.
var perTerm = terms.Select(t => decoded[distinct.IndexOf(t)]).ToArray();
var ranges = (candidates!.Length + PhraseRangeWords - 1) / PhraseRangeWords;   // 每段 64 個 ulong = 4,096 份文件
RunInParallel(ranges, range =>
{
    var cursors = new int[perTerm.Length];
    ...   // 只處理這一段的候選文件
});
```

### 平行化中的例外處理

`Parallel.For` 會把迴圈中丟出的例外包成 `AggregateException`。如果直接讓它往上傳，呼叫端原本 `catch (OperationCanceledException)` 的程式碼就會失效，取消會變成「錯誤」。所以多寫了一個小函式，把原本的例外拆出來重新丟出：

```csharp
/// Runs the body for 0..count-1 on the thread pool and rethrows the original exception rather than
/// an AggregateException, so callers still see OperationCanceledException or IndexCorruptException.
private void RunInParallel(int count, Action<int> body)
{
    try
    {
        Parallel.For(0, count, new ParallelOptions { CancellationToken = _cancellation }, body);
    }
    catch (AggregateException ex)
    {
        var inner = ex.Flatten().InnerExceptions;
        _cancellation.ThrowIfCancellationRequested();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner[0]).Throw();
    }
}
```

`ExceptionDispatchInfo.Capture(...).Throw()` 會保留原本的堆疊追蹤，除錯時才看得到錯誤真正發生的位置。

### 驗證

| 檢查 | 結果 |
|---|---|
| 特性測試：20,003 份文件、跨多段，片語次數與從原文計算的一致 | 修改前後都通過 |
| 完整測試 477 個 | 全部通過 |
| 並行與取消相關的測試重複 10 次 | 全部通過 |
| 同樣 4 條片語的平均時間 | 36.9 ms → 17.9 ms |

### 結果

| 查詢 | 平行化前 P95 | 平行化後 P95 |
|---|---|---|
| 最高頻片語 | 81.2 / 98.0 ms | **49.6 / 54.2 ms** |
| 最高頻片語的最慢單一查詢 | 141.6 / 162.4 ms | 71.5 / 65.0 ms |
| 隨機片語 | 48.8 / 54.0 ms | 30.7 / 34.4 ms |

其他指標（建立 118 s、記憶體 403 MB、開啟 168 到 175 ms、存檔到可搜尋平均 547 ms、最大 559 ms）不受影響。

### 這是架構決策

平行化改變了查詢的執行緒模型：一個查詢現在會佔用多個執行緒。在只有一個使用者、一次只跑一個查詢的桌面應用中這是合理的；在伺服器上同時處理大量查詢時，反而可能降低整體吞吐量。因為影響架構，這個改動是在**說明方案、取得同意後**才進行的。

---

## 12.6 誠實地報告

效能報告很容易「只報好看的數字」。本專案的報告遵守幾個原則：

1. **列出每一次量測**，不只挑最好的一次（兩次量測都寫進報告）。
2. **寫明餘裕**：「符合」與「勉強符合」是不同的結論。
3. **寫明已知的不足**：例如開啟時間是在作業系統快取已暖的情況下量的，沒有量重開機後的冷啟動。
4. **不寫沒有驗證的因果**。

最後一點有一個真實的例子。重新產生語料後，建立索引的時間從 109 秒變成 135 秒。撰寫報告時，原本寫了「因為字元集合變小，雙字詞重複得更頻繁，所以變慢」。但回頭檢查程式碼發現：新舊語料都是從 3,500 個字中抽取，**字元集合根本沒有變小**。這個解釋是錯的。

而且，第二次以同一份新語料重建索引時只花了 118 秒；舊語料兩次量測也分別是 109 與 117 秒。差距與量測本身的起伏同一個量級。最後報告改成只陳述事實：「這些差距與同一份語料兩次量測之間的起伏同一個量級，原因沒有進一步分析」。

**教學重點**：看到數字變化時，人會很自然地想出一個合理的解釋。寫進報告之前，要問「我驗證過這個解釋嗎？」如果沒有，就只寫觀察到的事實。

---

## 練習

1. 為什麼量測程式要固定處理器數量？如果不固定，在 24 執行緒的機器上量到的數據可以拿來證明「8 核心機器上符合規格」嗎？
2. 12.5 節的分析中，解碼位置的平行化最多只能讓兩詞片語快 2 倍。為什麼？三詞片語呢？
3. 在 `PhraseRangeWords` 改成 1（每段 64 份文件）與 1024（每段 65,536 份文件）時，分別預測效能會怎麼變，再實際量測。
4. 如果要讓最壞情境的片語查詢再快 10 倍，而且允許改索引格式，你會怎麼做？（提示：常見的高頻詞組合可以預先建立什麼？）
5. 用 `tests/LocalSeek.Perf` 的 `--skip-build` 模式量測兩次查詢延遲，比較兩次 P95 的差異，討論需要量幾次才能下結論。
