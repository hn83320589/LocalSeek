# 第 3 章　架構設計

## 學習目標

- 用分層與單向依賴組織一個中型系統，並用測試**強制**依賴規則。
- 理解「接縫」（seam）與依賴注入，以及它們如何讓故障注入、假時鐘、假檔案系統成為可能。
- 設計組合根（composition root），讓 UI 只是一層薄殼。
- 規劃並行模型：單一寫入者、多個讀者、背景工作與 UI 執行緒。
- 依層級制定錯誤處理策略。

---

## 3.1 分層與依賴方向

```
LocalSeek.sln
├─ src/
│  ├─ LocalSeek.Core/              net9.0  分詞、查詢、計分、片段格式、合併、摘要
│  ├─ LocalSeek.Indexing/          net9.0  爬檔、抽取、編碼偵測、監看、增量索引
│  ├─ LocalSeek.App.ViewModels/    net9.0  畫面邏輯（不含任何 UI 元件）
│  └─ LocalSeek.App/               Uno.Sdk 視窗、XAML、平台服務
├─ tests/
│  ├─ LocalSeek.Tests/             xUnit：單元、性質、故障注入、壓力
│  └─ LocalSeek.Perf/              效能量測
└─ tools/LocalSeek.CorpusGen/      產生測試語料
```

依賴只能往左指：

```
Core  ◄──  Indexing  ◄──  App.ViewModels  ◄──  App (Uno)
  ▲            ▲                ▲
  └────────────┴──── Tests ─────┘
```

每一層的職責：

| 層 | 知道什麼 | 不知道什麼 |
|---|---|---|
| Core | 文字、詞項、索引格式、計分 | 檔案從哪裡來、有沒有 UI |
| Indexing | 檔案系統、編碼、監看事件 | 畫面長什麼樣子 |
| App.ViewModels | 使用者操作的流程與狀態 | 用的是哪一套 UI 框架 |
| App | XAML、視窗、Windows API | 搜尋演算法 |

### 為什麼 Core 不能知道檔案系統？

Core 處理的是**資料與演算法**：給它一段文字，它分詞；給它一個索引與查詢，它算分數。這些都是純函式（或接近純函式），不需要碰檔案系統就能完整測試。

唯一的例外是索引檔本身：Core 要讀寫片段與 manifest。但它不直接呼叫 `File.WriteAllBytes`，而是透過 `IIndexStorage` 介面（3.3 節）。

### 用測試強制依賴規則

依賴規則寫在文件裡，很快就會被違反。本專案用測試把它變成**會失敗的檢查**：

```csharp
// tests/LocalSeek.Tests/Architecture/LayeringTests.cs（節錄）
[Fact]
public void Core_does_not_reference_other_localseek_assemblies()
{
    var references = Assembly.Load("LocalSeek.Core").GetReferencedAssemblies();
    Assert.DoesNotContain(references, r => r.Name!.StartsWith("LocalSeek.", StringComparison.Ordinal));
}

[Fact]
public void Indexing_does_not_reference_view_models() { ... }

[Theory]  // Core、Indexing、App.ViewModels 都不得引用 UI 或 Windows 專用組件
public void Pure_assemblies_do_not_reference_ui_or_windows_specific_assemblies(string assemblyName) { ... }
```

這類測試叫做**架構測試**（architecture test / fitness function）。它們成本極低，卻能防止架構在多次修改後悄悄腐化。

---

## 3.2 接縫與依賴注入

**接縫**（seam）是程式中「可以在不改程式碼的情況下替換行為」的地方。在 C# 中最常見的接縫是介面加建構子注入。

本專案的每一個外部依賴，都有一個接縫：

| 介面 | 所在層 | 正式實作 | 測試替身 | 讓哪些測試成為可能 |
|---|---|---|---|---|
| `IIndexStorage` | Core | `FileSystemIndexStorage` | `FakeIndexStorage` | 在任一 I/O 操作注入故障、模擬當機與 torn write |
| `ISourceFileSystem` | Indexing | `PhysicalSourceFileSystem` | `FakeSourceFileSystem` | 接合點迴圈、鎖定檔、權限不足、讀取中被修改 |
| `IFileWatcherFactory` | Indexing | `PhysicalFileWatcherFactory` | `FakeFileWatcherFactory` | 手動送出事件、模擬緩衝區溢位 |
| `TimeProvider` | 全部 | `TimeProvider.System` | `FakeTimeProvider` | 防抖、重試間隔、時間篩選，不必真的等待 |
| `IUiDispatcher` | ViewModels | `DispatcherQueueDispatcher` | 假排程器 | 驗證結果是在 UI 執行緒套用 |
| `IShellService` | ViewModels | `ShellService` | 記錄呼叫的假物件 | Enter 與 Alt+Enter 的行為 |

### 範例：`IIndexStorage`

```csharp
// src/LocalSeek.Core/Storage/IIndexStorage.cs
/// Every byte of index I/O goes through this interface so tests can inject faults and simulate
/// crashes at any operation. File names are flat (no directories).
public interface IIndexStorage
{
    IIndexOutput Create(string name);
    IIndexInput OpenRead(string name);
    void AtomicRename(string source, string destination);
    void Delete(string name);
    IReadOnlyList<string> List();
}

public interface IIndexOutput : IDisposable
{
    long Position { get; }
    void Write(ReadOnlySpan<byte> data);
    void Flush();          // Makes everything written so far durable.
}

/// Random-access reader. ReadAt must be safe to call concurrently.
public interface IIndexInput : IDisposable
{
    long Length { get; }
    void ReadAt(long offset, Span<byte> buffer);
}
```

幾個值得注意的設計：

- **介面很小**：只有建立、讀、原子改名、刪除、列舉。介面越小，假實作越容易寫對，故障注入也越容易涵蓋所有操作。
- **`Flush` 的語意寫在註解裡**：「讓目前為止寫入的內容持久化」。假實作就依這個語意，模擬當機時丟掉未 Flush 的資料（第 8 章）。
- **`ReadAt` 必須可並行呼叫**：這是一份**契約**。後來片語查詢改成平行讀取時（第 12 章），就是靠這條契約確認不需要加鎖。正式實作用 `RandomAccess.Read`，假實作用 `lock`，兩者都滿足。

### 範例：`TimeProvider`

規格要求所有「現在時間」都透過 `TimeProvider`。這讓防抖測試可以這樣寫：

```csharp
// tests/LocalSeek.Tests/Pipeline/IndexingEngineTests.cs（節錄）
f.Event(FileChangeKind.Changed, path);
f.Time.Advance(TimeSpan.FromMilliseconds(400));   // f.Time 是 FakeTimeProvider
await f.Engine.ProcessAsync();
Assert.Equal(1, f.Engine.PendingCount);           // 防抖 500 ms 還沒到，仍在等待
```

測試在幾毫秒內完成，而且結果完全確定。如果直接用 `DateTime.Now` 與 `Task.Delay`，測試就得真的等待，而且會因為機器忙碌而偶爾失敗。

**教學重點**：「可測試性」不是寫完程式再想辦法測，而是在架構階段就決定**哪裡要留接縫**。判斷標準很簡單：凡是**不確定**（時間、執行緒排程）、**慢**（磁碟、網路）或**難以製造**（當機、鎖定、權限錯誤）的東西，都應該在介面後面。

---

## 3.3 組合根

所有物件在哪裡被 `new` 出來？答案是**組合根**（composition root）：程式中唯一知道「正式實作是誰」的地方。

本專案的組合根分兩層：

```csharp
// src/LocalSeek.App/App.xaml.cs（節錄）：只負責提供平台相關的實作
_host = new AppHost(
    AppPaths.FromEnvironment(),
    new PhysicalSourceFileSystem(),
    new PhysicalFileWatcherFactory(),
    TimeProvider.System,
    new DispatcherQueueDispatcher(DispatcherQueue.GetForCurrentThread()),
    new ShellService(),
    TimeZoneInfo.Local);

MainWindow.Content = new MainPage(_host.Main);
_host.Start();
```

```csharp
// src/LocalSeek.App.ViewModels/AppHost.cs（節錄）：組裝其餘所有物件
_writer = IndexWriter.Open(_storageFactory());
var search = new SearchViewModel(
    new IndexSearchService(() => _writer.AcquireSnapshot(), timeZone),
    new PreviewService(new DocumentPreviewer(fileSystem, _extractor)),
    shell, ui, time, timeZone);
// ... 設定、診斷、狀態列的 ViewModel ...
Main = new MainViewModel(search, new StatusViewModel(...), diagnostics, settings);
```

`AppHost` 放在**沒有 UI 的** App.ViewModels 專案中，因此整個應用程式的組裝與啟動流程都可以在測試中用假實作執行。`ViewModels/AppHostTests` 就驗證了三件事：從根資料夾建立索引並透過 ViewModel 搜尋、變更根資料夾後重新索引並移除舊根資料夾的文件、根資料夾不存在與設定檔損毀時正確提示。Uno 專案只剩下 XAML 與幾個平台服務。

---

## 3.4 並行模型

搜尋引擎同時有很多事在進行。先把「誰在什麼執行緒做什麼」畫清楚：

| 執行者 | 做什麼 | 與其他執行者的關係 |
|---|---|---|
| UI 執行緒 | 處理輸入、更新畫面 | 只透過 `IUiDispatcher` 接收結果 |
| 背景索引服務（`IndexingService`） | 等待防抖到期、呼叫引擎處理、觸發合併 | 索引的新增、刪除、改名與提交都由它驅動的引擎發出 |
| 抽取 worker（多個） | 讀檔、解碼、分詞 | 結果交給寫入者，不直接寫索引 |
| 查詢（每次搜尋一個 `Task`） | 取得快照、執行查詢 | 只讀快照，不與寫入者競爭 |
| 合併 | 把多個片段合成一個 | 重活在鎖外做，只有提交時取得寫入者的鎖 |

核心原則是**單一寫入者、多個讀者**：

- `IndexWriter` 內部用一個 `lock` 保護所有可變狀態，同一時間只有一個寫入動作。
- 讀者拿到的是 `IndexSnapshot`：一個**不可變**的物件，代表某一次提交。寫入者之後的任何動作都不影響它。

這個模型讓查詢完全不需要鎖，也讓「搜尋期間看到一致的結果」（驗收 #14、#17）變成結構上的保證，而不是靠小心翼翼的同步。第 8 章會詳細說明快照的實作。

---

## 3.5 錯誤處理策略

錯誤處理也要分層設計。不同來源的錯誤，處理方式完全不同：

| 錯誤來源 | 例子 | 處理方式 | 理由 |
|---|---|---|---|
| 使用者的檔案 | 鎖定、權限不足、編碼無法判定 | 寫入診斷紀錄，略過該檔，繼續 | 單一檔案不得中止整體索引 |
| 使用者的查詢 | 語法錯誤 | 丟出 `QuerySyntaxException`（含位置），UI 顯示 | 錯誤是正常的使用情境 |
| 索引檔損毀 | CRC 不符、版本不符、片段遺失 | 丟棄受影響的片段，排程差異掃描補回 | 索引是可以重建的快取 |
| 索引 I/O 中途失敗 | 磁碟滿、寫入錯誤 | 寫入者標記為失敗、拒絕後續操作；由 `AppHost` 重新開啟 | 失敗後的記憶體狀態不可信，重開最安全 |
| 程式錯誤 | 不變式被違反 | 丟出例外，讓測試失敗 | Fail fast |

「索引 I/O 中途失敗」那一列值得特別說明：

```csharp
// src/LocalSeek.Core/Index/IndexWriter.cs（節錄）
public long Commit()
{
    lock (_lock)
    {
        ThrowIfUnusable();
        try
        {
            return CommitLocked();
        }
        catch
        {
            _failed = true;   // 記憶體中的狀態可能已與磁碟不一致
            throw;
        }
    }
}
```

寫到一半失敗時，記憶體中的資料結構可能已經部分更新，硬要「修好」很容易出錯。改成**拒絕後續操作、重新開啟**，就把問題化約成「從磁碟上最後一個有效提交復原」，而那正是中斷安全機制本來就要處理的情況。

**教學重點**：「不要無聲吞掉例外」不等於「到處 try-catch」。關鍵在於**在正確的層級處理**：知道如何復原的那一層才捕捉，其他層讓例外往上傳。

---

## 3.6 主要設計取捨

| 決策 | 理由 | 代價 |
|---|---|---|
| 文件與內容分離（Doc → Content 參照） | 改名或移動時內文不重新抽取、不重新分詞 | 合併時要處理跨片段參照 |
| 統計值在快照當下重算，不預存 df | 不可能用到過時的 N、avgdl、df | 每次查詢多掃一次 postings |
| 索引只存位置，摘要時重新抽取 | 索引小約一半 | 預覽要再讀一次檔案 |
| term-at-a-time 加稠密陣列 | 簡單、容易驗證，10 萬份文件內夠快 | 百萬級時需改用 doc-at-a-time 或 skip list |
| 保留最近兩個提交 | 最新 manifest 損毀時可以退回前一版 | 被取代的片段晚一個提交才刪除 |
| 開啟時只驗證小區段的 CRC | 開啟時間約 150 到 200 ms | 大區段損毀要等到完整驗證才會發現 |

每一個取捨都寫明**代價**。沒有代價的設計通常代表還沒想清楚。

---

## 練習

1. 如果有人在 Core 中加了 `using LocalSeek.Indexing;` 並使用其中的類別，會發生什麼事？實際試試看，並執行 `LayeringTests`。
2. 假設要支援「從 Google Drive 索引文件」，需要新增或修改哪些介面？哪些層完全不需要修改？
3. `AppHost` 為什麼放在 App.ViewModels，而不是 App？列出兩個好處。
4. 3.5 節說 I/O 失敗後「拒絕後續操作、重新開啟」。請思考：如果改成在記憶體中回復到失敗前的狀態，需要記錄哪些資訊？這樣做有什麼風險？
