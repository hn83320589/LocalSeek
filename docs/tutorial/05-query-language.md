# 第 5 章　查詢語言：Lexer、Parser 與 Binder

## 學習目標

- 把一個小型查詢語言拆成詞法分析、語法分析與語意綁定三個階段。
- 用遞迴下降法實作運算子優先序與隱含 AND。
- 在每個語法節點保留原始位置，產生精確的錯誤訊息。
- 用「有界性」判斷查詢是否有意義，並理解 De Morgan 對偶在其中的角色。
- 正確處理日期篩選中的時區問題。

相關程式：`src/LocalSeek.Core/Query/`（`QueryLexer.cs`、`QueryParser.cs`、`SyntaxTree.cs`、`QueryBinder.cs`、`BoundQuery.cs`、`Filters.cs`）

---

## 5.1 語言規格

| 語法 | 例子 | 意義 |
|---|---|---|
| 詞 | `模型`、`report` | 文件含有這個詞 |
| 片語 | `"AI 模型"` | 詞元必須位置連續 |
| AND（可省略） | `資料 AND 分析`、`資料 分析` | 兩者都要有 |
| OR | `model OR 報告` | 有其中一個即可 |
| NOT 或 `-` | `資料 NOT 分析`、`資料 -分析` | 排除 |
| 括號 | `(a OR b) c` | 改變優先序 |
| 前綴 | `da*` | 以 da 開頭的 Latin 詞 |
| 篩選 | `ext:docx`、`path:報告`、`modified:>=2024-01-01`、`size:<10KB` | 不計分的條件 |

優先序：NOT 最高，其次 AND，最後 OR。`AND`、`OR`、`NOT` 必須大寫才是運算子；小寫的 `and` 只是一般的詞。

---

## 5.2 三個階段

```
"報告 -草稿 OR \"AI 模型\" ext:docx"
        │
        ▼ Lexer（詞法分析）：字元 → 詞彙單元
[Word 報告] [Not] [Word 草稿] [Or] [Phrase AI 模型] [Filter ext:docx] [End]
        │
        ▼ Parser（語法分析）：詞彙單元 → 語法樹（只含原始字串）
Or( And( Word 報告, Not( Word 草稿 ) ), And( Phrase "AI 模型", Filter ext:docx ) )
        │
        ▼ Binder（語意綁定）：原始字串 → 詞項（使用分詞器）
Or( And( Term 報告, Not( Term 草稿 ) ), And( Phrase [ai, 模型], Filter ext:docx ) )
```

把 Binder 從 Parser 分出來，原因在第 2 章（難點 D6）已經說明：語法樹不含任何分詞結果，所以測試中的參考解可以用**自己的**分詞器做綁定，與正式引擎互相驗證。

---

## 5.3 Lexer：字元到詞彙單元

Lexer 的規則很簡單：

- 運算子字元只有 `(`、`)`、`"`，以及**開頭的** `-`。
- 其他連續的非空白字元是一個「字」。
- 字如果是 `AND`、`OR`、`NOT`（大寫）就是運算子；以 `*` 結尾是前綴；以 `ext:` 等開頭是篩選條件。

`-` 的判斷值得看一下：

```csharp
// src/LocalSeek.Core/Query/QueryLexer.cs（節錄）
else if (c == '-' && i + 1 < query.Length && !char.IsWhiteSpace(query[i + 1]) && query[i + 1] != ')')
{
    tokens.Add(new LexToken(LexKind.Not, i++, 1));
}
```

`-` 後面必須緊接著內容才是 NOT。所以 `資料 - 分析` 中單獨的 `-` 不是運算子，而會被當成一個字。它分詞後沒有任何詞元，在綁定階段會被忽略。

每個 `LexToken` 都記錄 `Start` 與 `Length`（在原始查詢字串中的位置），這是之後產生精確錯誤訊息的基礎。

---

## 5.4 Parser：遞迴下降

### 文法

```
or      := and ( "OR" and )*
and     := unary ( ["AND"] unary )*          // AND 可以省略
unary   := ("NOT" | "-") unary | primary
primary := "(" or ")" | PHRASE | FILTER | WORD | PREFIX
```

遞迴下降法（recursive descent）的關鍵是：**每一條文法規則對應一個函式，優先序越低的規則越在外層**。`ParseOr` 呼叫 `ParseAnd`，`ParseAnd` 呼叫 `ParseUnary`。所以 NOT 綁得最緊，OR 綁得最鬆，不需要另外寫優先序表。

```csharp
// src/LocalSeek.Core/Query/QueryParser.cs（節錄）
private QueryNode ParseAnd()
{
    var children = new List<QueryNode> { ParseUnary() };
    while (true)
    {
        if (Peek.Kind == LexKind.And)
        {
            var op = Next();
            RequireOperandAfter(op, "AND");
        }
        else if (!StartsOperand(Peek.Kind))
        {
            break;                      // 下一個不是運算元的開頭（例如 OR、右括號、結尾）
        }

        children.Add(ParseUnary());     // 有 AND，或下一個本身就是運算元（隱含 AND）
    }

    return children.Count == 1 ? children[0] : new AndNode(children, ...);
}

private static bool StartsOperand(LexKind kind) =>
    kind is LexKind.LParen or LexKind.Not or LexKind.Word or LexKind.Prefix or LexKind.Phrase or LexKind.Filter;
```

**隱含 AND** 就是這段 `else if`：只要下一個詞彙單元「能夠開始一個運算元」，就當作中間有一個 AND。

### 例子

`報告 -草稿 OR "AI 模型" ext:docx`：

1. `ParseOr` 呼叫 `ParseAnd`。
2. `ParseAnd` 讀到 `報告`；下一個是 NOT，可以開始運算元，於是隱含 AND，`ParseUnary` 讀到 `NOT 草稿`；下一個是 OR，不能開始運算元，結束。得到 `And(報告, Not(草稿))`。
3. 回到 `ParseOr`，看到 OR，再呼叫一次 `ParseAnd`，得到 `And("AI 模型", ext:docx)`。
4. 結果是 `Or(And(...), And(...))`。

---

## 5.5 錯誤位置

每個語法錯誤都帶著位置與長度：

```csharp
public sealed class QuerySyntaxException(string message, int position, int length) : Exception(...)
// 屬性：Reason（說明）、Position（從 0 起算）、Length
```

例子：查詢 `模型 AND (`（括號沒有內容就結束了）。

1. `ParseAnd` 讀到 `模型`，看到 `AND`，確認後面是 `(`，可以開始運算元。
2. `ParsePrimary` 讀到 `(`，接著呼叫 `ParseOr`，一路往下到 `ParsePrimary`。
3. 這時讀到的是 `End`（位置 8，長度 0），落入 `default` 分支：`查詢不完整`。

UI 顯示「查詢有誤：查詢不完整（第 9 個字元）」：內部位置從 0 起算，顯示給使用者時加 1。端對端測試中實際拿到的就是這段文字（見 [test-report.md](../test-report.md) 第 3 節）。

Parser 的設計原則是**錯誤要指向最有用的位置**：

| 查詢 | 錯誤 | 標示的範圍 |
|---|---|---|
| `a AND` | AND 後面缺少條件 | `AND` |
| `(a b` | 缺少右括號 | 左括號 |
| `a b)` | 多餘的右括號 | 右括號 |
| `"a b` | 引號沒有結束 | 從引號到結尾 |
| `()` | 括號內沒有內容 | 整對括號 |

「缺少右括號」標示的是**左括號**，因為使用者需要知道「哪一個括號沒有關」，標在字串結尾沒有幫助。

---

## 5.6 Binder：從字串到詞項

綁定時，每個字用正式分詞器分詞，再依詞元的數量與種類決定變成什麼：

| 字的詞元 | 綁定結果 | 例子 |
|---|---|---|
| 沒有詞元 | 忽略 | `，，，` |
| 一個 Latin 或雙字詞元 | 詞項 | `模型` → `term(模型)` |
| 一個單字詞元 | 單字詞項（用單字詞頻） | `模` → `char(模)` |
| 多個詞元、都不是單字 | 片語 | `AI模型` → `phrase(ai, 模型)` |
| 多個詞元、含單字 | 各詞元的 AND | `國a` → `AND(char(國), term(a))` |

另外兩條規則：

- 雙引號片語內若有單字詞元，回報錯誤（例如 `"國 a"`），因為單字詞元不存位置，無法做片語比對。
- 前綴必須正好是一個 Latin 詞元、至少 2 個字元（`a*` 不合法，因為它幾乎會展開成所有詞）。

```csharp
// src/LocalSeek.Core/Query/QueryBinder.cs（節錄）
private static BoundNode? BindWord(WordNode word)
{
    var tokens = Tokenizer.TokenizeToList(word.Text).Tokens;
    if (tokens.Count == 0)
    {
        return null;
    }

    if (tokens.Count == 1)
    {
        return Single(tokens[0]);
    }

    return tokens.Any(t => t.Kind == TokenKind.CjkSingle)
        ? new AndQuery(tokens.Select(Single).ToList())
        : new PhraseQuery(tokens.Select(t => t.Text).ToList());
}
```

`null` 代表「這個節點沒有作用」。`AND` 與 `OR` 綁定時會把 `null` 子節點濾掉；如果整個查詢綁定完都是 `null`，就回報「查詢沒有可用的詞項或篩選條件」。

---

## 5.7 有界性：拒絕只有 NOT 的查詢

第 1 章的決策 B 定義了**有界性**：一個查詢的結果集合，是否被至少一個正向條件限制住。

```csharp
// src/LocalSeek.Core/Query/QueryBinder.cs
public static bool IsBounded(BoundNode node) => node switch
{
    NotQuery n => IsComplementBounded(n.Operand),
    AndQuery a => a.Children.Any(IsBounded),
    OrQuery o => o.Children.All(IsBounded),
    _ => true,                                   // 詞項、片語、篩選
};

/// Whether NOT node would be bounded (De Morgan dual of IsBounded).
private static bool IsComplementBounded(BoundNode node) => node switch
{
    NotQuery n => IsBounded(n.Operand),
    AndQuery a => a.Children.All(IsComplementBounded),
    OrQuery o => o.Children.Any(IsComplementBounded),
    _ => false,
};
```

### 一個真實的錯誤

最初的版本遇到 NOT 就直接判為無界。這讓 `NOT NOT a` 被當成「只有排除條件」而拒絕，但它等於 `a`，是完全合法的查詢。

正確的想法是：`NOT x` 有沒有界，取決於 **x 的補集**有沒有界。根據 De Morgan 定律：

- `NOT (a AND b)` = `(NOT a) OR (NOT b)`，所以 AND 的補集有界 ⇔ **所有**子節點的補集都有界。
- `NOT (a OR b)` = `(NOT a) AND (NOT b)`，所以 OR 的補集有界 ⇔ **任一**子節點的補集有界。
- `NOT (NOT a)` = `a`，所以它的補集有界 ⇔ a 有界。
- 單一詞項的補集（「不含某詞的所有文件」）是無界的。

`IsComplementBounded` 就是 `IsBounded` 的**對偶**：Any 與 All 互換、基本情況的 true 與 false 互換。

**教學重點**：遇到「邏輯否定」相關的規則，先寫出 De Morgan 對偶通常就能得到正確答案。只處理最直接的情況（NOT → false），很容易漏掉巢狀的組合。

---

## 5.8 篩選條件與時區

篩選條件不計分，只決定文件是否符合。最容易出錯的是 `modified:`：

```csharp
// src/LocalSeek.Core/Query/Filters.cs（節錄）
public sealed record ModifiedFilter(DateComparison Comparison, DateOnly Date) : Filter
{
    public override bool Matches(in DocumentMetadata document, TimeZoneInfo timeZone)
    {
        var utc = DateTime.SpecifyKind(document.LastWriteUtc, DateTimeKind.Utc);
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone));
        return Comparison switch
        {
            DateComparison.Equal => local == Date,
            DateComparison.AtOrAfter => local >= Date,
            DateComparison.AtOrBefore => local <= Date,
            ...
        };
    }
}
```

規格要求以本機（UTC+8）的**日期**比較。檔案系統儲存的是 UTC 時間，所以：

- 一個在台灣時間 2024-01-02 早上 7 點修改的檔案，UTC 時間是 2024-01-01 23:00。
- 如果直接拿 UTC 日期比較，`modified:=2024-01-02` 會錯過它。

這就是驗收標準 #8 要求測試「時區換日」的原因。另外，時區是**注入**的（`TimeZoneInfo timeZone` 參數），不是直接用 `TimeZoneInfo.Local`。測試固定使用 UTC+8，所以在任何時區的機器（包括 UTC 時區的 CI 伺服器）上，結果都一樣。

其他篩選的細節：

- `ext:` 不分大小寫，不含句點（`ext:.txt` 也接受）。
- `path:` 是完整路徑的不分大小寫子字串比對，`/` 與 `\` 視為相同。
- `size:` 的 KB、MB 以 1024 為底，而且是**嚴格**比較（`size:<10KB` 不包含剛好 10240 bytes 的檔案）。

---

## 5.9 測試

| 測試類別 | 案例數 | 重點 |
|---|---|---|
| `Query/QueryParserTests` | 71 | 語法、優先序、隱含 AND、錯誤位置 |
| `Query/QueryBinderTests` | 44 | 綁定規則、單字詞元、有界性 |
| `Query/FilterTests` | 37 | 邊界值：剛好等於、差一單位、大小寫、時區換日 |

另外，`Properties/SearchProperties` 會產生 2000 條以上的隨機查詢（含巢狀括號、NOT、OR），確認正式引擎與參考解**對同一條查詢是否被拒絕**的判斷也一致。

---

## 練習

1. 畫出 `a OR b c -d` 與 `(a OR b) c -d` 的語法樹，說明兩者的差異。
2. `資料-分析`（中間沒有空白）會被怎麼解析？`-` 是 NOT 嗎？分詞後會變成什麼？寫個測試驗證你的答案。
3. 為什麼 `"國 a"` 是錯誤，但 `國a` 不是？從「單字詞元不存位置」的角度解釋。
4. 用 `IsBounded` 與 `IsComplementBounded` 手動判斷 `NOT (a AND NOT b)` 與 `NOT (NOT a OR NOT b)` 是否有界，再用 De Morgan 定律化簡驗證。
5. 新增一個篩選條件 `name:`（只比對檔名，不含路徑）。需要修改哪些地方？先寫測試。
