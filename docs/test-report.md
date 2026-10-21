# LocalSeek 測試報告

- 日期：2026-10-21
- 受測版本：`main` @ `cf5f47d`（與 GitHub `origin/main` 相同）
- 結論：**26 項驗收標準全部符合。** 另有 3 項不影響驗收的發現，見第 7 節；其中 F1 已修正。

---

## 1. 測試環境

| 項目 | 內容 |
|---|---|
| CPU | Intel Core i7-13700（效能量測時把行程限制在 16 個邏輯處理器，也就是 8 個 P-core 含超執行緒） |
| 記憶體 / 磁碟 | 31.7 GB / NVMe SSD |
| 作業系統 | Windows 11 Pro 10.0.26200 |
| .NET | SDK 9.0.315、Runtime 9.0.17，組態 Release |
| CI | GitHub Actions `windows-latest`，`setup-dotnet` 9.0.x |

---

## 2. 執行項目總覽

| # | 項目 | 指令或方法 | 結果 |
|---|---|---|---|
| T1 | 乾淨建置整個 Solution | `dotnet build LocalSeek.sln -c Release --no-incremental`（`TreatWarningsAsErrors`） | ✅ 0 個警告、0 個錯誤 |
| T2 | 完整單元測試，連跑 3 次 | `dotnet test tests/LocalSeek.Tests -c Release` | ✅ 3 次都是 464/464 通過，各約 7 至 9 秒 |
| T3 | 性質測試另外試 20 個種子 | `LOCALSEEK_SEED=1001…1020`，篩選 `Properties` | ✅ 所有與參考解的比對都通過；有 3 個「涵蓋量下限」斷言在單一案例重播模式下失敗，屬於測試工具的問題，見 F1（已修正，修正後重跑 20 個種子 0 失敗） |
| T4 | 並行與計時類測試重複 10 次 | 篩選 `ConcurrencyStress`、`SnapshotIsolation`、`CancellationLatency` | ✅ 10/10 通過 |
| T5 | 50,000 份文件的效能量測（從零重建） | `tests/LocalSeek.Perf --cores 16 --threads 8 --queries 300` | ✅ 全部在限制內，見第 4 節 |
| T6 | 由新行程開啟既有索引 | 同上，加 `--skip-build` | ✅ 166 ms |
| T7 | 實際 App 端對端冒煙測試 | 以 CI 相同的指令發佈 win-x64，再用 UI Automation 操作 App，見第 3 節 | ✅ |
| T8 | CI | GitHub Actions run [39406707082](https://github.com/hn83320589/LocalSeek/actions/runs/39406707082) | ✅ success；兩個 artifact 都已上傳 |
| T9 | 靜態檢查：專案切分、套件、獨立性 | 檢查 csproj，並用 grep 搜尋原始碼 | ✅ 見第 6 節 |

### 2.1 各測試類別的案例數（T2，共 464 個，全部通過）

| 類別 | 數量 | 類別 | 數量 |
|---|---|---|---|
| Architecture.LayeringTests | 5 | Pipeline.IndexingServiceTests | 7 |
| Crawling.CrawlerTests | 15 | Properties.*（12 組性質） | 13 |
| Crawling.PhysicalFileSystemTests | 4 | Query.FilterTests | 37 |
| Extraction.ExtractionTests | 34 | Query.QueryBinderTests | 44 |
| Index.ConcurrencyStressTests | 1 | Query.QueryParserTests | 71 |
| Index.CorruptionTests | 12 | Samples.SampleCorpusTests | 17 |
| Index.FaultInjectionTests | 16 | Search.QueryExecutorTests | 11 |
| Index.IndexWriterTests | 11 | Search.SnippetTests | 17 |
| Index.MergeTests | 7 | Settings.GlobTests | 35 |
| Index.SnapshotIsolationTests | 4 | Settings.SettingsStoreTests | 9 |
| Pipeline.DocumentPreviewerTests | 2 | Text.TextNormalizerTests | 18 |
| Pipeline.IndexingEngineTests | 17 | Text.TokenizerExpectedFileTests | 30 |
| ViewModels.SearchViewModelTests | 17 | ViewModels.OtherViewModelTests | 6 |
| ViewModels.AppHostTests | 3 | ViewModels.CancellationLatencyTests | 1 |

---

## 3. 實際 App 端對端測試（T7）

**做法**
- 以 CI 相同的指令發佈 App：`dotnet publish … -r win-x64 -p:PublishProfile=win-x64`。
- 用 `LOCALSEEK_DATA_DIR` 指向全新的資料目錄，根資料夾設為 `samples/corpus` 的複本。
- 用 Windows UI Automation 輸入查詢，再讀取畫面上的文字。

| 檢查 | 預期 | 實際 | 結果 |
|---|---|---|---|
| 首次索引 | 14 個檔案處理完，索引 12 份文件 | 「已是最新｜已處理 14｜待處理 0」「索引 12 份文件，1 個片段」 | ✅ |
| 16 個手算查詢的命中數（`samples/expected`） | 模型 5、資料 7、臺灣 2、AI 4、"AI模型" 3、模 5、𠮷野 1、file 1、da* 2、株 1、model OR 報告 3、資料 -ext:csv 6、資料 NOT 分析 5、ext:txt 8、資料分析 2、科學 學習 1 | 16 筆全部相同 | ✅ |
| 分數與手算一致 | 「模型」：05-page.html 0.849、01-utf8-nobom.txt 0.774 | 0.849、0.774，排序也相同 | ✅ |
| 預覽高亮 | 「12-模型手冊.txt」檔名中的「模型」被標示 | 有標示 | ✅ |
| 語法錯誤 | `模型 AND (` 顯示位置與說明，程式不崩潰 | 「查詢有誤：查詢不完整（第 9 個字元）」，程式仍在執行 | ✅ |
| 新增檔案後可被搜尋 | ≤ 3 秒 | 589 ms（第一次執行時為 563 ms） | ✅ |
| 改名 | 結果顯示新路徑 | 顯示 `e2e-renamed.txt` | ✅ |
| 刪除 | 結果消失 | 551 ms 後變成 0 筆 | ✅ |
| 診斷頁 | 跳過 2 個檔案：二進位 1、無法判定編碼 1，並列出路徑與原因 | 完全相同；索引文件數 12，與搜尋頁一致 | ✅ |

---

## 4. 效能（T5、T6）

語料：`CorpusGen --mixed` 產生的 50,000 個檔案，943.9 MB，平均 19.3 KB。

| 項目 | 本次重測 | README 原記錄 | 限制 | 結果 |
|---|---|---|---|---|
| 完整建立索引（含背景合併完成） | 116.9 s（抽取到提交為 92.6 s） | 109 s | ≤ 240 s | ✅ |
| 記憶體峰值（Working Set） | 395 MB | 398 MB | ≤ 600 MB | ✅ |
| 開啟既有索引（同一行程） | 181 ms | 142 ms | ≤ 2 s | ✅ |
| 開啟既有索引（新行程，T6） | 166 ms | — | ≤ 2 s | ✅ |
| 兩詞 AND P95（300 條） | 1.5 ms | 1.4 ms | ≤ 100 ms | ✅ |
| 片語 P95（300 條） | 51.1 ms（新行程 44.0 ms） | 43 ms | ≤ 100 ms | ✅ |
| 存檔到可被搜尋（10 次，含 500 ms 防抖） | 平均 541 ms，最大 546 ms | 平均 545 ms，最大 566 ms | ≤ 3 s | ✅ |

兩次結果的差距都在 ±25% 以內，README 上的數據仍然有代表性。

---

## 5. 驗收標準逐項判定

| # | 標準（摘要） | 題目要求的判定方式 | 證據 | 結果 |
|---|---|---|---|---|
| 1 | 分詞：全形、大小寫、臺→台、混排、CJK 長度 1 與 ≥2、代理對、標點，與預期檔逐項一致 | 單元測試 | `TokenizerExpectedFileTests`（30 個，對照 `TestData/tokenizer-expected.json`）、`TokenizerProperties` 與參考 tokenizer 比對 | ✅ |
| 2 | 原始位置還原（㈱、ﬁ、全形、代理對） | 單元測試＋性質測試 | `TextNormalizerTests`（18 個）、`TokenizerProperties`：原始範圍包含詞元且最小 | ✅ |
| 3 | UTF-8 有無 BOM、UTF-16、Big5、HTML、docx、CSV 正確抽取；二進位、超大檔、無法判定編碼者跳過並記錄 | 單元測試 | `ExtractionTests`（34 個）；T7 診斷頁實際顯示 2 個被跳過的檔案 | ✅ |
| 4 | 查詢解析：優先序、隱含 AND、-/NOT、括號、前綴、篩選；錯誤回報位置；只有 NOT 的查詢被拒絕 | 單元測試 | `QueryParserTests`（71 個）、`QueryBinderTests`（44 個）；T7 實際顯示錯誤位置 | ✅ |
| 5 | 布林命中集合與參考解一致（≥300 份文件、≥2000 條查詢） | 性質測試 | `SearchProperties.Boolean_hit_sets_match_reference`：320 份文件、2600 次迭代，斷言至少 2000 條有效查詢；T3 另外試了 20 個種子 | ✅ |
| 6 | 片語（Latin、CJK、跨文字系統）出現次數一致，不跨標點拼字 | 性質測試 | `SearchProperties.Phrase_occurrence_counts_match_reference`，斷言至少 100 條跨文字系統片語 | ✅ |
| 7 | 單一 CJK 字元與前綴詞的命中與 tf | 性質測試 | `SearchProperties`（單字與前綴）；T7「模」「da*」「株」與手算相同 | ✅ |
| 8 | 篩選條件的邊界值（等於、差一、大小寫、時區換日） | 單元測試 | `FilterTests`（37 個），含 `Modified_filter_compares_local_utc8_dates` | ✅ |
| 9 | 分數相對誤差 ≤ 1e-9，排序（含同分依路徑）一致 | 性質測試 | `SearchProperties`（計分）；`SampleCorpusTests` 與手算一致 | ✅ |
| 10 | 隨機增刪改後，分數等於只含現存文件的參考解 | 性質測試 | `IndexProperties`（隨機增、改、刪、改名、合併） | ✅ |
| 11 | 增量更新加溢位後的差異掃描，結果等於從零重建 | 性質測試 | `IncrementalProperties`；`Overflow_triggers_a_difference_scan_that_catches_up` | ✅ |
| 12 | 內容未變的改名或移動不重新抽取（計數器斷言） | 單元測試 | `IndexingEngineTests`：`Rename_event_updates_the_path_without_extracting_or_tokenizing`、`Move_across_folders_…_detected_by_hash`、`Directory_rename_moves_all_files_without_extracting`、`Moves_found_by_a_difference_scan_are_not_extracted_either`；T7 改名後路徑已更新 | ✅ |
| 13 | 爬檔：接合點迴圈、長路徑、鎖定重試、權限不足、讀取中被修改、隱藏與系統檔 | 單元測試（假 FS）＋實測 | 假 FS：`CrawlerTests`（15 個）、`Reader_classifies_locked_changed_missing_and_denied_files`、鎖定檔重試 3 次。實測：`Real_junction_loop_terminates`、`Real_long_path_is_read`、`Real_exclusively_locked_file_is_reported_as_locked` | ✅（權限不足與讀取中被修改只用假 FS 驗證，見 F3） |
| 14 | 快照隔離；被取代的檔案在最後一個舊讀者釋放後才刪除 | 單元測試 | `SnapshotIsolationTests`，含 `Superseded_files_are_deleted_only_after_the_last_old_reader_releases`；T4 重複 10 次 | ✅ |
| 15 | 提交與合併的每個 I/O 操作注入故障，重開後一致 | 故障注入測試 | `FaultInjectionTests`（16 個）：7 種情境 × 每個 I/O 操作 × 有無 torn write，另測復原過程本身再次當機 | ✅ |
| 16 | 片段數對數成長、合併前後結果不變、墓碑清除 | 性質測試 | `IndexProperties`、`MergeTests`（7 個）；T5 結束時 50,000 份文件只剩 1 個片段 | ✅ |
| 17 | 索引與合併期間同時搜尋，不丟例外、不出現不一致 | 壓力測試（固定種子） | `ConcurrencyStressTests`；T2 跑 3 次、T4 跑 10 次，共 13 次全過 | ✅ |
| 18 | 摘要片段數、長度、順序，高亮精確，不切代理對，重疊合併 | 性質測試 | `SnippetProperties`、`SnippetTests`（17 個）；T7 實際顯示高亮 | ✅ |
| 19 | 版本不符、校驗碼不符、片段遺失：明確提示、只重建受影響部分、不崩潰 | 單元測試 | `CorruptionTests`（12 個）：segment 或 manifest 版本不符、CRC 不符、片段遺失、刪除檔損毀、退回前一個提交 | ✅ |
| 20 | ViewModel：防抖、取消舊查詢、舊結果不覆蓋、50 ms 內取消、鍵盤命令 | ViewModel 測試（假時鐘與假排程器） | `SearchViewModelTests`（17 個，使用 `FakeTimeProvider`）、`CancellationLatencyTests`（在兩萬份文件的索引上實測，取 5 次的中位數） | ✅ |
| 21 | glob（*、**、?、大小寫、分隔符）；設定檔損毀時用預設值並提示 | 單元測試 | `GlobTests`（35 個）、`SettingsStoreTests`（9 個），含 `Damaged_file_falls_back_to_defaults_with_warning_and_backup` | ✅ |
| 22 | 效能符合限制，README 載明測量環境 | 效能測試＋實測 | 第 4 節重測全數符合；README 第 7 節與 `docs/perf-results.md` | ✅ |
| 23 | 診斷頁列出跳過與失敗的檔案與原因，數字一致 | 實測＋測試 | `OtherViewModelTests`（計數一致）；T7 診斷頁實測 | ✅ |
| 24 | ≥ 250 個案例（含 ≥ 8 組性質測試），全數通過 | 執行測試 | 464 個案例、12 組性質測試，T2 三次全過，CI 也通過；修正 F1 後為 472 個，全數通過 | ✅ |
| 25 | README 含建置、截圖、架構、格式、演算法、效能、取捨、限制 | 檢視 README | README 第 1 至 10 節，含 4 張截圖 | ✅ |
| 26 | CI 在 Windows 建置並測試、綠燈、README 有徽章、上傳 win-x64 artifact | 檢視 Actions | T8：run 39406707082 success；artifact `LocalSeek-win-x64`（86.9 MB）與 `test-results`；README 第 3 行有徽章 | ✅ |

---

## 6. 其他技術限制檢查（T9）

| 限制 | 檢查方式 | 結果 |
|---|---|---|
| Core、Indexing、App.ViewModels 為純 `net9.0`，不引用 UI | 檢查 csproj，並由 `LayeringTests` 斷言 | ✅ |
| App 使用 Uno.Sdk、`net9.0-windows10.0.19041`、只引用 ViewModels | 檢查 csproj | ✅ |
| 不使用 Lucene.NET、SQLite 或其他全文檢索程式庫，不呼叫網路 | grep `lucene\|sqlite\|HttpClient\|WebClient`，沒有結果；套件只有 CommunityToolkit.Mvvm、System.IO.Hashing，測試另用 TimeProvider.Testing 與 xUnit | ✅ |
| 所有索引檔 I/O 經過 `IIndexStorage` | grep Core 中的 `File.*` 與 `FileStream`，只出現在 `FileSystemIndexStorage` | ✅ |
| 「現在時間」都透過 `TimeProvider` | grep `src` 中的 `DateTime.Now`、`DateTime.UtcNow`、`Thread.Sleep`，沒有結果 | ✅ |
| 參考解與正式程式碼互相獨立 | `Reference/` 只引用 `LocalSeek.Core.Query` 的資料型別，沒有呼叫正式的 Tokenizer、Normalizer、Binder、Bm25、Executor | ✅ |
| 範例語料 ≥ 12 份、手算 ≥ 12 個查詢 | `samples/corpus` 有 14 個檔案（12 份會被索引），`samples/expected` 有 16 個查詢 | ✅ |

---

## 7. 發現事項（不影響驗收）

### F1：性質測試在單一種子重播模式下，會因涵蓋量斷言而失敗（輕微，屬於測試工具）— 已修正

- **現象**：設定 `LOCALSEEK_SEED` 後只會執行 1 個案例，但以下 3 個性質測試在迴圈結束後還會檢查涵蓋量下限，因此一定失敗：`Boolean_hit_sets_match_reference`（至少 2000 條查詢）、`Phrase_occurrence_counts_match_reference`（至少 300 條片語）、`Snippets_and_highlights_are_bounded_ordered_exact_and_merged`（至少 1000 個命中）。
- **影響**：
  - 重播一個真正失敗的案例時，比對錯誤會先在迴圈內拋出，所以重現功能仍然有效。
  - 但重播成功時會出現誤導性的失敗訊息。
  - 正常執行（不設定環境變數）不受影響。
- **修正**：
  - 新增 `Property.AssertCoverage`：正常執行時照常檢查涵蓋量下限，重播模式下略過。
  - 上述 5 個涵蓋量斷言改用它；性質本身的比對斷言都在迴圈內，沒有更動。
  - 新增 `PropertyRunnerTests`（8 個案例），驗證種子解析，以及兩種模式下的行為。
- **驗證**：修正後全部 472 個測試通過；`LOCALSEEK_SEED=1001…1020` 重播 20 次，0 失敗。

### F2：單一片語查詢最慢約 109 ms

- 從零重建後那一次量測中，300 條片語的最大值是 108.8 ms；P95 是 51.1 ms，符合限制。
- 新行程那次的最大值是 98.2 ms。
- 題目只限制 P95，所以符合規格。最慢的查詢都是由高頻雙字詞組成的長片語。

### F3：部分爬檔情境沒有在真實磁碟上實測

- 「權限不足」與「讀取中被修改」只在假檔案系統上驗證。
- 真實磁碟上實測過的是接合點迴圈、長路徑、獨占鎖定檔。
- 真實磁碟上要製造 ACL 拒絕存取，需要修改權限設定；我判斷不適合在使用者的機器上這樣做，因此沒有進行。

### 其他備註

- 開啟索引的時間是在作業系統檔案快取已暖的狀態下量測的（T6 雖然是新行程，但索引檔剛被讀過）。重開機後的冷啟動沒有量測。
- 這次 T7 沒有在實際 App 中操作 Ctrl+K、方向鍵、Enter 這些鍵盤命令；它們由 `SearchViewModelTests` 驗證，App 端只負責把按鍵轉給 ViewModel 的 `HandleKey`。
- CI 中 `actions/*@v4` 有 Node 20 停用的提示，目前不影響執行。

---

## 8. 重現方式

```powershell
dotnet restore LocalSeek.sln -p:Configuration=Release
dotnet build LocalSeek.sln -c Release --no-restore
dotnet test tests/LocalSeek.Tests -c Release --no-build

# 效能（需先產生語料；不要放進 repo）
dotnet run -c Release --project tools/LocalSeek.CorpusGen -- --out D:\perf\corpus --count 50000 --mixed
dotnet run -c Release --project tests/LocalSeek.Perf -- --corpus D:\perf\corpus --work D:\perf\work --threads 8 --queries 300 --cores 16
```

---

## 9. 第二輪：依審查意見的修正（2026-10-21）

| 審查意見 | 處理 | 證據 |
|---|---|---|
| 簡體 GBK 檔案被誤判為 Big5，索引成亂碼 | **已修正**：通過 Big5 結構檢查後，漢字達 10 個以上時，常用字（Big5 第一級，0xA440–0xC67E）須占 85% 以上，否則以「無法判定編碼」跳過，並在診斷頁顯示「疑似簡體 GBK」 | `ExtractionTests`：`Simplified_chinese_gbk_is_not_mistaken_for_big5`（先確認這段 GBK 位元組確實是合法的 Big5，修正前此測試失敗）、`Big5_with_a_few_rare_ideographs_is_still_accepted`、`Too_few_ideographs_to_judge_fall_back_to_big5` |
| 缺少測試報告與 CI 證據 | 本文件；CI 連結見第 2 節 T8，最新 commit 的 CI 結果見下方 | 本機 trx：475/475 通過 |
| 效能查詢集沒有涵蓋最高頻詞 | **已補上**：最高頻 25 詞兩兩配對的 AND 與片語各 300 條，並寫出詞頻分佈 | `docs/perf-results.md` 第 3 節與第 5 節 |
| 路徑寫死 Windows 的 `\` | README 已知限制註明「只支援 Windows」，程式不修改（題目只針對 Windows） | README 第 9 節 |
| 保留兩個提交，大合併後暫時多留舊片段 | 刻意的取捨，維持不變 | README 第 8 節 |
| 沒有測試證明片段數以對數成長 | **與事實不符**：`IndexProperties.Merging_preserves_results_and_keeps_segment_count_logarithmic` 在加入 500 份文件的過程中，每 10 份提交一次，並斷言片段數不超過 `(MergeFactor − 1) × (層數 + 1)`，也就是 O(log N) 的上限 | `tests/LocalSeek.Tests/Properties/IndexProperties.cs` |

### 門檻值的依據

- **做法**：把 repo 內的繁體中文文件切成每段 10、20、30、40 個漢字的樣本。
  - 繁體樣本直接編成 Big5。
  - 簡體樣本先用 Windows 內建的繁轉簡功能轉換，再編成 GBK，然後當成 Big5 解讀。
- **結果**：

| 每段漢字數 | 繁體被誤擋（門檻 85%） | GBK 被誤收（門檻 85%） | 繁體最低比例 | GBK 最高比例 |
|---|---|---|---|---|
| 10 | 0 / 1462 | 16 / 1462（1.1%） | 90.0% | 100% |
| 20 | 0 / 730 | 2 / 730（0.3%） | 95.0% | 90.0% |
| 30 | 0 / 486 | 0 / 486 | 96.7% | 80.0% |
| 40 | 0 / 363 | 0 / 363 | 97.5% | 72.5% |

- **限制**：
  - 漢字少於 10 個的檔案無法可靠判斷，仍依位元組結構當成 Big5。
  - 大量使用罕用字的繁體檔案（例如古籍、罕見人名）可能被擋。被擋的檔案會出現在診斷頁，不會無聲遺失。

### 效能重測（新語料）

`CorpusGen` 改為只用 Big5 常用字，否則它產生的 Big5 檔案會被新規則擋下。因此重新產生了 50,000 份語料並重新量測：

| 項目 | 結果 | 限制 | 結果 |
|---|---|---|---|
| 完整建立索引 | 135.2 s | ≤ 240 s | ✅ |
| 記憶體峰值 | 402 MB | ≤ 600 MB | ✅ |
| 開啟既有索引 | 192 ms（新行程 154 ms） | ≤ 2 s | ✅ |
| 兩詞 AND P95（隨機 / 最高頻） | 1.6 ms / 1.9 ms | ≤ 100 ms | ✅ |
| 片語 P95（隨機） | 48.8 ms（第二次 54.0 ms） | ≤ 100 ms | ✅ |
| 片語 P95（最高頻 25 詞兩兩配對） | 81.2 ms（第二次 **98.0 ms**） | ≤ 100 ms | ✅，但餘裕很小 |
| 存檔到可被搜尋 | 最大 566 ms | ≤ 3 s | ✅ |

**新發現 F4**：
- **現象**：最壞情境的片語查詢只是勉強符合限制。最高頻的詞出現在全部 50,000 份文件中，由它們組成的片語必須解碼兩個詞在每份文件中的位置，第二次量測的 P95 是 98.0 ms，單一查詢最慢 162 ms。
- **影響**：在較慢的 8 核心機器上可能超過 100 ms。
- **可能的改善方向**：把片語比對依文件範圍分段平行處理，或為高頻雙字詞的組合另建索引。這屬於架構層面的變更，尚未進行。
