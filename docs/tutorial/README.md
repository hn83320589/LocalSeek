# LocalSeek 教學：從需求到實作的全文檢索系統

這份教材以 LocalSeek 為完整案例：它是一個本機全文檢索桌面工具，大約一萬行 C#，附 477 個自動化測試。教材依軟體開發的順序，說明一個中型系統如何從**需求**走到**分析**、**架構**、**程式**與**驗證**。

每一章都對應 repo 中實際的程式碼與 commit，讀者可以邊讀邊打開原始碼對照。

## 適合的讀者

- 已經會寫 C#（類別、介面、泛型、LINQ、`async`/`await`），想學習如何設計「比作業大、比商業產品小」的系統。
- 修習系統分析與設計、資料結構、資訊檢索或軟體工程課程，想看一個從頭到尾完整的例子。

## 學習目標

讀完之後，你應該能夠：

1. 把一份文字需求拆成可以驗證的功能需求、非功能需求、限制與假設，並找出**真正決定架構的難點**。
2. 用分層架構與依賴注入，把「容易測試」設計進系統裡，而不是事後補救。
3. 理解倒排索引（inverted index）、BM25 排序、不可變片段（immutable segment）與原子提交（atomic commit）這些搜尋引擎的核心技術。
4. 用參考解（reference implementation）、性質測試（property-based testing）與故障注入（fault injection）驗證一個複雜的系統。
5. 用「先量測、再優化」的方式處理效能問題。

## 章節

| 章 | 主題 | 重點 |
|---|---|---|
| [1](01-requirements.md) | 需求分析 | 需求分類、模糊需求的處理、可驗證的驗收標準 |
| [2](02-system-analysis.md) | 系統分析 | 系統脈絡、使用案例、領域模型、資料流、找出難點 |
| [3](03-architecture.md) | 架構設計 | 分層、依賴方向、介面注入、執行緒模型 |
| [4](04-text-processing.md) | 正規化與分詞 | Unicode NFKC、CJK 雙字詞元、座標還原 |
| [5](05-query-language.md) | 查詢語言 | Lexer、遞迴下降 Parser、Binder、錯誤位置 |
| [6](06-index-format.md) | 索引結構與檔案格式 | 倒排索引、varint、片段檔、CRC |
| [7](07-search-and-ranking.md) | 查詢執行與排序 | bitset 布林運算、片語比對、BM25、Top-K |
| [8](08-consistency.md) | 一致性與中斷安全 | 原子提交、快照隔離、參考計數、合併 |
| [9](09-incremental-indexing.md) | 爬檔與增量索引 | 編碼偵測、監看、防抖、改名偵測 |
| [10](10-ui-mvvm.md) | 使用者介面與 MVVM | 輸入即搜尋、取消、版本號、可測試的 ViewModel |
| [11](11-testing.md) | 測試策略 | TDD、參考解、性質測試、故障注入、壓力測試 |
| [12](12-performance.md) | 效能分析與調校 | 量測方法、最壞情境、平行化 |
| [13](13-process-and-lessons.md) | 開發流程與檢討 | 里程碑、CI、真實的錯誤與修正、習題總整理 |

## 建議的閱讀方式

- **第一次讀**：依序讀第 1 到 3 章，建立全貌；第 4 到 10 章挑有興趣的讀；最後讀第 11 到 13 章。
- **想學資訊檢索**：讀第 4、6、7 章。
- **想學軟體工程**：讀第 1、2、3、11、13 章。
- **想學系統程式與可靠性**：讀第 6、8、9、12 章。

每章最後都有「練習」，有些是思考題，有些需要修改程式並執行測試。

## 動手環境

```powershell
git clone https://github.com/hn83320589/LocalSeek
cd LocalSeek
dotnet restore LocalSeek.sln -p:Configuration=Release
dotnet build LocalSeek.sln -c Release --no-restore
dotnet test tests/LocalSeek.Tests -c Release --no-build
```

需要 Windows 與 .NET 9 SDK。測試約 10 秒跑完。

## 相關文件

- [README](../../README.md)：功能、建置、格式與演算法摘要。
- [architecture.md](../architecture.md)：開發前寫的系統分析與架構設計（第 1 到 3 章會引用）。
- [test-report.md](../test-report.md)：26 項驗收標準的測試報告。
- [perf-results.md](../perf-results.md)：效能量測結果。
- [samples/expected/README.md](../../samples/expected/README.md)：16 個查詢的逐步手算。
