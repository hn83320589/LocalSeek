# 第 6 章　索引結構與檔案格式

## 學習目標

- 理解倒排索引的結構，以及 postings、位置、文件頻率的意義。
- 用差值編碼（delta encoding）與 varint 壓縮整數序列。
- 設計一個可隨機存取、可驗證完整性的二進位檔案格式。
- 理解「不可變片段」的好處，以及它與墓碑、manifest 的關係。

相關程式：`src/LocalSeek.Core/Index/Segments/`（`SegmentFormat.cs`、`SegmentWriter.cs`、`SegmentReader.cs`、`PostingsCodec.cs`、`DeletionFile.cs`）、`src/LocalSeek.Core/Index/Manifest.cs`、`src/LocalSeek.Core/Storage/ByteBuffer.cs`

---

## 6.1 倒排索引

假設有三份文件：

| 文件 | 內文 | 詞元（位置） |
|---|---|---|
| 0 | 資料分析 | 資料(0) 料分(1) 分析(2) |
| 1 | 分析資料 | 分析(0) 析資(1) 資料(2) |
| 2 | 資料資料 | 資料(0) 料資(1) 資料(2) |

**正排**（forward）是「文件 → 詞」，也就是上表。搜尋時我們需要反過來，「詞 → 文件」，這就是**倒排索引**（inverted index）：

| 詞 | postings（文件, tf, 位置） | df |
|---|---|---|
| 分析 | (0, 1, [2]) (1, 1, [0]) | 2 |
| 料分 | (0, 1, [1]) | 1 |
| 料資 | (2, 1, [1]) | 1 |
| 析資 | (1, 1, [1]) | 1 |
| 資料 | (0, 1, [0]) (1, 1, [2]) (2, 2, [0, 2]) | 3 |

- **tf**（term frequency）：詞在該文件出現幾次。
- **df**（document frequency）：有幾份文件含有這個詞，就是 postings 的長度。
- **位置**：片語查詢用。查詢 `"資料 分析"` 要找「資料在 p、分析在 p+1」的文件，只有文件 0 符合。

查詢 `資料 AND 分析`，只要讀兩條 postings 取交集（文件 0、1），完全不用看其他文件。這就是全文檢索快的原因。

---

## 6.2 壓縮整數：差值編碼與 varint

postings 裡全是整數，而且數量龐大：5 萬份文件、每份數千個詞元，位置總數以億計。用 4 bytes 存每個整數太浪費。

### 差值編碼

postings 中的文件編號是遞增的，所以只存「與前一個的差」：

```
文件編號：3, 7, 8, 120
差值：    3, 4, 1, 112
```

本專案還多減 1（因為嚴格遞增，差至少是 1，減 1 後最小是 0）：

```csharp
// src/LocalSeek.Core/Index/Segments/PostingsCodec.cs（節錄）
public void Add(int id, int frequency, ReadOnlySpan<int> positions)
{
    if (id <= _lastId)
    {
        throw new InvalidOperationException($"Posting ids must increase ({_lastId} then {id}).");
    }

    _postings.WriteVarInt32(id - _lastId - 1);   // _lastId 從 -1 開始
    _postings.WriteVarInt32(frequency);
    var last = 0;
    foreach (var p in positions)
    {
        _positions.WriteVarInt32(p - last);       // 位置也做差值
        last = p;
    }

    _lastId = id;
    Count++;
}
```

注意第一行的檢查：編號不是遞增時直接丟例外。這是**不變式**（invariant）檢查，寫入端一旦出錯，立刻失敗，而不是寫出一個讀不回來的檔案。

### Varint（LEB128）

差值通常很小，用 varint 編碼：每個 byte 用 7 bits 存資料，最高位元表示「後面還有」。

| 數值 | 二進位 | 編碼 | bytes |
|---|---|---|---|
| 5 | `101` | `05` | 1 |
| 127 | `111 1111` | `7F` | 1 |
| 300 | `10 0101100` | `AC 02` | 2 |

300 的低 7 位是 `0101100`（0x2C），加上「後面還有」的旗標變成 0xAC；剩下的 `10`（0x02）放第二個 byte。小於 128 的數只要 1 byte，而差值絕大多數都小於 128。

---

## 6.3 四種詞項空間

每份文件有兩個欄位（檔名、內文），而 CJK 單字又要另外計算詞頻，所以索引中有四種詞項空間：

```csharp
// src/LocalSeek.Core/Index/IIndexReader.cs
public enum TermSpace : byte
{
    BodyTerm,   // 內文詞元（Latin、CJK 雙字），有位置
    NameTerm,   // 檔名詞元，有位置；計分時 ×2
    BodyChar,   // 內文的 CJK 單字詞頻，沒有位置
    NameChar,   // 檔名的 CJK 單字詞頻，沒有位置；計分時 ×2
}
```

詞典的鍵（key）是「1 byte 的空間編號 + 詞的 UTF-8」，以 byte 序排序：

```csharp
public static byte[] Key(TermSpace space, string term)
{
    var key = new byte[1 + Encoding.UTF8.GetByteCount(term)];
    key[0] = (byte)space;
    Encoding.UTF8.GetBytes(term, key.AsSpan(1));
    return key;
}
```

把空間放在最前面，同一空間的詞在排序後會連在一起，前綴查詢（例如 BodyTerm 中所有以 `da` 開頭的詞）就是一段連續範圍。

**單字詞元不存 postings**。查詢單一漢字時，規格規定用的是「單字詞頻」（BodyChar、NameChar），而片語中不允許單字詞元。所以單字詞元的 postings 永遠不會被讀，只需要計入文件長度。這是一個從規格推導出來的空間優化。

---

## 6.4 片段檔格式

一個片段是一個不可變的檔案，包含一批文件的完整索引：

```
Header(32)  magic "LSEKSEG\0" | version u16 | reserved | segmentId i64 | reserved
Contents    count, [bodyLength varint, contentHash u64]...
Docs        count, [path, size, lastWriteTicks, nameLength, contentSegment, contentId]...
Postings    每個詞：[doc 差值, freq]*，接著該詞所有 posting 的 [位置差值]*
TermDict    每 64 個詞一個 block：[共用前綴長度, 後綴長度, 後綴, docFreq, postingsLen, positionsLen]...
TermIndex   count, [每個 block 的第一個詞, block 的 offset]...
Footer      5 個 section 的 [offset, length, crc32] | headerCrc | footerCrc | magic "LSEKEND\0"
```

（完整定義在 `SegmentFormat.cs` 的註解中。）

### 讀取的順序：從尾巴開始

開啟片段時，先讀**最後**固定大小的 Footer，從中得知每個 section 的位置與 CRC。Footer 放在最後，是因為寫入時要等所有 section 寫完，才知道它們的位置與長度。這讓片段可以**串流寫入**，不必先把整個檔案放在記憶體中。

### 查詢一個詞：兩層索引

```
TermIndex（開啟時整個載入記憶體）
  block 0: 第一個詞 = "ai"      → offset 0
  block 1: 第一個詞 = "data"    → offset 812
  block 2: 第一個詞 = "模型"    → offset 1650
        │  二分搜尋：找最後一個「第一個詞 ≤ 目標」的 block
        ▼
TermDict 的一個 block（從磁碟讀出，最多 64 個詞）
        │  依序解碼，比對到目標詞
        ▼
得到 postings 的 offset 與長度 → 讀 Postings section
```

```csharp
// src/LocalSeek.Core/Index/Segments/SegmentReader.cs（節錄）
public bool TryGetTerm(ReadOnlySpan<byte> key, out TermEntry entry)
{
    entry = default;
    var block = BlockFor(key);                 // 在記憶體中的 TermIndex 二分搜尋
    if (block < 0)
    {
        return false;
    }

    foreach (var candidate in DecodeBlock(block))
    {
        var order = candidate.Key.AsSpan().SequenceCompareTo(key);
        if (order == 0)
        {
            entry = candidate;
            return true;
        }

        if (order > 0)
        {
            break;                              // 已經超過，詞不存在
        }
    }

    return false;
}
```

這是**稀疏索引**（sparse index）的典型設計：記憶體中只放每 64 個詞中的一個，查詢時最多讀一個 block。詞典再大，記憶體用量也只有詞數的 1/64。

### Front coding

排序後相鄰的詞常有共同前綴，例如 `資料`、`資料庫`、`資料夾`。Block 中每個詞只存「與前一個詞共用幾個 byte」加上「不同的部分」，詞典可以小很多。這也是每個 block 必須從頭解碼的原因：要還原一個詞，需要前一個詞。

---

## 6.5 完整性：CRC32 與 magic

每個 section 都有 CRC32 校驗碼，檔頭與檔尾有 magic 字串（`LSEKSEG\0`、`LSEKEND\0`）。它們能偵測：

| 問題 | 如何偵測 |
|---|---|
| 檔案被截斷（寫到一半當機） | 檔尾 magic 不對，或 Footer CRC 不符 |
| 磁碟位元錯誤 | section CRC 不符 |
| 讀到別種檔案 | 檔頭 magic 不對 |
| 舊版程式寫的格式 | 版本號不符 |

### 開啟速度與完整驗證的取捨

開啟索引的限制是 2 秒，但完整驗證 700 MB 的 CRC 需要讀完整個檔案。折衷做法：

- **開啟時**：只驗證 Header、Footer、Contents、Docs、TermIndex 的 CRC（這些是開啟時本來就要讀的）。
- **需要時**：`IndexWriter.VerifySegments()` 讀完 TermDict 與 Postings，做完整驗證。

這是一個寫進 README「設計取捨」的決定：開啟只需約 150 到 200 ms，代價是大區段的損毀要等到完整驗證才會發現。

---

## 6.6 墓碑檔與 manifest

片段一旦寫好就**永不修改**。那刪除怎麼辦？

- **墓碑檔**（`seg_00000003_000002.lsd`）：一個 bitset，標記片段中哪些文件已刪除。每次刪除產生新的一代（檔名最後的 `_000002`、`_000003`），舊的不修改。格式是 `magic "LSEKDEL\0" | version | segmentId | docCount | bitset | crc32`。
- **Manifest**（`manifest_0000000007.lsm`）：一個提交點，列出這次提交包含哪些片段、各自用第幾代墓碑。內容是 JSON，後面接長度、CRC32 與 magic `LSEKMAN\0`。

```
manifest_0000000007.lsm
├─ seg_00000001.lss + seg_00000001_000003.lsd
├─ seg_00000004.lss
└─ seg_00000005.lss + seg_00000005_000001.lsd
```

讀者只要拿到一個 manifest，就能確定要讀哪些檔案、哪些文件已刪除。第 8 章會說明 manifest 如何讓提交成為原子操作。

### 為什麼要不可變？

| 好處 | 說明 |
|---|---|
| 並行讀取不需要鎖 | 檔案永遠不變，任意多個讀者同時讀都安全 |
| 中斷安全容易證明 | 寫到一半當機，最多留下一個沒被任何 manifest 引用的孤兒檔，刪掉即可 |
| 快照隔離是免費的 | 舊讀者繼續讀舊檔案，新讀者讀新 manifest |
| 作業系統快取友善 | 不會有「快取中的資料過時」的問題 |

代價是刪除與更新都只能「加墓碑、寫新片段」，片段會越來越多、越來越碎，所以需要背景**合併**（第 8 章）。這與 Lucene、LevelDB 等 LSM 類系統的思路相同。

---

## 6.7 文件與內容在格式中的實現

第 2 章提到「文件與內容分離」。在格式中：

```csharp
// src/LocalSeek.Core/Index/Segments/SegmentFormat.cs
public readonly record struct ContentEntry(int BodyLength, ulong ContentHash);

public readonly record struct DocEntry(string Path, long Size, long LastWriteTicks, int NameLength,
                                       long ContentSegment, int ContentId);
```

- 內文的 postings（BodyTerm、BodyChar）以 **ContentId** 為編號。
- 檔名的 postings（NameTerm、NameChar）以 **DocId** 為編號。
- `DocEntry.ContentSegment` 可以指向**另一個片段**。

改名時新寫的 Doc，`ContentSegment` 指向舊片段，`ContentId` 指向舊的內容。讀取時，`IndexSnapshot` 為每個片段建立兩張對照表（ContentId → 擁有它的文件、DocId → 文件），把兩種編號都轉成同一個全域文件序號。

### 設計演變

開發前的設計把 Postings 與 Positions 分成兩個 section。實作時改成同一個 section，每個詞的 postings 後面緊接它的位置。理由是**合併時可以串流寫入**：合併器一次只需要處理一個詞，不必把整個片段的位置暫存起來。AND 查詢仍然只讀 postings 那一段，不受影響。這類「實作後的差異」記錄在 [architecture.md](../architecture.md) 第 12 節。

---

## 練習

1. 手動寫出 6.1 節三份文件中，詞「資料」的 postings 編碼後的位元組（差值減 1、varint；位置也做差值）。
2. 寫出 1000、16384 的 varint 編碼。varint 最多需要幾個 byte 才能表示一個 `int`？
3. 如果 TermDict 每個 block 改成 16 個詞，記憶體用量與查詢速度會怎麼變？改成 1024 個詞呢？
4. 6.5 節說開啟時不驗證 Postings 的 CRC。如果 Postings 區段有一個位元錯誤，查詢時可能發生什麼事？`VerifySegments` 發現後系統會怎麼處理？（提示：第 8 章的損毀處理。）
5. 打開一個實際的索引目錄（在 `%LocalAppData%\LocalSeek\index\`，或用環境變數 `LOCALSEEK_DATA_DIR` 指定），列出檔案，並用十六進位檢視器確認片段檔的頭尾 magic。
