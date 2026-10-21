# 第 8 章　一致性與中斷安全

## 學習目標

- 設計一個「任何時間點中止都不會損壞」的提交協定。
- 用不可變快照與參考計數，實現讀寫並行時的隔離。
- 偵測並局部修復索引損毀。
- 理解分層合併（tiered merge）為什麼讓片段數以對數成長，以及合併與寫入並行時如何不遺失變更。
- 用故障注入，系統化地驗證每一個 I/O 操作點。

相關程式：`src/LocalSeek.Core/Index/`（`IndexWriter.cs`、`IndexSnapshot.cs`、`Manifest.cs`、`FileReferences.cs`、`MergePolicy.cs`、`Segments/SegmentMerger.cs`）、`tests/LocalSeek.Tests/Fakes/FakeIndexStorage.cs`、`tests/LocalSeek.Tests/Index/FaultInjectionTests.cs`

---

## 8.1 問題

需求說：「程式在任何時間點被中止後重新開啟，索引必須是一致的（提交前或提交後的狀態），不得需要手動修復」。

「任何時間點」包括：

- 寫片段寫到一半；
- 片段寫完了，manifest 還沒寫；
- manifest 寫到一半；
- 提交完成了，正在刪除舊檔案；
- 合併進行到一半；
- 甚至是**上一次當機後的復原過程中**又當機。

逐一想每種情況很容易漏掉。更好的做法是設計一個協定，讓「一致」由結構保證，再用自動化測試覆蓋所有操作點。

---

## 8.2 原子提交協定

```
1. 寫入新的片段檔與墓碑檔 → Flush（持久化）
2. 寫入 manifest_N.tmp → Flush
3. 原子改名 manifest_N.tmp → manifest_N.lsm      ← 提交點
4. 刪除不再被引用的舊檔案（盡力而為）
```

關鍵在第 3 步：**只有改名成功的那一刻，新狀態才存在**。

- 在提交點之前當機：新寫的片段檔沒有被任何 manifest 引用，是孤兒，下次開啟時刪除。磁碟上最新的有效 manifest 仍是 N−1，看到的是**提交前**的狀態。
- 在提交點之後當機：manifest_N 已經存在，看到的是**提交後**的狀態。第 4 步沒做完只是留下一些多餘的檔案，下次開啟時清掉。

```csharp
// src/LocalSeek.Core/Index/Manifest.cs（節錄）
internal void Write(IIndexStorage storage)
{
    var name = IndexFileNames.Manifest(Generation);
    var temp = name + IndexFileNames.TempSuffix;
    var json = JsonSerializer.SerializeToUtf8Bytes(this);
    // ... 緩衝區：json | json 長度 | CRC32 | magic "LSEKMAN\0" ...
    using (var output = storage.Create(temp))
    {
        output.Write(buffer.Span);
        output.Flush();                         // 2. 持久化
    }

    storage.AtomicRename(temp, name);           // 3. 提交點
}
```

正式實作中，`Flush` 是 `FileStream.Flush(flushToDisk: true)`，`AtomicRename` 是 `File.Move(..., overwrite: true)`。在 NTFS 上同一磁碟內的改名是原子的：要嘛完全成功，要嘛完全沒發生。

### 為什麼 Flush 一定要在改名之前？

作業系統會延後寫入。如果沒有先 Flush，可能發生「改名已經持久化，但 manifest 的內容還在記憶體快取中」的情況。這時當機，磁碟上會有一個名字正確但內容是空的或殘缺的 manifest。manifest 尾端的 CRC 與 magic 能偵測這種情況，但那已經是「需要退回前一版」的損毀情境，不是正常的提交前狀態。

**教學重點**：持久化的順序必須是「先資料、後指標」。這個原則也出現在資料庫的 write-ahead log、檔案系統的日誌等各種系統中。

### 提交點之後的程式碼

```csharp
// src/LocalSeek.Core/Index/IndexWriter.cs（節錄）
manifest.Write(_storage);

// Committed: from here on only in-memory bookkeeping and best-effort cleanup.
foreach (var (id, generation) in newDeletionGenerations) { ... }
...
_generation = manifest.Generation;
RetainCommit(manifest.Files().ToArray());
PublishSnapshot();
```

這一行註解把程式分成兩半：前半段的任何失敗都等於「沒有提交」；後半段的失敗不影響磁碟上的正確性。在程式碼中標出提交點，能幫助讀者推理中斷安全。

---

## 8.3 開啟與復原

開啟索引時：

1. 刪除所有 `.tmp` 檔（寫到一半的東西）。
2. 列出所有 manifest，**由新到舊**嘗試讀取，使用第一個能通過 CRC 與版本檢查的。
3. 載入它引用的片段；任何片段有問題，只移除那個片段（8.5 節）。
4. 刪除所有沒被選中的 manifest 引用的檔案（孤兒）。

```csharp
// src/LocalSeek.Core/Index/IndexWriter.cs（節錄）
Manifest? chosen = null;
foreach (var (name, _, _) in manifests)        // 依 generation 由大到小
{
    try
    {
        chosen = Manifest.Read(_storage, name);
        break;
    }
    catch (IndexFormatVersionException ex) { issues.Add(...FormatVersionMismatch...); }
    catch (IndexCorruptException ex)       { issues.Add(...ManifestUnreadable...); }
}
```

### 保留最近兩個提交

如果只保留最新的提交，一旦最新的 manifest 損毀（例如磁碟位元錯誤），就沒有可以退回的版本。所以寫入者保留最近**兩個**提交引用的所有檔案：最新的不能用時，可以退回前一個。

代價是舊片段要晚一個提交才刪除。大合併之後，磁碟上會暫時多出一份舊資料。這是 README 中記錄的取捨。

---

## 8.4 快照隔離與參考計數

### 快照

搜尋開始時取得一個 `IndexSnapshot`：

```csharp
// src/LocalSeek.Core/Index/IndexSnapshot.cs（節錄）
/// An immutable, reference-counted view of one commit. ... Commits and merges that happen after
/// the snapshot was acquired never change what it returns, and its files are not deleted until
/// it is disposed.
public sealed class IndexSnapshot : IIndexReader, IDisposable
```

快照建立時：

- 持有一組片段讀取器（片段檔不可變，所以可以共用）。
- 持有每個片段墓碑 bitset 的**複本**（寫入者之後的刪除不影響它）。
- 為所有現存文件編上連續的序號，並算好 N、Σlen 與每份文件的長度。

所以，快照建立之後，無論寫入者做了多少提交、合併，快照看到的永遠是同一個狀態（驗收 #14）。

### 檔案什麼時候可以刪除？

合併之後，舊片段已經不在最新的提交中。但如果有一個查詢還在用舊快照讀舊片段，現在刪除就會讓查詢失敗。

解法是**參考計數**（reference counting），由 `FileReferences` 管理。持有檔案的有三種：

| 持有者 | 何時增加 | 何時減少 |
|---|---|---|
| 寫入者的工作集合 | 片段加入時 | 片段被移除時 |
| 保留的提交（最近兩個） | 提交時 | 被第三新的提交擠出時 |
| 快照 | 快照建立時 | 快照的最後一個使用者 `Dispose` 時 |

計數歸零才真的刪除檔案。刪除失敗（例如 Windows 上檔案還被開著）會記下來，之後再試。

快照本身也有參考計數。`AcquireSnapshot` 把目前快照的計數加 1 回傳；每個使用者用完 `Dispose` 減 1。加 1 的操作用 compare-and-swap，避免與「計數剛好歸零」競爭：

```csharp
internal bool TryAddRef()
{
    while (true)
    {
        var current = Volatile.Read(ref _refCount);
        if (current == 0)
        {
            return false;                       // 已經釋放，不能再復活
        }

        if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
        {
            return true;
        }
    }
}
```

測試 `SnapshotIsolationTests.Superseded_files_are_deleted_only_after_the_last_old_reader_releases` 驗證了「最後一個舊讀者釋放後才刪除」。

---

## 8.5 損毀處理：只重建受影響的部分

| 情況 | 處理 |
|---|---|
| 最新 manifest 損毀或版本不符 | 退回前一個有效的提交 |
| 所有 manifest 都不能用 | 回報「需要完整重建」，從空索引開始 |
| 片段檔遺失、CRC 不符、版本不符 | 只從索引中移除這個片段（以及內容存在這個片段的文件），其他片段照常使用 |
| 墓碑檔損毀 | 移除該片段 |

每一種情況都會產生一筆 `IndexIssue`，App 啟動時以通知顯示（驗收 #19 的「明確提示」）。

被移除的片段中的文件怎麼補回來？索引服務在啟動時一定會做一次**差異掃描**：比對磁碟上的檔案與索引中的文件。被移除的文件在索引中不存在，但磁碟上存在，所以會被當成「新檔案」重新索引。不需要特別的修復程式，既有的增量索引機制就完成了「只重建受影響部分」。

**教學重點**：讓錯誤復原**重用正常路徑**。特別寫的修復程式很少執行，很容易有 bug；正常路徑每天都在跑，經過充分測試。

---

## 8.6 合併

### 為什麼需要合併

每次提交都可能產生一個新片段。如果不合併，片段會越來越多，查詢每個詞都要查每個片段，開啟時也要讀更多檔案。被刪除的文件也一直佔著空間。

### 分層策略

```csharp
// src/LocalSeek.Core/Index/MergePolicy.cs（節錄）
public int MergeFactor { get; init; } = 10;
public int MinSegmentDocs { get; init; } = 1000;

public int LevelOf(int liveDocs) =>
    liveDocs <= MinSegmentDocs ? 0 : (int)Math.Floor(Math.Log((double)liveDocs / MinSegmentDocs, MergeFactor)) + 1;
```

- 1000 份以下的片段是第 0 層，1000 到 1 萬是第 1 層，1 萬到 10 萬是第 2 層，以此類推。
- 同一層累積到 10 個片段，就合併成一個上一層的片段。
- 另外，被刪除的文件超過 30% 的片段會被單獨重寫，清除墓碑。

每一層最多 9 個片段，層數是 O(log N)，所以片段總數 ≤ 9 × (層數 + 1) = **O(log N)**（驗收 #16）。

這和二進位計數器很像：每層最多 9 個，滿 10 個就進位到上一層。每份文件被重寫的次數也是 O(log N)，所以合併的總成本是 O(N log N)。

### 合併與寫入同時進行

合併一個大片段可能要好幾秒。如果合併期間鎖住寫入者，使用者存檔就要等。所以合併分三段：

```
1. 取得鎖：決定要合併哪些片段（計畫），然後放開鎖
2. 不持有鎖：讀取輸入片段，寫出新片段（耗時的部分）
3. 取得鎖：把新片段換進去，提交
```

第 2 步進行時，寫入者可能已經刪除或改名了某些正在被合併的文件。第 3 步必須處理這些變化：

```csharp
// src/LocalSeek.Core/Index/IndexWriter.cs（節錄：CommitMergeLocked）
// Carry over deletions that happened while the merge was running.
var deleted = new BitArray(result.DocCount);
for (var i = 0; i < plan.States.Count; i++)
{
    for (var d = 0; d < result.DocMap[i].Length; d++)
    {
        var mapped = result.DocMap[i][d];              // 舊片段的 d 號文件 → 新片段的編號
        if (mapped >= 0 && plan.States[i].Deleted[d])  // 合併期間被刪除了
        {
            deleted[mapped] = true;                    // 在新片段上也標記刪除
        }
    }
    ...
}
```

合併器回傳每個輸入片段的「舊編號 → 新編號」對照表（`DocMap`），第 3 步用它把合併期間新增的墓碑**帶到**新片段上。

### 內容參照讓合併更複雜

第 2 章的「文件與內容分離」在這裡付出代價：片段 A 中的 Doc 可能指向片段 B 中的 Content。如果只合併 B 而不合併 A，Content 搬到新片段後，A 的 Doc 就指向不存在的東西。

處理方式：

- **計畫時**：如果選中的片段中有某個 Content 被片段外的 Doc 引用，就把那個 Doc 所在的片段也納入（反覆擴大直到封閉，稱為 closure）。
- **提交時**：如果合併期間又出現了新的跨片段參照：
  - 參照還在記憶體中（尚未寫成片段），就把它改指向新片段；
  - 參照已經提交了，就**放棄這次合併**，下次再試。

設計文件原本的構想是「讓合併結果收養片段外的文件」。實作時改成 closure 加放棄重試，因為邏輯簡單得多，也不需要在別的片段重新分詞檔名（記錄在 [architecture.md](../architecture.md) 第 12 節）。

---

## 8.7 用故障注入驗證

### 假的儲存裝置

`FakeIndexStorage` 在記憶體中模擬檔案系統，並提供兩個能力：

- **計數與注入**：每個 Create、Write、Flush、AtomicRename、Delete 都計數；設定 `FailAtOperation = k`，第 k 個操作就丟例外。
- **模擬當機**：`SimulateCrash()` 丟掉每個檔案中**沒有 Flush 的部分**。傳入一個亂數產生器時，改成保留隨機長度的前綴，模擬寫到一半的 **torn write**。

### 測試迴圈

```csharp
// tests/LocalSeek.Tests/Index/FaultInjectionTests.cs（節錄）
var (before, after, setupOps, totalOps) = Reference(scenario);   // 先跑一次，記下操作前後的狀態與操作數

for (var k = setupOps + 1; k <= totalOps; k++)                   // 對受測操作中的每一個 I/O
{
    var storage = new FakeIndexStorage();
    RunWithFault(storage, scenario, k);                          // 在第 k 個操作失敗
    storage.SimulateCrash(tornWrites ? new Random(k) : null);    // 當機

    using var reopened = IndexWriter.Open(storage, SmallBuffer); // 重新開啟
    var dump = LogicalDump.Of(reopened);
    Assert.True(dump == before || dump == after, $"Fault at operation {k} ... produced a third state.");
    Assert.False(reopened.OpenReport.HasIssues, ...);            // 不需要任何修復
    ...
}
```

- 涵蓋 7 種情境：新增（adds）、刪除（deletes）、改名（renames）、混合（mixed）、清空片段（drop-segment）、合併（merge），以及 RAM 緩衝區滿了自動寫出片段（auto-flush）。
- 每種情境分「有」與「沒有」torn write 各跑一次。
- **逐一**在每個 I/O 操作點注入故障，不是隨機抽樣。
- `LogicalDump` 把索引的邏輯內容（所有文件、詞、postings）輸出成字串，用來比較「是不是同一個狀態」，與實體檔案的排列無關。

另外，`Recovery_after_a_crash_is_itself_crash_safe` 測試「復原過程中又當機」：先在受測操作中當機，再對**復原過程**的每個 I/O 操作注入故障，確認最終仍然一致。

**教學重點**：「中斷安全」無法靠人工推理證明完整。可行的做法是：

1. 讓所有 I/O 經過一個介面（第 3 章）；
2. 寫一個能精確模擬持久化語意的假實作；
3. 窮舉每一個操作點。

這套測試能抓到人工推理很難發現的問題，例如「Flush 了資料但忘了 Flush 墓碑檔」。

---

## 練習

1. 如果把 8.2 節的第 2 步與第 3 步對調（先改名，再寫入內容並 Flush），在哪個時間點當機會出問題？會看到什麼？
2. 「保留最近兩個提交」改成「只保留最新一個」，哪一個故障注入或損毀測試會失敗？實際改改看（`IndexWriterOptions.RetainedCommits`）。
3. 用二進位計數器的類比，說明為什麼每份文件在分層合併中最多被重寫 O(log N) 次。
4. 8.6 節說合併期間的跨片段參照若已經提交，就放棄合併。這種情況在什麼操作順序下會發生？放棄會不會導致合併永遠做不成？
5. 在 `FaultInjectionTests` 中新增一個情境：「先刪除一份文件，再新增一份同路徑的文件，然後提交」。執行測試，看看共有多少個 I/O 操作點被測試。
