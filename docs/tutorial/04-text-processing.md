# 第 4 章　正規化與分詞

## 學習目標

- 理解中文全文檢索為什麼需要特別的分詞方法，以及 n-gram 與字典式分詞的取捨。
- 理解 Unicode 正規化（NFKC）的作用與它帶來的「長度改變」問題。
- 實作一個能把每個詞元對應回原始文字座標的分詞器。
- 處理代理對（surrogate pair）、不合法的 UTF-16 等邊界情況。

相關程式：`src/LocalSeek.Core/Text/`（`TextNormalizer.cs`、`NormalizedText.cs`、`Tokenizer.cs`、`Token.cs`）

---

## 4.1 問題：中文沒有空白

英文的詞之間有空白，「找出所有詞」幾乎就是「用空白切開」。中文沒有空白，「台灣大學的研究」要怎麼切？

常見的兩種做法：

| 做法 | 例子：「台灣大學」 | 優點 | 缺點 |
|---|---|---|---|
| 字典式分詞 | 台灣 / 大學 | 詞元有意義，索引較小 | 需要詞典；新詞、人名、專有名詞容易切錯，切錯就搜不到 |
| 重疊雙字（bigram） | 台灣 / 灣大 / 大學 | 不需要詞典，不會漏 | 產生無意義的詞元（灣大），索引較大 |

本專案依規格採用**重疊雙字**。它的關鍵性質是**召回率高**：只要查詢的字串真的出現在文件中，就一定找得到，因為查詢「大學」產生的詞元「大學」一定也出現在文件的詞元裡。代價是精確率：查「灣大」也會命中「台灣大學」。

查詢較長的字串時（例如「台灣大學」），會被拆成連續的雙字詞元，並要求它們**位置連續**（片語），所以結果仍然精確。

---

## 4.2 規則

### 正規化

依規格，每段文字先做三步：

1. **NFKC**：把「相容字元」換成標準形式。全形 `Ａ` → `A`、全形空白 → 一般空白、`ﬁ`（連字）→ `fi`、`㈱` → `(株)`。
2. **轉小寫**（invariant culture）：`AI` → `ai`。
3. **臺 → 台**：只有這一個對應。

### 分詞

- Latin 詞元：正規化後連續的 `[a-z0-9]`。
- CJK 字元：U+3400–U+4DBF、U+4E00–U+9FFF、U+20000–U+2EBEF（後者需要代理對表示）。
- 其他所有字元都是**分隔符**。
- 連續的 CJK 區段長度為 L：L ≥ 2 時產生 L − 1 個重疊雙字詞元；L = 1 時產生一個**單字詞元**。
- 每個詞元佔一個位置，依序遞增；標點不佔位置。
- 另外，每個 CJK 字元出現一次，就為它的「單字詞頻」加 1（用於查詢單一漢字）。

### 例子

範例語料 `samples/corpus/01-utf8-nobom.txt` 的內容是「台灣大學的AI模型研究」：

| 位置 | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| 詞元 | 台灣 | 灣大 | 大學 | 學的 | ai | 模型 | 型研 | 研究 |
| 種類 | 雙字 | 雙字 | 雙字 | 雙字 | Latin | 雙字 | 雙字 | 雙字 |

`samples/corpus/07-fullwidth.txt` 是「ＡＩ模型與ＤＡＴＡ　ＡＮＡＬＹＳＩＳ」，正規化後變成「ai模型與data analysis」，詞元是 `ai`、`模型`、`型與`、`data`、`analysis`。

---

## 4.3 核心難點：座標還原

搜尋結果要**高亮命中的位置**。高亮必須畫在原始文字上，但詞元是從**正規化後**的文字產生的，兩者的長度可能不同。

以 `samples/corpus/09-compat.txt`「㈱公司的ﬁle資料」為例：

```
原始：  ㈱   公  司  的  ﬁ    l  e  資  料
索引：  0    1   2   3   4    5  6  7   8

正規化：( 株 ) 公 司 的 f i l e 資 料
```

一個字元 `㈱` 變成三個字元，一個 `ﬁ` 變成兩個。如果直接用正規化後的索引去高亮原始文字，位置會全部錯開。

### 解法：正規化單元與對照表

1. 把原始字串切成**正規化單元**：一個 code point 加上它後面的組合記號（例如 `e` 加上「́」）。代理對算一個 code point。
2. 每個單元**各自**正規化。
3. 單元產生的每一個輸出字元，都記錄「我來自原始文字的 [start, end)」。

```csharp
// src/LocalSeek.Core/Text/TextNormalizer.cs（節錄）
public static void Normalize(ReadOnlySpan<char> source, NormalizedText into)
{
    into.Clear();
    var i = 0;
    while (i < source.Length)
    {
        var start = i;
        i += CodePointLength(source, i);                 // 一個 code point（代理對算 2 個 char）
        while (i < source.Length && IsCombiningMark(source, i))
        {
            i += CodePointLength(source, i);             // 加上後面的組合記號
        }

        var unit = source[start..i];
        into.Append(unit.Length == 1 ? NormalizeBmp(unit[0]) : NormalizeUnit(unit), start, i);
    }
}
```

`NormalizedText` 是一個可重複使用的緩衝區，對每個輸出字元保存兩個整數：

```csharp
// src/LocalSeek.Core/Text/NormalizedText.cs（節錄）
internal void Append(ReadOnlySpan<char> text, int sourceStart, int sourceEnd)
{
    // ... 必要時擴充陣列 ...
    text.CopyTo(_chars.AsSpan(Length));
    _sourceStart.AsSpan(Length, text.Length).Fill(sourceStart);   // 這個單元的所有輸出字元
    _sourceEnd.AsSpan(Length, text.Length).Fill(sourceEnd);       // 都指回同一個原始範圍
    Length += text.Length;
}
```

對上面的例子，對照表是：

| 正規化字元 | ( | 株 | ) | 公 | 司 | 的 | f | i | l | e | 資 | 料 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 原始範圍 | [0,1) | [0,1) | [0,1) | [1,2) | [2,3) | [3,4) | [4,5) | [4,5) | [5,6) | [6,7) | [7,8) | [8,9) |

詞元的原始範圍 = **第一個字元的 start** 到**最後一個字元的 end**：

| 詞元 | 位置 | 原始範圍 | 原始子字串 |
|---|---|---|---|
| 株（單字） | 0 | [0,1) | ㈱ |
| 公司 | 1 | [1,3) | 公司 |
| 司的 | 2 | [2,4) | 司的 |
| file | 3 | [4,7) | ﬁle |
| 資料 | 4 | [7,9) | 資料 |

這正是第 1 章「決策 A」的情況：`株` 的原始子字串是 `㈱`，正規化後是 `(株)`，**包含**但不**等於** `株`。`file` 的原始子字串 `ﬁle` 正規化後剛好等於 `file`。

### 為什麼要「逐單元」正規化？

如果先把整串正規化再想辦法對回去，就必須反推「哪幾個輸出字元來自哪個輸入字元」，而 NFKC 沒有提供這個資訊。逐單元處理則讓對照關係**由構造保證**。

代價是少數跨單元的組合（例如諺文字母 Jamo 組字）會被分開處理，座標偏寬；但這些字元在分詞規則中都是分隔符，不影響任何詞元。這被列為已知限制。

---

## 4.4 分詞器的實作

分詞器在正規化後的字元上掃描一次：

```csharp
// src/LocalSeek.Core/Text/Tokenizer.cs（節錄：CJK 區段的處理）
var runStart = i;
var count = 0;
var previous = -1;
while (i < chars.Length)
{
    length = TextNormalizer.CodePointLength(chars, i);
    if (!IsCjkAt(chars, i, length))
    {
        break;
    }

    sink.OnCjkChar(chars.Slice(i, length), n.SourceStart(i), n.SourceEnd(i));   // 單字詞頻
    if (previous >= 0)
    {
        // 前一個字與這個字組成雙字詞元，位置是「區段起點 + 第幾個雙字」
        sink.OnToken(chars[previous..(i + length)], TokenKind.CjkBigram, position + count - 1,
                     n.SourceStart(previous), n.SourceEnd(i));
    }

    previous = i;
    count++;
    i += length;
}

if (count == 1)
{
    sink.OnToken(chars[runStart..i], TokenKind.CjkSingle, position, n.SourceStart(runStart), n.SourceEnd(runStart));
    position++;
}
else
{
    position += count - 1;   // L 個字產生 L − 1 個雙字詞元
}
```

### 效能設計

建立 5 萬份文件的索引時，分詞器要處理約 1 GB 的文字，所以它的設計很在意配置（allocation）：

- **`ITokenSink` 介面**：詞元透過回呼傳出，參數是 `ReadOnlySpan<char>`，不必為每個詞元配置一個 `string`。泛型參數加上 `allows ref struct`，呼叫端可以傳入 `ref struct` 型別的 sink，連介面呼叫的成本都由 JIT 消除。
- **重複使用緩衝區**：`NormalizedText` 只在需要時擴充，同一個 worker 處理下一份文件時直接重用。
- **BMP 快取**：絕大多數字元是單一 char 的單元。`NormalizeBmp` 用一個 65,536 格的陣列快取每個字元的正規化結果，同一個字元只呼叫一次 `string.Normalize`。

```csharp
// src/LocalSeek.Core/Text/TextNormalizer.cs（節錄）
// Lazily filled cache for single-char BMP units, which are the overwhelming majority of input.
// Races are benign: two threads compute the same immutable string.
private static readonly string?[] BmpCache = new string?[char.MaxValue + 1];
```

注意註解中的「Races are benign」：多個執行緒可能同時填同一格，但它們算出的是同一個不可變字串，所以不需要加鎖。這類「良性競爭」要在註解中說清楚，否則維護者會以為是 bug。

---

## 4.5 邊界情況

| 輸入 | 處理 | 為什麼 |
|---|---|---|
| `𠮷`（U+20BB7，代理對） | 視為一個 CJK 字元 | 在 U+20000–U+2EBEF 範圍內；`CodePointLength` 把代理對當一個單位 |
| 單獨的代理字元（不合法 UTF-16） | 原樣保留，當作分隔符 | `string.Normalize` 會丟例外 |
| `U+FFFE` 等非字元（noncharacter） | 原樣保留，當作分隔符 | 開發時的性質測試發現 `Normalize` 對它丟 `ArgumentException` |

第三列容易被忽略：輸入含有 `U+FFFE` 這類非字元時，`string.Normalize` 會丟出例外。如果沒有處理，一個含有奇怪字元的檔案就會讓整個分詞失敗、中止索引。本專案在第一版分詞器（M1）就預先處理了這種情況：捕捉例外並原樣保留，並在註解中寫明理由：

```csharp
catch (ArgumentException)
{
    // Noncharacters such as U+FFFE are rejected by the normalizer. They are separators either
    // way, so the unit passes through unchanged; arbitrary file content must never abort indexing.
    return text;
}
```

這裡捕捉例外**不是**「無聲吞掉」：它是一個有明確理由、有測試保護、結果仍然正確的處理。

**教學重點**：處理「使用者提供的任意資料」的程式碼，要特別為**不合法或罕見的輸入**寫測試。本專案用兩種方式：

- `Properties/TextGen.cs` 從一組刻意挑選的片段（全形字、`㈱`、`ﬁ`、`㍻`、組合記號、代理對 `𠮷` 等）隨機組合出字串，讓性質測試大量覆蓋各種組合。
- 隨機組合仍然不會碰到 `U+FFFE` 或單獨的代理字元，所以另有單元測試 `TextNormalizerTests.Ill_formed_or_noncharacter_input_does_not_throw` 專門餵這些不合法的輸入。

隨機測試與刻意挑選的邊界案例互補，缺一不可。

---

## 4.6 如何驗證

| 驗收 | 測試 | 方法 |
|---|---|---|
| #1 詞元與位置正確 | `Text/TokenizerExpectedFileTests` | 對照 `TestData/tokenizer-expected.json` 中逐項寫好的 29 個預期案例（全形、大小寫、臺轉台、混排、CJK 長度 1 與 ≥ 2、代理對、標點），另加一個單獨代理字元的測試，共 30 個測試 |
| #1 與參考實作一致 | `Properties/TokenizerProperties` | 隨機字串分別交給正式分詞器與測試專案中的 `ReferenceTokenizer`，比對詞元、位置與單字詞頻 |
| #2 座標還原 | `Text/TextNormalizerTests`、`Properties/TokenizerProperties` | 原始範圍必須包含詞元且最小；不含會變長字元的文字必須完全相等；不切斷代理對 |

參考分詞器刻意用**不同的寫法**：整串先 NFKC、轉成 code point 陣列再掃描、完全不追蹤座標。兩個寫法不同的實作得到相同結果，才能互相印證（第 11 章）。

---

## 練習

1. 用 4.2 節的規則，手動分詞「臺北市ＡＢＣ公司，2024年報告」：列出每個詞元、種類與位置，以及每個漢字的單字詞頻。然後寫一個小程式呼叫 `Tokenizer.TokenizeToList` 驗證。
2. 為什麼 `株` 是單字詞元，而不是和後面的 `公` 組成雙字詞元？
3. 雙字分詞下，查詢「灣大」會命中「台灣大學」。這算是錯誤嗎？如果要避免，你會怎麼改？代價是什麼？
4. `BmpCache` 的競爭為什麼是良性的？如果快取的值是可變物件（例如 `StringBuilder`），還會是良性的嗎？
5. 在 `TokenizerExpectedFileTests` 的預期檔中新增一個案例（例如含有 `①` 的文字），先寫出你預期的結果，再執行測試看看是否正確。
