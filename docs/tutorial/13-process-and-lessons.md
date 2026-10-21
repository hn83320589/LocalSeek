# 第 13 章　開發流程與檢討

## 學習目標

- 把一個大系統切成「每一步都能編譯、能測試、能提交」的里程碑。
- 用 commit message 記錄「為什麼」，讓歷史可以被理解。
- 認識 CI 的價值：它抓到了本機永遠不會出現的問題。
- 學會回應外部審查：接受、驗證、提出證據，必要時指出不正確之處。
- 整理本專案的教訓，作為下一個專案的檢查清單。

---

## 13.1 先分析、再討論、後開發

本專案的第一個 commit 不是程式碼，而是設計文件：

```
8c418d0 docs: add system analysis and architecture design draft
```

[architecture.md](../architecture.md) 包含需求分析、難點、專案切分、資料格式、測試策略、效能策略與里程碑，並標出 6 個需要決定的地方（決策 A 到 F）。文件經過討論、確認之後才開始寫程式。

這一步花的時間，在後面省了很多：

- 「文件與內容分離」在設計時就決定了（第 2 章）。如果寫完索引格式才發現改名需要它，就得重寫格式與合併。
- 「Parse 與 Bind 分開」也是設計時決定的（第 5 章）。否則參考解無法獨立，驗收標準就做不到。
- 套件清單在開始前就取得同意，避免中途引入新的依賴。

---

## 13.2 里程碑：每一步都可以交付

| 里程碑 | commit | 內容 | 對應驗收 |
|---|---|---|---|
| M0 | `e0bfed6` | Solution 骨架、Uno App、CI | 26 |
| M1 | `af9e333` | 正規化與分詞、座標還原 | 1、2 |
| M2 | `890c166` | Lexer、Parser、Binder、篩選 | 4、8 |
| M3 | `56f98b8` | 記憶體索引、執行器、BM25、**參考解** | 5、6、7、9 |
| M4 | `b8533b1` | 片段格式、manifest、快照、復原 | 14、15、19 |
| M5 | `0ecdbf0` | 背景合併與並行變更的帶入 | 10、16、17 |
| M6 | `831b01f` | 編碼偵測與抽取器 | 3 |
| M7 | `d2cde9e` | glob、設定、爬檔、診斷 | 13、21 |
| M8 | `359a2d6` | 增量索引引擎、監看、背景服務 | 11、12 |
| M9 | `4c0cec3` | 摘要與高亮、預覽 | 18 |
| M10 | `359a746` | ViewModels | 20 |
| M11 | `cd64628` | Uno UI、診斷頁、狀態列 | 23 |
| M12 | `193ee68` | 語料產生器、效能量測與調校 | 22 |
| M13 | `15b7b9f`、`77a7891` | 範例語料與手算、README、截圖 | 25 |

排序的原則：

- **由內而外**：先做沒有依賴的 Core（分詞、查詢），再做依賴它的 Indexing、ViewModels、UI。
- **先做驗證工具**：M3 就寫了參考解。之後每個里程碑的正確性都有標準可以比對。
- **先建 CI**：M0 就讓 CI 跑起來，因為「Uno 在 CI 上能否建置」是風險表上的高風險項目。
- **效能放在功能完成之後**：M12 才做效能，因為要量的是完整的系統；但效能需要的架構（平行抽取、快速路徑）在設計時就考慮了。

每個 commit 都必須能編譯、所有測試通過、包含新功能的測試。這讓任何一個 commit 都可以被檢出、執行、檢查。

---

## 13.3 Commit message 寫「為什麼」

看看幾個 commit message：

```
fix: skip GBK files that also happen to be well-formed Big5

Simplified Chinese in GBK is often structurally valid Big5, so such
files were indexed as mojibake (e.g. 涴岆珨爺潠极笢恅腔惆豢) and never
showed up as skipped. Real Traditional text in Big5 is almost all
level-1 common ideographs; GBK read as Big5 is about half rare ones.

Measured on this repo's Traditional documents cut into samples: with
an 85% threshold no Traditional sample was rejected ...
```

```
build: commit the Uno app project that .gitignore had excluded

The template .gitignore excludes *.app (macOS bundles) and *.pubxml.
Windows git matches case-insensitively, so the whole src/LocalSeek.App
folder and its publish profiles were never committed; local builds
worked from the working tree and only the clean CI checkout failed ...
```

標題說「做了什麼」，內文說**為什麼**：問題是什麼、怎麼發現的、為什麼選這個解法。程式碼的 diff 本身就能說明「改了什麼」，但「為什麼」只能靠文字留下來。半年後有人看到 `MinCommonRatio = 0.85` 想改成 0.9，`git blame` 就能帶他找到當初的實驗數據。

---

## 13.4 CI 抓到的問題

功能全部完成、本機 464 個測試全過之後，第一次 push 到 GitHub。CI 的經過：

### 問題 1：Release 建置失敗（push 前就發現）

在本機從乾淨的目錄**照 CI 的步驟**重跑一次時發現：Uno.Sdk 在 Debug restore 時會加入 DevServer 套件，而這個套件不允許最佳化建置（UNOB0019）。CI 先不指定組態 restore，再用 `--no-restore` 建置 Release，必定失敗。修正：restore 時就指定 `-p:Configuration=Release`。

### 問題 2：整個 App 專案沒有進 git

第一次 CI 失敗：找不到 `LocalSeek.App.csproj`。

原因是範本的 `.gitignore` 有一條 `*.app`（用來排除 macOS 的應用程式包）。Windows 上的 git **不分大小寫**，`LocalSeek.App` 資料夾就這樣被排除了。另一條 `*.pubxml` 也把發佈設定檔排除了。

本機建置讀的是工作目錄，檔案都在，所以從來沒有發現。只有 CI 從乾淨的 clone 建置時才會出錯。修正後，用一個全新的 clone 完整重跑 CI 流程確認。

早期的 M0 與 M11 commit 因此缺少 App 的檔案。這裡選擇**不改寫歷史**，而是補一個 commit 並在訊息中說明，因為歷史已經 push 出去了。

### 問題 3：取消延遲測試在 CI 上失敗

本機穩定通過的測試，在 CI 上失敗。第 10 章詳細說明了這個測試的兩次重新設計。

### 教訓

- **「在我的電腦上可以」不算數**。CI 從乾淨的環境開始，能抓到工作目錄中殘留的檔案、`.gitignore` 的問題、時間相依的測試。
- **最早建 CI**。如果 M0 的 CI 真的從乾淨的 clone 建置過 App，問題 2 在第一天就會發現。這裡的教訓是：CI 必須真的建置**所有**交付的專案。

---

## 13.5 回應審查

專案完成後，有一份外部審查意見。處理方式值得參考：

| 審查意見 | 處理 | 方法 |
|---|---|---|
| 簡體 GBK 被誤判為 Big5 | **接受並修正** | 先寫重現測試，用實驗決定門檻（第 9 章） |
| 缺少測試報告與 CI 證據 | **接受** | 補上測試報告、CI 連結與 trx 統計 |
| 效能查詢集沒有涵蓋最高頻詞 | **接受**，並發現更嚴重的問題 | 加入最壞情境查詢，發現餘裕不足，再優化（第 12 章） |
| 路徑寫死 Windows 分隔符 | **接受為已知限制** | 題目只針對 Windows，在 README 註明，不改程式 |
| 保留兩個提交會暫時多留舊片段 | **維持** | 這是刻意的取捨，README 已說明 |
| 沒有測試證明片段數以對數成長 | **指出不正確** | 附上 `IndexProperties.Merging_preserves_results_and_keeps_segment_count_logarithmic` 的位置與它斷言的內容 |

幾個原則：

- **每一項都驗證**，不是全盤接受，也不是全盤反駁。最後一項審查意見與事實不符，就要拿出證據說明。
- **審查的價值可能超出它的字面**。「加入最高頻詞查詢」本來只是要讓結論更可靠，實際量測後卻發現最壞情境只剩 2 ms 的餘裕，引出了一次真正的優化。
- **架構層面的改動先取得同意**。平行化會改變查詢的執行緒模型，所以先提出方案與理由，確認後才實作。

---

## 13.6 教訓清單

把本專案學到的事整理成清單，可以在下一個專案開始前逐條檢查：

**需求與分析**
- [ ] 每個形容詞都轉成可量測的指標了嗎？
- [ ] 找出會決定架構的難點了嗎？有沒有看似小、卻會改變資料模型的需求？
- [ ] 不可能或模糊的需求，提出精確的替代定義並取得同意了嗎？
- [ ] 除了需求文件，使用者的實際情境還可能帶來什麼問題？（例如簡繁混合的檔案）

**架構**
- [ ] 時間、檔案系統、排程、儲存裝置都在介面後面嗎？
- [ ] 依賴規則有測試強制嗎？
- [ ] 錯誤在「知道如何復原」的那一層處理嗎？

**測試**
- [ ] 每一項驗收標準都對應到測試了嗎？
- [ ] 複雜演算法有獨立的參考解嗎？
- [ ] 隨機測試可以重現嗎？有檢查涵蓋量，避免空洞地通過嗎？
- [ ] 新寫的測試，有先看到它失敗嗎？
- [ ] 有沒有測試依賴「睡一下應該就好了」？

**效能**
- [ ] 量測環境、語料、統計方式都寫清楚了嗎？
- [ ] 查詢集包含最壞情境嗎？
- [ ] 量了不只一次嗎？報告的是「符合」還是「勉強符合」？
- [ ] 優化之前，先量出時間花在哪裡了嗎？

**流程**
- [ ] CI 從乾淨的環境建置**所有**交付物嗎？
- [ ] commit message 寫了「為什麼」嗎？
- [ ] 報告中有沒有寫進自己沒驗證過的因果解釋？

---

## 13.7 延伸專題

以下題目可以作為課程專題或自主練習，難度由低到高：

1. **搜尋建議**：輸入時列出以目前字串開頭的詞（提示：`GetTermsWithPrefix`）。
2. **檔名高亮**：結果清單中，把檔名的命中位置也高亮。需要對檔名做第 4 章的座標還原。
3. **跨平台**：讓 Core 與 Indexing 在 Linux 上通過測試。先用搜尋找出所有寫死 `'\\'` 的地方（例如 `Crawler.cs`、`IndexingEngine.cs`、`DiagnosticsLog.cs`），改用 `Path` 相關的 API，並在 CI 加一個 Linux job。
4. **Skip list**：在 postings 中每 N 筆加一個跳躍點，讓 AND 查詢在一個詞很常見、一個詞很少見時，不必讀完常見詞的整條 postings。量測改進前後的差異。
5. **雙詞片語索引**：為最常見的詞對預先建立索引，讓第 12 章的最壞情境片語查詢不必解碼位置。分析它對索引大小與建立時間的影響。
6. **doc-at-a-time**：把執行器改成 doc-at-a-time，用 WAND 等演算法在取前 K 筆時提早結束。在 100 萬份文件上與目前的實作比較。

做任何一個專題，都請照本專案的流程：先寫分析與設計、列出取捨、寫測試、量測、寫下為什麼。
