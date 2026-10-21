# 第 7 章　查詢執行與排序

## 學習目標

- 用 term-at-a-time 與 bitset 執行布林查詢。
- 實作片語比對：先用 postings 縮小範圍，再比對位置。
- 理解 BM25 的三個組成（idf、tf 飽和、長度正規化），並能手算分數。
- 處理浮點數加總順序、同分排序與 Top-K。
- 產生摘要與精確的高亮範圍。

相關程式：`src/LocalSeek.Core/Search/`（`QueryExecutor.cs`、`Bm25.cs`、`SnippetGenerator.cs`）

---

## 7.1 執行策略：term-at-a-time

查詢執行有兩種經典策略：

| 策略 | 做法 | 適合 |
|---|---|---|
| term-at-a-time | 一次處理一個詞，把它的所有 postings 讀完，結果累加到每份文件的陣列 | 文件數不大，實作簡單 |
| doc-at-a-time | 同時開啟所有詞的 postings，依文件編號往前推進，一次處理完一份文件 | 文件數極大，可以提早結束 |

本專案的規模上限約 10 萬份文件，選擇 **term-at-a-time 加稠密陣列**：

- 每個詞產生一個 `int[DocCount]`，存每份文件的 tf。
- 布林結構轉成 **bitset**：`ulong[(DocCount + 63) / 64]`，每個 bit 代表一份文件。

10 萬份文件的 bitset 只有 12.5 KB，`AND` 就是逐個 `ulong` 做 `&=`，一次處理 64 份文件。

```csharp
// src/LocalSeek.Core/Search/QueryExecutor.cs（節錄）
case AndQuery a:
{
    var result = Match(a.Children[0]);
    foreach (var child in a.Children.Skip(1))
    {
        var other = Match(child);
        for (var w = 0; w < _words; w++)
        {
            result[w] &= other[w];
        }
    }

    return result;
}

case NotQuery not:
{
    var result = Match(not.Operand);
    for (var w = 0; w < _words; w++)
    {
        result[w] = ~result[w];
    }

    ClearTail(result);      // 最後一個 ulong 中超出 DocCount 的 bit 要清掉
    return result;
}
```

`ClearTail` 是一個容易忘記的細節：假設有 100 份文件，bitset 有 2 個 `ulong`（128 bits），取反後第 100 到 127 個 bit 會變成 1，代表不存在的文件。忘了清掉，`NOT` 查詢就會回傳不存在的文件編號。

**教學重點**：選擇演算法要看規模。term-at-a-time 在百萬級文件時，每個詞都要配置一個百萬大小的陣列，就不再適合；但在 10 萬以內，它簡單、容易驗證，而且夠快。設計文件中寫明了這個上限與換方案的時機。

---

## 7.2 每個葉節點的詞頻

不同種類的詞項，詞頻的算法不同。共同規則是「內文詞頻 + 2 × 檔名詞頻」：

```csharp
// src/LocalSeek.Core/Search/QueryExecutor.cs（節錄）
switch (leaf)
{
    case TermQuery t:
        Accumulate(tf, TermSpace.BodyTerm, t.Term, 1);
        Accumulate(tf, TermSpace.NameTerm, t.Term, 2);
        break;
    case CharQuery c:                              // 單一漢字：用單字詞頻
        Accumulate(tf, TermSpace.BodyChar, c.Char, 1);
        Accumulate(tf, TermSpace.NameChar, c.Char, 2);
        break;
    case PrefixQuery p:                            // 前綴：展開成所有符合的詞，詞頻相加
        foreach (var term in _index.GetTermsWithPrefix(TermSpace.BodyTerm, p.Prefix))
        {
            Accumulate(tf, TermSpace.BodyTerm, term, 1);
        }
        // ... NameTerm 同理，權重 2 ...
        break;
    case PhraseQuery phrase:                       // 片語：詞頻 = 起始位置的個數
        AccumulatePhrase(tf, TermSpace.BodyTerm, phrase.Terms, 1);
        AccumulatePhrase(tf, TermSpace.NameTerm, phrase.Terms, 2);
        break;
}
```

同一個葉節點在一次查詢中可能被用兩次：一次判斷是否命中（`Match`），一次計分。所以結果以葉節點的字串表示為鍵快取起來（`_frequencies`）。

---

## 7.3 片語比對

片語 `"資料 分析"` 要找「資料出現在 p、分析出現在 p+1」的位置。最直接的做法是讀出兩個詞在所有文件中的所有位置，再逐份比對。但位置資料量很大，大部分文件根本不可能命中。

### 第一步：用 postings 求候選文件

只讀 postings（文件編號與 tf，不含位置），把每個詞出現過的文件做成 bitset，再取交集。只有**所有詞都出現**的文件才可能含有片語。

### 第二步：只解碼候選文件的位置

讀位置時傳入一個 `wanted` 函式，不是候選的文件，位置資料直接跳過：

```csharp
// src/LocalSeek.Core/Index/Segments/PostingsCodec.cs（節錄）
if (wanted is not null && !wanted(id))
{
    positionReader.SkipVarInts(frequency);   // 只看最高位元找出 varint 邊界，不做加總
    continue;
}
```

`SkipVarInts` 只需要找 varint 的結束位置（最高位元為 0 的 byte），比完整解碼便宜。

### 第三步：單向合併計算起始位置

每份候選文件中，詞的位置都已排序。對第一個詞的每個位置 `s`，檢查其他詞是否在 `s + i`。因為 `s` 遞增，每個詞的游標也只需要往前走，不必回頭：

```csharp
// src/LocalSeek.Core/Search/QueryExecutor.cs（節錄）
private static int CountPhraseStarts(PositionLists[] perTerm, int doc, int[] cursors)
{
    Array.Clear(cursors);
    var first = perTerm[0].Of(doc);
    var count = 0;
    foreach (var start in first)
    {
        var all = true;
        for (var i = 1; i < perTerm.Length && all; i++)
        {
            var positions = perTerm[i].Of(doc);
            var target = start + i;
            ref var cursor = ref cursors[i];
            while (cursor < positions.Length && positions[cursor] < target)
            {
                cursor++;
            }

            all = cursor < positions.Length && positions[cursor] == target;
        }

        if (all)
        {
            count++;
        }
    }

    return count;
}
```

這是**合併**（merge）的經典技巧，時間是所有位置清單長度的總和，而不是乘積。

注意片語可以重疊：在「w1 w1 w1」中查 `"w1 w1"`，起始位置 0 與 1 都符合，tf 是 2。測試 `QueryExecutorTests.Phrase_counts_are_exact_across_many_documents` 就包含這個情況。

這三步讓片語查詢的 P95 從 118 ms 降到 43 ms。後來又把第二、三步平行化，在最壞情境下再快約一倍，詳見第 12 章。

---

## 7.4 BM25

### 公式

本專案使用規格定義的 BM25 變形：

```
idf(t) = ln(1 + (N − df + 0.5) / (df + 0.5))

score(d, q) = Σ  idf(t) × tf × (k1 + 1) / (tf + k1 × (1 − b + b × len(d) / avgdl))
             t∈q

k1 = 1.2，b = 0.75
```

| 符號 | 意義 |
|---|---|
| N | 現存文件數 |
| df | 含有詞 t 的現存文件數 |
| tf | 詞 t 在文件 d 的詞頻（內文 + 2 × 檔名） |
| len(d) | 文件長度（內文詞元數 + 2 × 檔名詞元數） |
| avgdl | 所有現存文件的平均長度 |

```csharp
// src/LocalSeek.Core/Search/Bm25.cs
public static double Idf(int documentCount, int documentFrequency) =>
    Math.Log(1 + ((documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));

public static double TermScore(double idf, int termFrequency, int length, double averageLength) =>
    idf * termFrequency * (K1 + 1) / (termFrequency + (K1 * (1 - B + (B * length / averageLength))));
```

### 直覺

BM25 由三個想法組成：

1. **稀有的詞比較重要（idf）**：「的」幾乎每份文件都有，命中它沒什麼資訊量；「牛丼」很少見，命中它很有意義。df 越小，idf 越大。
2. **詞頻有用，但會飽和（k1）**：出現 10 次比出現 1 次相關，但不是 10 倍相關。`tf × (k1 + 1) / (tf + k1 × …)` 隨 tf 增加而趨近上限 `k1 + 1`。
3. **長文件要打折（b）**：長文件自然會出現更多詞。`len / avgdl` 大於 1 時分母變大、分數變小。b = 0.75 代表長度正規化的強度。

### 手算一個例子

`samples/expected/README.md` 有 16 個查詢的逐步手算。以查詢 `模型` 為例：

- 範例語料中有 12 份文件會被索引，N = 12，Σlen = 151，avgdl = 151 / 12 = 12.583333333。
- 含「模型」的文件有 5 份，df = 5，idf = ln(1 + (12 − 5 + 0.5) / (5 + 0.5)) = 0.860201265。

| 文件 | tf | len | 分母 tf + 1.2 × (0.25 + 0.75 × len / avgdl) | 分數 |
|---|---|---|---|---|
| 12-模型手冊.txt | 2（檔名命中 1 次 × 2） | 13 | 2 + 1.229801325 | **1.171863278** |
| 10-資料.csv | 1 | 10 | 1 + 1.015231788 | 0.939069538 |
| 07-fullwidth.txt | 1 | 11 | 1 + 1.086754967 | 0.906883086 |
| 05-page.html | 1 | 13 | 1 + 1.229801325 | 0.848704664 |
| 01-utf8-nobom.txt | 1 | 16 | 1 + 1.444370861 | 0.774204444 |

觀察：

- 第一名是只有**檔名**含「模型」的文件，因為檔名命中算 2 次。
- 後四名的 tf 都是 1，排序完全由**長度**決定：越短的文件分數越高。

測試 `Samples/SampleCorpusTests` 驗證引擎算出的五個分數與這張手算表的相對誤差 ≤ 1e-9。端對端測試中，也在實際 App 的畫面上確認了排序，以及 05-page.html 的 0.849、01-utf8-nobom.txt 的 0.774（見 [test-report.md](../test-report.md) 第 3 節）。

---

## 7.5 統計值必須精確

規格要求「N 與 df 只算現存文件」，而刪除只是加墓碑。所以：

- **N 與 Σlen** 在建立快照時，由所有未刪除的文件精確加總（`long`）。
- **df** 在查詢時由該詞的 tf 陣列計算：數一數有幾個 tf > 0。

```csharp
private SearchResult Run(BoundNode query, int topK)
{
    var n = _index.DocCount;                    // 快照中的現存文件數
    var matched = Match(query);
    var averageLength = n == 0 ? 0 : (double)_index.TotalLength / n;

    var leaves = ScoringLeaves(query);
    for (var i = 0; i < leaves.Count; i++)
    {
        tfs[i] = Frequencies(leaves[i]);
        var df = CountPositive(tfs[i]);         // 只數現存文件
        idfs[i] = Bm25.Idf(n, df);
        ...
    }
    ...
}
```

沒有任何統計值是預先存好的，所以不可能用到過時的數字。代價是每次查詢要多掃描一次陣列，在 10 萬份文件內只需要幾十微秒。

規格還要求長度、詞頻、df、N 以**整數**保存。整數加總沒有誤差；如果用 `double` 累加 avgdl，加入與刪除文件的順序不同，結果就可能有微小差異，與參考解比對時就無法要求「相對誤差 ≤ 1e-9」。

---

## 7.6 加總順序、同分與 Top-K

### 浮點數加總的順序

浮點數加法沒有結合律：`(a + b) + c` 不一定等於 `a + (b + c)`。查詢 `模型 OR 資料` 與 `資料 OR 模型` 的分數，如果依查詢中出現的順序加總，可能在最後一位不同，排序就可能不同。

解法是用**固定順序**加總：`ScoringLeaves` 取出所有正向葉節點，去掉重複，再依字串表示排序：

```csharp
public static IReadOnlyList<BoundNode> ScoringLeaves(BoundNode query)
{
    var leaves = new SortedDictionary<string, BoundNode>(StringComparer.Ordinal);
    // 走訪整棵樹，跳過 NOT 底下（奇數層）的葉節點與篩選條件
    ...
    return leaves.Values.ToList();
}
```

這也同時滿足規格的「重複的相同詞項只計一次」與「NOT 與篩選不計分」。

### 同分與 Top-K

規格要求同分時依完整路徑（忽略大小寫、ordinal）由小到大排序。結果只取前 K 筆（預設 200），用一個大小為 K 的 heap，堆頂是「目前最差的一筆」：

```csharp
public static int Compare(SearchHit a, SearchHit b)
{
    var byScore = b.Score.CompareTo(a.Score);
    return byScore != 0 ? byScore : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
}
```

新的命中比堆頂好，就取代堆頂。總時間 O(M log K)，M 是命中數。

---

## 7.7 摘要與高亮

索引中只存位置，不存字元 offset（可以省下約一半的索引空間）。所以預覽時：

1. 重新讀檔、抽取文字。
2. 重新分詞，得到每個詞元的原始座標（第 4 章）。
3. 找出所有正向詞項的命中範圍；片語則找連續位置的詞元，範圍從第一個詞元的開頭到最後一個的結尾。
4. 把重疊或相鄰的範圍**合併**成高亮區段。
5. 選出最多 3 段、每段最多 120 字元的視窗：

```csharp
// src/LocalSeek.Core/Search/SnippetGenerator.cs（節錄）
while (windows.Count < maxSnippets)
{
    var best = highlights
        .Select(h => Window(text, h.Start - LeadingContext, maxLength))   // 每個高亮前留 20 字元
        .Where(w => !windows.Any(c => w.Start < c.End && c.Start < w.End)) // 不與已選的重疊
        .Select(w => (Window: w, Score: Score(hits, shown, w)))
        .Where(x => x.Score.DistinctTerms > 0)
        .OrderByDescending(x => x.Score.DistinctTerms)   // 優先：還沒顯示過的不同詞最多
        .ThenByDescending(x => x.Score.Hits)
        .ThenBy(x => x.Window.Start)
        .FirstOrDefault();
    ...
}
```

這是一個**貪婪演算法**：每次選「能顯示最多還沒出現過的詞」的視窗。它不保證全域最佳，但簡單、可預測，而且對使用者來說效果很好：三段摘要通常會涵蓋查詢中不同的詞，而不是三段都在講同一個詞。最後依文件中的順序輸出。

視窗的邊界不能切斷代理對，否則 `𠮷` 會變成兩個無法顯示的半字元。這由 `SnippetProperties` 性質測試驗證（驗收 #18）。

---

## 練習

1. 有 100 份文件時，bitset 需要幾個 `ulong`？`NOT` 之後若不呼叫 `ClearTail`，第 2 個 `ulong` 會有哪些多餘的 bit？
2. 用 7.4 節的資料，手算查詢 `資料` 在 `11-notes.md`（tf = 3，len = 10）的分數，再對照 `samples/expected/README.md`。
3. 如果 k1 改成 0，BM25 會變成什麼？如果 b 改成 0 呢？分別說明對排序的影響。
4. 在「w1 w2 w1 w2 w1」中查 `"w1 w2 w1"`，tf 是多少？用 `CountPhraseStarts` 的演算法逐步追蹤游標。
5. 7.6 節說浮點數加總要固定順序。寫一個小程式，找出三個 `double` 使 `(a + b) + c != a + (b + c)`。
6. 摘要的貪婪選擇什麼時候會比最佳解差？舉一個例子。
