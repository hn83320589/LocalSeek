# 第 10 章　使用者介面與 MVVM

## 學習目標

- 用 MVVM 把畫面邏輯從 UI 框架中分離出來，使它可以單元測試。
- 實作「輸入即搜尋」：防抖、取消舊查詢、版本號防止舊結果覆蓋新結果。
- 用假時鐘與假排程器寫出確定性的 ViewModel 測試。
- 從一個偶發失敗的測試中學到：測試本身也可能有設計缺陷。

相關程式：`src/LocalSeek.App.ViewModels/`（`SearchViewModel.cs`、`Abstractions.cs`、`Services.cs`）、`src/LocalSeek.App/Views/SearchView.xaml(.cs)`、`tests/LocalSeek.Tests/ViewModels/`

---

## 10.1 MVVM

| 角色 | 職責 | 本專案 |
|---|---|---|
| Model | 資料與商業邏輯 | Core 與 Indexing |
| ViewModel | 畫面狀態與使用者操作的邏輯 | App.ViewModels（純 `net9.0`） |
| View | 呈現與輸入 | App（XAML） |

ViewModel 透過屬性變更通知（`INotifyPropertyChanged`）讓 View 更新。本專案用 CommunityToolkit.Mvvm 產生這些樣板程式碼：

```csharp
[ObservableProperty]
private string _queryText = "";           // 自動產生 QueryText 屬性與變更通知

[RelayCommand(CanExecute = nameof(HasSelection))]
private void OpenSelected() => RunShell(_shell.OpenFile, "開啟檔案");   // 自動產生 OpenSelectedCommand
```

ViewModel 放在**沒有 UI 引用**的專案中（第 3 章的分層規則），所以它的邏輯可以在一般的 xUnit 測試中執行，不需要啟動視窗。

### 鍵盤命令也放在 ViewModel

規格要求 Ctrl+K 聚焦搜尋、方向鍵選取、Enter 開啟、Alt+Enter 開啟所在資料夾。按鍵的**意義**放在 ViewModel，View 只負責把按鍵轉成平台無關的 `AppKey`：

```csharp
// src/LocalSeek.App.ViewModels/SearchViewModel.cs（節錄）
public bool HandleKey(AppKey key, AppKeyModifiers modifiers)
{
    switch (key)
    {
        case AppKey.K when modifiers == AppKeyModifiers.Control:
            FocusSearch();
            return true;
        case AppKey.Down:
            MoveSelection(1);
            return true;
        case AppKey.Enter when modifiers == AppKeyModifiers.Alt:
            RevealSelectedCommand.Execute(null);
            return SelectedResult is not null;
        case AppKey.Enter when modifiers == AppKeyModifiers.None:
            OpenSelectedCommand.Execute(null);
            return SelectedResult is not null;
        ...
    }
}
```

所以「Enter 開啟、Alt+Enter 顯示資料夾、沒有選取時 Enter 不做事」都可以直接測試（`Enter_opens_and_alt_enter_reveals_the_selected_file`、`Enter_without_selection_does_nothing`）。

---

## 10.2 輸入即搜尋的三個問題

使用者每打一個字就搜尋，會遇到三個問題：

1. **太頻繁**：打「台灣大學」會觸發四次搜尋，前三次都是浪費。
2. **舊查詢還在跑**：新查詢開始時，舊查詢應該停止，把 CPU 讓出來。
3. **結果亂序**：舊查詢（「台」，命中很多，比較慢）可能比新查詢（「台灣大學」，比較快）**晚**回來。如果直接顯示，畫面會出現舊查詢的結果。

### 解法

```csharp
// src/LocalSeek.App.ViewModels/SearchViewModel.cs（節錄）
partial void OnQueryTextChanged(string value)
{
    var version = ++_version;                 // 1. 每次輸入都遞增版本號
    _debounce?.Dispose();                     // 2. 取消上一個計時器
    _searchCancellation?.Cancel();            // 3. 立刻取消正在執行的搜尋
    if (string.IsNullOrWhiteSpace(value))
    {
        ClearResults();
        ...
        return;
    }

    // 4. 150 ms 後在 UI 執行緒上開始搜尋
    _debounce = _time.CreateTimer(_ => _ui.Post(() => _ = RunSearchAsync(value, version)),
                                  null, DebounceDelay, Timeout.InfiniteTimeSpan);
}

private async Task RunSearchAsync(string text, long version)
{
    if (version != _version)
    {
        return;                               // 計時器到了，但已經有更新的輸入
    }

    var cancellation = new CancellationTokenSource();
    _searchCancellation = cancellation;
    SearchOutcome outcome;
    try
    {
        outcome = await _search.SearchAsync(text, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        return;                               // 被取消是正常情況
    }
    ...
    if (version != _version || cancellation.IsCancellationRequested)
    {
        return;                               // 5. 結果回來時已經過時，丟棄
    }

    Apply(outcome);
    DisplayedVersion = version;
}
```

三個機制各解決一個問題：

| 問題 | 機制 |
|---|---|
| 太頻繁 | 防抖：150 ms 內沒有新的輸入才搜尋 |
| 舊查詢還在跑 | `CancellationTokenSource`：輸入時立刻 `Cancel()` |
| 結果亂序 | 版本號：結果回來時，版本不是最新就丟棄 |

為什麼有了取消還需要版本號？因為取消是**協作式**的：查詢要等到下一個檢查點才會發現被取消，在那之前它可能已經算完並回傳結果。版本號是最後一道防線，保證畫面上永遠是最新查詢的結果。

### 搜尋本身在背景執行緒

```csharp
// src/LocalSeek.App.ViewModels/Services.cs（節錄）
public Task<SearchOutcome> SearchAsync(string query, CancellationToken cancellation) =>
    Task.Run<SearchOutcome>(() =>
    {
        // 解析、綁定、取得快照、執行查詢
        ...
    }, cancellation);
```

`Task.Run` 讓查詢不佔用 UI 執行緒。`await` 回來時，在 UI 的同步內容（synchronization context）中繼續，所以 `Apply(outcome)` 更新集合時是在 UI 執行緒上。

---

## 10.3 可測試的 ViewModel

`SearchViewModel` 的所有依賴都是注入的：

```csharp
public SearchViewModel(ISearchService search, IPreviewService preview, IShellService shell,
                       IUiDispatcher ui, TimeProvider time, TimeZoneInfo timeZone)
```

測試中：

- `FakeTimeProvider`：手動推進時間，計時器到期時才觸發。
- `QueueDispatcher`：`Post` 的工作先放進佇列，測試呼叫 `RunPending()` 才執行，模擬「UI 執行緒稍後才處理」。
- `FakeSearchService`：記錄每次呼叫，回傳一個由測試控制何時完成的 `Task`。

有了這些替身，「舊結果不覆蓋新結果」可以寫得非常精確：

```csharp
// tests/LocalSeek.Tests/ViewModels/SearchViewModelTests.cs
[Fact]
public void Results_of_an_older_query_never_overwrite_newer_results()
{
    var older = Type("apple");
    var newer = Type("banana");

    newer.Complete(@"C:\banana.txt");                         // 新的先回來
    older.Complete(@"C:\apple1.txt", @"C:\apple2.txt");       // 舊的後回來

    Assert.Equal([@"C:\banana.txt"], _vm.Results.Select(r => r.Path));
    Assert.Equal(1, _vm.TotalHits);
    Assert.Equal(_vm.CurrentVersion, _vm.DisplayedVersion);
}
```

`Type` 是測試中的輔助方法：設定 `QueryText`、把假時鐘推進 150 ms、執行 UI 佇列。完成的順序完全由測試控制，所以這個測試**每次結果都一樣**。如果改用真實的執行緒與延遲，要製造「舊的比新的晚回來」只能靠運氣。

**教學重點**：並行程式的測試，關鍵在於**把「時間」與「排程」變成測試能控制的東西**。

---

## 10.4 一個 UI 錯誤：屬性變更的順序

截圖時發現，語法錯誤的位置顯示錯了：不管錯在哪裡，畫面都顯示「第 1 個字元」，也就是位置的預設值。

原因是 View 在收到 `ErrorMessage` 的變更通知時，就讀取 `ErrorPosition` 組成提示文字；而 ViewModel 原本先設定 `ErrorMessage`、再設定 `ErrorPosition`。`ErrorMessage` 的通知送出的那一刻，`ErrorPosition` 還沒更新。

目前的 View 程式碼如下，兩個屬性任一變更都會重新組成提示文字：

```csharp
// src/LocalSeek.App/Views/SearchView.xaml.cs（節錄）
case nameof(SearchViewModel.ErrorMessage):
case nameof(SearchViewModel.ErrorPosition):
    ErrorText.Text = _viewModel.ErrorMessage is null ? ""
        : $"查詢語法錯誤（第 {_viewModel.ErrorPosition + 1} 個字元）：{_viewModel.ErrorMessage}";
```

修正是調換順序，並加上註解與測試：

```csharp
case SearchOutcome.Invalid invalid:
    ClearResults();
    // Position first: views render the message when it changes.
    ErrorPosition = invalid.Position;
    ErrorLength = invalid.Length;
    ErrorMessage = invalid.Message;
```

```csharp
[Fact]
public void Error_position_is_already_set_when_the_message_changes()
{
    var seen = new List<int>();
    _vm.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(SearchViewModel.ErrorMessage))
        {
            seen.Add(_vm.ErrorPosition);    // 在 ErrorMessage 變更的那一刻讀取位置
        }
    };

    Type("台灣 AND (模型").Completion.SetResult(new SearchOutcome.Invalid("缺少右括號", 7, 1));

    Assert.Equal([7], seen);
}
```

這個測試把「View 依賴的順序」變成 ViewModel 的明確契約。

**教學重點**：多個相關屬性一起變更時，訂閱者可能在**中間狀態**讀到不一致的值。解法之一是讓「觸發更新的屬性」最後設定；另一個做法是把相關的值包成一個不可變物件，一次設定。

---

## 10.5 一個偶發失敗的測試

驗收 #20 要求「取消在 50 ms 內生效」。`CancellationLatencyTests` 在兩萬份文件的索引上，用一個很重的查詢實測取消延遲。

### 第一版：量到的不是想量的東西

第一版先跑一次查詢，確認它「夠慢」，然後在固定的 30 ms 時取消。在 CI 上它失敗了：第一次查詢**同時建立了延遲產生的兩萬份文件索引**，所以「夠慢」的檢查通過了；但真正的查詢很快，在 30 ms 之前就跑完，什麼都沒有被取消。

修正：先暖機一次（建立索引、JIT 編譯），基準只量查詢本身並要求至少 200 ms，改用更重的查詢，並在量到的基準時間的 1/4 時取消。

### 第二版：時間推算本身就不可靠

第二版在本機穩定通過，在 CI 上又偶發失敗：取消之後，查詢**沒有**丟出取消例外。

這次先驗證假設，而不是直接改：

- 懷疑是 JIT 分層編譯讓後續查詢變快。在本機連續量 12 次，都穩定在 230 到 320 ms，即使限制在 2 個處理器也一樣。**假設不成立**。
- 檢查查詢程式碼的每個階段，都有檢查取消，也沒有「被取消卻正常回傳」的路徑。**產品沒有問題**。

結論是測試設計的缺陷：用「一次量測的時間」推算「什麼時候查詢一定還在跑」，在負載變動的 CI 機器上不可靠。任何干擾（GC 暫停、其他程序搶 CPU）讓那一次基準量測變長，之後的取消就可能太晚。

### 第三版：不再推算時間

改成直接觀察「查詢已經開始」：

```csharp
// tests/LocalSeek.Tests/ViewModels/CancellationLatencyTests.cs（節錄）
/// Delegates to the real index and signals the first postings read.
private sealed class StartSignallingReader(IIndexReader inner) : IIndexReader
{
    public ManualResetEventSlim Started { get; } = new();

    public void ForEachPositions(TermSpace space, string term, PositionsCallback callback, Func<int, bool>? wanted = null)
    {
        Started.Set();                          // 查詢開始讀索引了
        inner.ForEachPositions(space, term, callback, wanted);
    }
    // ... 其他成員直接轉給 inner ...
}
```

```csharp
var search = service.SearchAsync(HeavyQuery, cancellation.Token);
Assert.True(reader.Started.Wait(TimeSpan.FromSeconds(10)), "The search never started reading the index.");
var stopwatch = Stopwatch.StartNew();
await cancellation.CancelAsync();               // 收到訊號才取消：查詢一定還在跑
var outcome = await Record.ExceptionAsync(() => search);
Assert.True(outcome is OperationCanceledException, $"Search completed without observing cancellation ...");
latencies.Add(stopwatch.ElapsedMilliseconds);
```

這是一個**裝飾器**（decorator）：包住真正的索引讀取器，只多做一件事（發出訊號）。因為 Core 透過 `IIndexReader` 介面讀取索引，測試才能插入這一層。第 3 章「留接縫」的好處，在這裡又出現一次。

改完後在本機重複執行 20 次（其中 10 次限制在 2 個處理器）全部通過，CI 也恢復綠燈。

**教學重點**：

1. 偶發失敗的測試不能靠「重跑一次」解決，要找出它依賴了什麼不確定的東西。
2. 修正前先**驗證假設**。第一個猜測（JIT）是錯的，如果照它修，問題不會消失。
3. 測試中避免「睡一下應該就好了」的時間推算。能觀察到事件，就等待事件。

---

## 練習

1. 如果移除版本號檢查，只保留取消，`Results_of_an_older_query_never_overwrite_newer_results` 會失敗嗎？為什麼？實際改改看。
2. 防抖時間 150 ms 是怎麼選的？太短或太長分別會有什麼問題？
3. 10.4 節提到「把相關的值包成一個不可變物件」。試著把 `ErrorMessage`、`ErrorPosition`、`ErrorLength` 改成一個 `QueryError` record，View 要怎麼改？
4. `CancellationLatencyTests` 取 5 次的**中位數**判斷是否 ≤ 50 ms，而不是最大值。這樣做的理由是什麼？有什麼風險？
5. 寫一個測試：使用者輸入查詢後，在 100 ms 時清空搜尋框，確認不會觸發任何搜尋。
