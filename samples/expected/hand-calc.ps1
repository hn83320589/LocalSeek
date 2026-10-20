# Arithmetic for the hand calculation. All inputs (tokens, len, tf, df) below were derived by hand from
# the tokenization rules; this script only evaluates idf and the BM25 formula and formats the steps.
$ErrorActionPreference = "Stop"
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$out = $PSScriptRoot
New-Item -ItemType Directory -Force $out | Out-Null

$docs = [ordered]@{
  "01-utf8-nobom.txt" = @{ Body = "台灣 灣大 大學 學的 ai 模型 型研 研究"; BodyLen = 8; Name = "01 utf8 nobom txt"; NameLen = 4 }
  "02-utf8-bom.txt"   = @{ Body = "ai model report the model is good"; BodyLen = 7; Name = "02 utf8 bom txt"; NameLen = 4 }
  "03-utf16.txt"      = @{ Body = "台灣 灣資 資料 料分 分析 析報 報告"; BodyLen = 7; Name = "03 utf16 txt"; NameLen = 3 }
  "04-big5.txt"       = @{ Body = "台北 北市 市政 政府 府資 資料 料開 開放"; BodyLen = 8; Name = "04 big5 txt"; NameLen = 3 }
  "05-page.html"      = @{ Body = "資料 料科 科學 ／ 學習 ／ 研究 ／ ai ／ 模型"; BodyLen = 7; Name = "05 page html"; NameLen = 3 }
  "06-report.docx"    = @{ Body = "期末 末報 報告 ／ 資料 料分 分析 ／ data ／ analysis"; BodyLen = 8; Name = "06 report docx"; NameLen = 3 }
  "07-fullwidth.txt"  = @{ Body = "ai ／ 模型 型與 ／ data ／ analysis"; BodyLen = 5; Name = "07 fullwidth txt"; NameLen = 3 }
  "08-surrogate.txt"  = @{ Body = "𠮷野 野家 家的 的牛 牛丼"; BodyLen = 5; Name = "08 surrogate txt"; NameLen = 3 }
  "09-compat.txt"     = @{ Body = "株(單字) ／ 公司 司的 ／ file ／ 資料"; BodyLen = 5; Name = "09 compat txt"; NameLen = 3 }
  "10-資料.csv"       = @{ Body = "名稱 ／ 值(單字) ／ 模型 ／ 100"; BodyLen = 4; Name = "10 資料 csv"; NameLen = 3 }
  "11-notes.md"       = @{ Body = "筆記 ／ 資料 ／ 資料 ／ 資料"; BodyLen = 4; Name = "11 notes md"; NameLen = 3 }
  "12-模型手冊.txt"   = @{ Body = "使用 用說 說明"; BodyLen = 3; Name = "12 模型 型手 手冊 txt"; NameLen = 5 }
}
foreach ($d in $docs.Values) { $d.Len = $d.BodyLen + 2 * $d.NameLen }
$N = $docs.Count
$total = [int]($docs.Values | ForEach-Object { $_.Len } | Measure-Object -Sum).Sum
$avgdl = $total / $N

# Each query: terms with per-document tf = body + 2 × name, and the matching documents (boolean result).
$queries = @(
  @{ Id = "Q1"; Query = "模型"; Note = "內文與檔名命中（12 號的檔名 ×2）"; Terms = @(@{ Key = "term(模型)"; Tf = @{ "01-utf8-nobom.txt" = 1; "05-page.html" = 1; "07-fullwidth.txt" = 1; "10-資料.csv" = 1; "12-模型手冊.txt" = 2 } }) }
  @{ Id = "Q2"; Query = "資料"; Note = "Big5、UTF-16、HTML 標題、docx、相容字元後的詞元、檔名命中"; Terms = @(@{ Key = "term(資料)"; Tf = @{ "03-utf16.txt" = 1; "04-big5.txt" = 1; "05-page.html" = 1; "06-report.docx" = 1; "09-compat.txt" = 1; "10-資料.csv" = 2; "11-notes.md" = 3 } }) }
  @{ Id = "Q3"; Query = "臺灣"; Note = "臺→台 正規化"; Terms = @(@{ Key = "term(台灣)"; Tf = @{ "01-utf8-nobom.txt" = 1; "03-utf16.txt" = 1 } }) }
  @{ Id = "Q4"; Query = "AI"; Note = "大小寫與全形（07 號的 ＡＩ）"; Terms = @(@{ Key = "term(ai)"; Tf = @{ "01-utf8-nobom.txt" = 1; "02-utf8-bom.txt" = 1; "05-page.html" = 1; "07-fullwidth.txt" = 1 } }) }
  @{ Id = "Q5"; Query = '"AI模型"'; Note = "跨文字系統片語；HTML 的 <script> 內容不算"; Terms = @(@{ Key = "phrase(ai 模型)"; Tf = @{ "01-utf8-nobom.txt" = 1; "05-page.html" = 1; "07-fullwidth.txt" = 1 } }) }
  @{ Id = "Q6"; Query = "模"; Note = "單一 CJK 字：使用單字詞頻（含雙字詞元中的字，檔名 ×2）"; Terms = @(@{ Key = "char(模)"; Tf = @{ "01-utf8-nobom.txt" = 1; "05-page.html" = 1; "07-fullwidth.txt" = 1; "10-資料.csv" = 1; "12-模型手冊.txt" = 2 } }) }
  @{ Id = "Q7"; Query = "𠮷野"; Note = "代理對字元"; Terms = @(@{ Key = "term(𠮷野)"; Tf = @{ "08-surrogate.txt" = 1 } }) }
  @{ Id = "Q8"; Query = "file"; Note = "連字 ﬁ 經 NFKC 成為 fi"; Terms = @(@{ Key = "term(file)"; Tf = @{ "09-compat.txt" = 1 } }) }
  @{ Id = "Q9"; Query = "da*"; Note = "前綴詞：所有以 da 開頭的 Latin 詞元"; Terms = @(@{ Key = "prefix(da)"; Tf = @{ "06-report.docx" = 1; "07-fullwidth.txt" = 1 } }) }
  @{ Id = "Q10"; Query = "株"; Note = "㈱ 經 NFKC 成為 (株)，株 是單字詞元"; Terms = @(@{ Key = "char(株)"; Tf = @{ "09-compat.txt" = 1 } }) }
  @{ Id = "Q11"; Query = "model OR 報告"; Note = "OR：各文件只加總自己有命中的詞項"; Terms = @(
      @{ Key = "term(model)"; Tf = @{ "02-utf8-bom.txt" = 2 } },
      @{ Key = "term(報告)"; Tf = @{ "03-utf16.txt" = 1; "06-report.docx" = 1 } }) }
  @{ Id = "Q12"; Query = "資料 -ext:csv"; Note = "篩選條件不計分，也不影響 df"; Exclude = @("10-資料.csv"); Terms = @(@{ Key = "term(資料)"; Tf = @{ "03-utf16.txt" = 1; "04-big5.txt" = 1; "05-page.html" = 1; "06-report.docx" = 1; "09-compat.txt" = 1; "10-資料.csv" = 2; "11-notes.md" = 3 } }) }
  @{ Id = "Q13"; Query = "資料 NOT 分析"; Note = "NOT 排除 03、06 號，不計分"; Exclude = @("03-utf16.txt", "06-report.docx"); Terms = @(@{ Key = "term(資料)"; Tf = @{ "03-utf16.txt" = 1; "04-big5.txt" = 1; "05-page.html" = 1; "06-report.docx" = 1; "09-compat.txt" = 1; "10-資料.csv" = 2; "11-notes.md" = 3 } }) }
  @{ Id = "Q14"; Query = "ext:txt"; Note = "只有篩選條件：分數為 0，依路徑排序"; FilterOnly = @("01-utf8-nobom.txt", "02-utf8-bom.txt", "03-utf16.txt", "04-big5.txt", "07-fullwidth.txt", "08-surrogate.txt", "09-compat.txt", "12-模型手冊.txt"); Terms = @() }
  @{ Id = "Q15"; Query = "資料分析"; Note = "無空白的 CJK 字＝片語（資料 料分 分析），位置須連續"; Terms = @(@{ Key = "phrase(資料 料分 分析)"; Tf = @{ "03-utf16.txt" = 1; "06-report.docx" = 1 } }) }
  @{ Id = "Q16"; Query = "科學 學習"; Note = "隱含 AND，兩個詞項分數相加"; Terms = @(
      @{ Key = "term(科學)"; Tf = @{ "05-page.html" = 1 } },
      @{ Key = "term(學習)"; Tf = @{ "05-page.html" = 1 } }) }
)

function F([double]$x) { $x.ToString("0.000000000", $inv) }
$md = New-Object System.Text.StringBuilder
$null = $md.AppendLine("# 範例語料的逐步手算結果")
$null = $md.AppendLine("")
$null = $md.AppendLine("語料在 [``samples/corpus``](../corpus)。以下的詞元、tf、df、len 皆依假設事項的分詞規則**人工推導**；")
$null = $md.AppendLine("只有最後的算術（ln 與除法）用計算機完成。測試 ``SampleCorpusTests`` 驗證引擎結果與本表一致（相對誤差 ≤ 1e-9）。")
$null = $md.AppendLine("")
$null = $md.AppendLine("## 1. 文件、詞元與長度")
$null = $md.AppendLine("")
$null = $md.AppendLine("len(d) = 內文詞元數 + 2 × 檔名詞元數（「／」表示區段之間的分隔；單字詞元計入長度）。")
$null = $md.AppendLine("")
$null = $md.AppendLine("| 檔案 | 內文詞元 | 內文數 | 檔名詞元 | 檔名數 | len |")
$null = $md.AppendLine("|---|---|---|---|---|---|")
foreach ($k in $docs.Keys) { $d = $docs[$k]; $null = $md.AppendLine("| $k | $($d.Body) | $($d.BodyLen) | $($d.Name) | $($d.NameLen) | $($d.Len) |") }
$null = $md.AppendLine("")
$null = $md.AppendLine("被跳過（不計入 N）：``13-binary.txt``（大量 NUL，二進位）、``14-unknown-encoding.txt``（非有效 UTF-8 或 Big5）。")
$null = $md.AppendLine("docx 的頁首（header1.xml「頁首不索引」）與刪除修訂（w:delText「已刪除」）不在內文中；HTML 的 ``<script>`` 內容不在內文中。")
$null = $md.AppendLine("")
$null = $md.AppendLine("N = $N，Σlen = $total，avgdl = $total / $N = $(F $avgdl)")
$null = $md.AppendLine("")
$null = $md.AppendLine("公式：idf = ln(1 + (N − df + 0.5) / (df + 0.5))；score = Σ idf × tf × 2.2 / (tf + 1.2 × (0.25 + 0.75 × len / avgdl))")
$null = $md.AppendLine("")
$null = $md.AppendLine("## 2. 查詢")

$expected = @()
foreach ($q in $queries) {
  $null = $md.AppendLine("")
  $null = $md.AppendLine("### $($q.Id)　``$($q.Query)``")
  $null = $md.AppendLine("")
  $null = $md.AppendLine("$($q.Note)。")
  $null = $md.AppendLine("")
  $termStats = @()
  $scores = @{}
  foreach ($t in $q.Terms) {
    $df = $t.Tf.Count
    $idf = [Math]::Log(1 + ($N - $df + 0.5) / ($df + 0.5))
    $termStats += [ordered]@{ key = $t.Key; df = $df; idf = $idf }
    $null = $md.AppendLine("- **$($t.Key)**：df = $df，idf = ln(1 + ($N − $df + 0.5) / ($df + 0.5)) = $(F $idf)")
    foreach ($file in ($t.Tf.Keys | Sort-Object)) {
      $tf = $t.Tf[$file]; $len = $docs[$file].Len
      $norm = 1.2 * (0.25 + 0.75 * $len / $avgdl)
      $part = $idf * $tf * 2.2 / ($tf + $norm)
      $null = $md.AppendLine("  - $file：tf = $tf，len = $len，1.2 × (0.25 + 0.75 × $len / avgdl) = $(F $norm)，分項 = $(F $idf) × $tf × 2.2 / ($tf + $(F $norm)) = $(F $part)")
      $scores[$file] = $scores[$file] + $part
    }
  }

  if ($q.FilterOnly) { foreach ($f in $q.FilterOnly) { $scores[$f] = 0.0 } }
  foreach ($x in @($q.Exclude)) { if ($x) { $scores.Remove($x) } }
  $hits = $scores.GetEnumerator() | Sort-Object @{ Expression = { $_.Value }; Descending = $true }, @{ Expression = { $_.Key.ToUpperInvariant() }; Ascending = $true } |
    ForEach-Object { [ordered]@{ file = $_.Key; score = $_.Value } }
  $null = $md.AppendLine("")
  $null = $md.AppendLine("| 名次 | 檔案 | 分數 |")
  $null = $md.AppendLine("|---|---|---|")
  $rank = 1
  foreach ($h in $hits) { $null = $md.AppendLine("| $rank | $($h.file) | $(F $h.score) |"); $rank++ }
  $expected += [ordered]@{ id = $q.Id; query = $q.Query; totalHits = @($hits).Count; terms = $termStats; hits = @($hits) }
}

$lengths = [ordered]@{}; foreach ($k in $docs.Keys) { $lengths[$k] = $docs[$k].Len }
$json = [ordered]@{ documentCount = $N; totalLength = $total; lengths = $lengths; skipped = @("13-binary.txt", "14-unknown-encoding.txt"); queries = $expected }
[System.IO.File]::WriteAllText("$out\expected.json", ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText("$out\README.md", $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
"N=$N total=$total avgdl=$(F $avgdl); queries=$($queries.Count)"
