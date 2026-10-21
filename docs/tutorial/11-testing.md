# 第 11 章　測試策略

## 學習目標

- 依照風險選擇測試種類，而不是只寫單元測試。
- 用 TDD 的「紅、綠、重構」節奏開發與修 bug。
- 用參考解（test oracle）驗證複雜演算法，並理解「獨立實作」的意義。
- 寫出可重現的性質測試（property-based test）。
- 設計壓力測試的不變式，並避免「空洞地通過」。
- 從本專案實際遇到的測試問題中，學會辨認不好的測試。

---

## 11.1 測試組合

本專案共 477 個自動化測試，約 10 秒跑完。依種類：

| 種類 | 代表 | 驗證什麼 | 對應驗收 |
|---|---|---|---|
| 單元測試 | `QueryParserTests`（71）、`FilterTests`（37）、`GlobTests`（35）、`ExtractionTests`（37） | 單一元件在各種輸入下的行為 | 1、3、4、8、21 |
| 預期檔測試 | `TokenizerExpectedFileTests`（30） | 逐項對照人工寫好的預期結果 | 1 |
| 性質測試 | `Properties/*`（12 組） | 隨機輸入下，與參考解一致或滿足不變式 | 2、5、6、7、9、10、11、16、18 |
| 故障注入 | `FaultInjectionTests`（16） | 每個 I/O 操作點當機後仍一致 | 15 |
| 損毀與隔離 | `CorruptionTests`（12）、`SnapshotIsolationTests`（4） | 損毀偵測、快照不受寫入影響 | 14、19 |
| 壓力測試 | `ConcurrencyStressTests` | 寫入、合併、搜尋同時進行 | 17 |
| ViewModel 測試 | `SearchViewModelTests`（17） | 假時鐘與假排程器下的 UI 邏輯 | 20 |
| 實機測試 | `PhysicalFileSystemTests`（4） | 真實磁碟上的接合點、長路徑、鎖定檔 | 13 |
| 範例語料 | `SampleCorpusTests`（17） | 與逐步手算的結果一致 | 交付物 |
| 架構測試 | `LayeringTests`（5） | 專案間的依賴規則 | 限制 |

自動化測試以外，還有：

- **效能量測**（`tests/LocalSeek.Perf`）：在 5 萬份文件上量測，不在 CI 中執行（第 12 章）。
- **端對端冒煙測試**：發佈後的 App 用 Windows UI Automation 操作，讀取畫面文字確認結果（見 [test-report.md](../test-report.md) 第 3 節）。

**教學重點**：測試種類要對應**風險**。中斷安全的風險無法用單元測試覆蓋，所以有故障注入；搜尋正確性的輸入空間太大，所以有性質測試加參考解；並行問題很難重現，所以有固定種子的壓力測試。

---

## 11.2 TDD：紅、綠、重構

專案依照 TDD 開發：

1. **紅**：先寫測試，執行，確認它**失敗**，而且是因為預期的原因失敗。
2. **綠**：寫最少的程式讓它通過。
3. **重構**：測試保護下整理程式碼。

### 範例：修正 GBK 誤判

```csharp
// tests/LocalSeek.Tests/Extraction/ExtractionTests.cs（節錄）
[Fact]
public void Simplified_chinese_gbk_is_not_mistaken_for_big5()
{
    var bytes = Gbk.GetBytes("这是一份简体中文的报告，内容包括数据分析与模型研究。");
    Assert.True(DecodesAsStrictBig5(bytes), "Precondition: the GBK bytes are also valid Big5 (e.g. 涴岆珨爺潠极笢恅腔惆豢).");

    var result = Extract("简体.txt", bytes);

    Assert.Equal(SkipReason.UnknownEncoding, Skipped(result));
    Assert.Contains("常用字", ((ExtractionResult.Skipped)result).Message, StringComparison.Ordinal);
}
```

- 修正前執行，失敗訊息是「預期 Skipped，實際 Success」，證明它重現了 bug。
- 測試的第一個斷言是**前置條件**：確認這段 GBK 位元組真的能被 Big5 嚴格解碼。沒有這一行，如果某天這段文字剛好被結構檢查擋下，測試仍然會通過，卻沒有測到新的規則。

同時加的另外兩個測試，在修正前就已經通過：

- `Big5_with_a_few_rare_ideographs_is_still_accepted`：繁體中夾雜少數罕用字，仍要接受。
- `Too_few_ideographs_to_judge_fall_back_to_big5`：漢字太少時不判斷。

它們是**防止修過頭**的測試。新規則如果太嚴格，會把正常的繁體檔擋下，這兩個測試就會失敗。

### 純重構時沒有「紅」

把片語查詢改成平行執行（第 12 章），結果不應該有任何改變，所以寫不出「先失敗」的測試。這時的做法是先寫**特性測試**（characterization test），把目前的正確行為釘住，確認它在修改前就通過，再開始修改：

```csharp
[Theory]
[InlineData("w1 w2 w1")]
[InlineData("w1 w1")]
public void Phrase_counts_are_exact_across_many_documents(string phrase)
{
    // Enough documents that phrase matching is split into several document ranges.
    ...   // 20,003 份隨機文件；期望值直接從原文逐份計算，不經過索引
}
```

文件數刻意選 20,003：平行化會以 4,096 份為一段，這樣會有多段，最後一段也不是滿的。既有的測試都用小語料，永遠只有一段，測不到平行的路徑。

---

## 11.3 參考解

驗收標準要求：「隨機語料加 2000 條隨機查詢，命中集合與參考解完全一致」、「分數相對誤差 ≤ 1e-9」。要比對，就需要一個**一定正確**的答案。

### 參考解是什麼

`tests/LocalSeek.Tests/Reference/` 中的 `ReferenceEngine`：

- **不建索引**，每次查詢都把記憶體中的所有文件從頭掃一遍。
- 用**自己的**分詞器 `ReferenceTokenizer`（整串先 NFKC、轉成 code point 陣列再掃描、不追蹤座標）。
- 用自己的 Binder、布林求值、片語計數、篩選與 BM25 計算。
- 與正式程式碼**只共用資料型別**：語法樹、綁定後的查詢型別、`QuerySyntaxException`。

它的效能很差，但程式碼直接對應規格，簡單到可以逐行檢查。

### 為什麼要「獨立」實作？

如果參考解呼叫正式的分詞器，兩邊的分詞結果就一定相同，分詞的 bug 就永遠不會被發現。參考解的價值來自**用不同的方法得到相同的答案**。兩個寫法不同的實作同時寫錯、而且錯得一模一樣的機率很低。

### 比對的方式

```csharp
// tests/LocalSeek.Tests/Properties/Oracle.cs（節錄）
/// Binds with both binders; returns false when both reject the query, fails when they disagree.
public static bool TryBindBoth(string query, out BoundNode engine, out BoundNode reference) { ... }

/// Same total, same ranking (ties by path) and scores within 1e-9 relative error.
public static void AssertSameResults(IIndexReader index, ReferenceEngine reference, string query, ...)
{
    ...
    Assert.Equal(reference.Count, actual.DocumentCount);
    Assert.Equal(expected.Total, actual.TotalHits);
    Assert.Equal(expected.Hits.Select(h => h.Path), actual.Hits.Select(h => h.Path));
    // 每一筆的分數相對誤差 ≤ 1e-9
}
```

連「這個查詢是否被拒絕」都要一致：正式引擎拒絕、參考解接受（或反過來），測試就失敗。

### 範例語料：人工的參考解

`samples/expected/README.md` 對 12 份文件、16 個查詢做了**人工推導**：詞元、tf、df、len 由人依規則推導，只有最後的 ln 與除法用計算機。`SampleCorpusTests` 驗證引擎與人工結果一致。手算結果與引擎第一次執行就完全一致，這也間接驗證了「參考解是對的」：三個獨立來源（人工、參考解、正式引擎）得到同一個答案。

---

## 11.4 性質測試

### 概念

單元測試檢查「某個輸入 → 某個輸出」。性質測試檢查「**所有**輸入都滿足某個性質」，做法是產生大量隨機輸入。

本專案的 12 組性質：

| 性質 | 驗收 |
|---|---|
| 分詞器與參考分詞器的詞元、位置、單字詞頻一致 | 1 |
| 原始範圍包含詞元且最小；不變長的文字完全相等；不切斷代理對 | 2 |
| 320 份文件、2000 條以上隨機查詢的命中集合一致 | 5 |
| 片語出現次數一致（含跨文字系統） | 6 |
| 單一漢字與前綴的 tf、df 一致 | 7 |
| 分數相對誤差 ≤ 1e-9，排序完全一致 | 9 |
| 隨機增、改、刪、改名、合併後，分數等於只含現存文件的參考解 | 10 |
| 增量更新（含資料夾改名與溢位）收斂到從零重建 | 11 |
| 合併前後結果不變、片段數 O(log N)、墓碑清除 | 16 |
| 摘要的數量、長度、順序，高亮的精確度與合併 | 18 |

### 可重現：固定種子

隨機測試最大的問題是「失敗了卻無法重現」。本專案寫了一個很小的執行器：

```csharp
// tests/LocalSeek.Tests/Properties/Property.cs（節錄）
public static void ForAll(int seed, int iterations, Action<Random> body, [CallerMemberName] string name = "")
{
    if (ParseReplaySeed(Environment.GetEnvironmentVariable(SeedVariable)) is { } replaySeed)
    {
        Run(name, iteration: 0, replaySeed, body);       // 重播單一案例
        return;
    }

    var seeds = new Random(seed);                        // 固定種子 → 每次產生同樣的案例序列
    for (var i = 0; i < iterations; i++)
    {
        Run(name, i, seeds.Next(), body);
    }
}

private static void Run(string name, int iteration, int caseSeed, Action<Random> body)
{
    try
    {
        body(new Random(caseSeed));
    }
    catch (Exception ex)
    {
        throw new PropertyFailedException(
            $"Property '{name}' failed at iteration {iteration}. Reproduce with {SeedVariable}={caseSeed}. {ex.Message}", ex);
    }
}
```

- 每次執行產生完全相同的案例，CI 上的失敗在本機一定能重現。
- 失敗訊息印出該案例的種子，設定環境變數 `LOCALSEEK_SEED` 就能只跑那一個案例來除錯。

### 避免「空洞地通過」

隨機產生器可能產生太簡單的輸入。例如隨機查詢大多沒有命中，「命中集合一致」就只是在比較兩個空集合。所以性質測試在迴圈結束後會檢查**涵蓋量**：

```csharp
Property.AssertCoverage(checkedQueries >= 2000, $"Only {checkedQueries} valid queries were checked.");
Property.AssertCoverage(nonEmpty >= checkedQueries / 2, $"Only {nonEmpty}/{checkedQueries} queries had hits; generator too sparse.");
```

### 一個測試工具的 bug

完整測試時，用 20 個不同的種子重播，發現 3 個性質測試**每次都失敗**。原因是涵蓋量檢查原本用 `Assert.True`：重播模式只跑 1 個案例，「至少 2000 條查詢」當然不成立。

真正的比對在迴圈內都通過了，所以這不是產品的 bug，但它讓「重播一個通過的案例」看起來像失敗，會誤導除錯的人。修正是把涵蓋量檢查改成 `Property.AssertCoverage`，重播時略過：

```csharp
public static void AssertCoverage(bool condition, string message) =>
    AssertCoverage(condition, message, replay: ParseReplaySeed(Environment.GetEnvironmentVariable(SeedVariable)) is not null);

internal static void AssertCoverage(bool condition, string message, bool replay)
{
    if (!replay)
    {
        Assert.True(condition, message);
    }
}
```

注意這裡多了一個 `internal` 的多載，把「是不是重播」當參數傳入。這是為了讓 `PropertyRunnerTests` 不必修改環境變數就能測試兩種模式：測試是平行執行的，修改行程層級的環境變數會影響其他同時執行的性質測試。

**教學重點**：測試工具也是程式，也需要測試。

---

## 11.5 壓力測試的不變式

驗收 #17：索引與合併進行中同時搜尋，不丟例外、不看到不一致的結果。

並行的錯誤很難用「預期輸出」描述，因為每次的交錯都不同。做法是找出**任何一致的快照都必須滿足的不變式**：

```csharp
// tests/LocalSeek.Tests/Index/ConcurrencyStressTests.cs（節錄：搜尋端的檢查）
Assert.Equal(StableCount, stableHits.TotalHits);                                   // 從不改動的 50 份文件，永遠都在
Assert.Equal(StableCount, stableHits.Hits.Select(h => h.Path).Distinct().Count()); // 沒有重複
Assert.Equal(ids.Count, ids.Distinct().Count());
Assert.Equal(snapshot.DocCount, StableCount + churnHits.TotalHits);                // 文件總數 = 穩定的 + 變動的
Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
```

- 寫入端用**固定種子**產生新增、修改、刪除、改名的序列；另一個工作不斷觸發合併。
- 3 個搜尋工作同時反覆取得快照並檢查不變式。
- 「同一份文件出現兩次」或「穩定的文件消失」都代表看到了不一致的狀態。

### 避免空洞地通過

在忙碌的機器上，可能寫入端很快跑完，搜尋端還沒開始，兩者根本沒有重疊。測試會「通過」，卻什麼也沒測到。所以寫入端會持續運作，**直到合併與搜尋真的重疊過**才停止：

```csharp
// Keep churning (same seeded sequence) until merges and searches have really overlapped,
// so the test does not pass vacuously on a busy machine.
```

開發時這個測試曾偶發失敗一次，之後無法重現。當時沒有抓到錯誤訊息，所以做了兩件事：加上上面的重疊保證，並讓失敗時輸出詳細的狀態。之後在每次完整測試與專門的重複執行中都通過。測試報告中如實記錄了「原因未能確認」。

---

## 11.6 不好的測試：本專案遇到的例子

| 問題 | 例子 | 怎麼發現 | 修正 |
|---|---|---|---|
| 量到錯的東西 | 取消延遲測試的「夠慢」檢查包含了建立索引的時間 | CI 失敗 | 先暖機，基準只量查詢 |
| 依賴時間推算 | 用一次量測推算「查詢一定還在跑」 | CI 偶發失敗 | 改成等待「已開始讀索引」的訊號（第 10 章） |
| 測試的清理程式有 bug | 刪除接合點測試資料夾時無限遞迴 | 測試卡住 | 先移除接合點再刪除 |
| 測試工具的邏輯錯誤 | 重播模式下涵蓋量檢查必定失敗 | 用多個種子重跑 | `AssertCoverage` |
| 前置條件沒有斷言 | （預防）GBK 測試若沒有先確認位元組能被 Big5 解碼，可能測不到新規則 | 設計時想到 | 加上前置條件斷言 |

**教學重點**：

- 一個測試通過，只代表「這段程式碼沒有讓它失敗」。測試本身是否真的檢查了你以為它檢查的東西，要另外確認：先看它失敗，或檢查涵蓋量。
- 偶發失敗的測試會讓團隊習慣忽略紅燈，比沒有測試更危險，要優先處理。

---

## 練習

1. 從 11.1 節的表中挑一個驗收標準，說明為什麼它適合用那種測試，而不適合用單元測試。
2. 參考解為什麼可以與正式程式碼共用「語法樹」型別，卻不能共用「分詞器」？如果共用 Parser，會失去什麼驗證能力？
3. 寫一個新的性質：「對任何查詢 q，`q` 的命中數 ≥ `q AND x` 的命中數」。用 `Property.ForAll` 實作，並加上涵蓋量檢查。
4. 設定 `LOCALSEEK_SEED=12345` 執行 `Properties` 底下的測試。輸出中每個性質跑了幾個案例？
5. 為壓力測試再想一個不變式，並說明違反它代表什麼樣的錯誤。
