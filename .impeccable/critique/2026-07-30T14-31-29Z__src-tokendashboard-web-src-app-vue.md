---
target: 儀錶板內容
total_score: 28
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 3
timestamp: 2026-07-30T14-31-29Z
slug: src-tokendashboard-web-src-app-vue
---
Method: dual-agent (A: /root/design_review · B: /root/evidence_review)

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|---|---:|---|
| 1 | 系統狀態可見性 | 4 | KPI、篩選快照與同步狀態能建立目前狀態 |
| 2 | 符合真實世界 | 3 | Token、成本、快取用語貼近任務，但 treemap 缺少比例與尺度說明 |
| 3 | 使用者控制與自由 | 3 | 可調日期、篩選與 Inspector，但資料管理操作與主流程並列 |
| 4 | 一致性與標準 | 3 | 結構一致，但 Dashboard、Session ledger、Detail、Capabilities 等中英混雜 |
| 5 | 錯誤預防 | 3 | 未知價格維持未知，仍應把匯出與刪除的影響範圍更明確隔離 |
| 6 | 辨識勝於記憶 | 2 | Token type、Adapter、Mode 與快取覆蓋率需要使用者自行理解 |
| 7 | 彈性與效率 | 3 | 預設日期、快速鍵與多重篩選有效率，但缺少有效篩選摘要與一鍵清除 |
| 8 | 美感與極簡設計 | 2 | 左側控制、中央證據與右側 Inspector 在初次使用時同時競爭注意力 |
| 9 | 錯誤辨識與復原 | 3 | 有重試與部分結果訊息，但同步細節藏在 operation message |
| 10 | 說明與文件 | 2 | 缺少如何讀比較矩陣、cache coverage 與未知成本的就地說明 |
| **Total** | | **28/40** | **良好基線，主要瓶頸是理解成本而非視覺一致性** |

## Design Specificity Verdict

**LLM assessment**：這是為 Token Dashboard 寫的工作介面，而不是可套用到任意 SaaS 的卡片頁。左側控制軌、中央比較證據、右側 Inspector，以及 session → turn → event 的追溯鏈，明確支持「比較不同工具與模型消耗效率」的目標。Blueprint Margin 的三欄工作台概念也有落實。

**Deterministic scan**：`detect.mjs --json src/TokenDashboard.Web/src/App.vue` 成功完成，結果為 `[]`，共 0 筆 finding。這代表機械規則沒有偵測到違反項目；它無法判定 treemap 是否可理解、術語是否足夠清楚，亦不能替代實際渲染驗證。

**Visual overlays**：未能建立可靠的 user-visible overlay。in-app Browser 回報 `Browser is not available: iab`，因此無法建立新分頁、進行可變腳本預檢、注入 `detect.js` 或讀取 console。沒有宣稱 overlay 已顯示。

## Overall Impression

這是一個有明確觀點的分析工作台：它把資料來源、總覽與原始證據放在同一個可追查的平面上。最大機會不在於增加更多圖表，而是讓第一次進入的人知道「先看什麼、這些數字能回答什麼、資料不完整時該如何解讀」。

## What's Working

- 三欄流向自然：控制軌設定比較範圍，中央呈現摘要與比較，右側承接選取後的證據與細節，不會把 session 調查切離主畫面
- 對未知價格採取保守表達，沒有把不確定資料偽裝成精確成本，符合產品的資料可信度承諾
- 已實作桌面到窄螢幕的版面折疊，底層 layout 並非只為單一螢幕尺寸設計

## Priority Issues

- **[P1] Treemap 缺少可讀的比較語意**
  - **Why it matters**：使用者看見面積後仍不知道比較的是 token、成本還是快取，也無法以精確值比較接近的區塊
  - **Fix**：提供 Token／成本切換、圖例、單位與總額；同步提供可排序的表格檢視，作為視覺圖的精確替代
  - **Suggested command**：`$impeccable clarify`

- **[P1] 初次進入的認知負荷過高**
  - **Why it matters**：日期 preset、至少四種篩選、來源同步、價格未知、KPI、趨勢區間、熱力圖、treemap、session ledger 與 Inspector 同時出現，讓使用者需要先學會介面才能比較效率
  - **Fix**：把同步、匯出與刪除收進「資料管理」；保留主要篩選在控制軌，增加目前有效篩選摘要與「清除全部」，把低頻設定放到進階區
  - **Suggested command**：`$impeccable distill`

- **[P1] 比較指標的解讀缺少就地引導**
  - **Why it matters**：Token type、Adapter、cache coverage 與未知成本彼此相關，使用者容易把 coverage 或 EST. COST 讀成完整、可比較的成本結論
  - **Fix**：在 KPI 與圖表標題旁加入短而具體的說明，例如「成本僅計入已知定價事件」與「快取覆蓋率以回報 cache 的來源為分母」；首次出現時以簡短提示說明推薦閱讀順序
  - **Suggested command**：`$impeccable clarify`

- **[P2] 語言與術語沒有收斂**
  - **Why it matters**：Dashboard、Session ledger、Detail、Capabilities 與中文標籤混用，會提高第一次使用時的轉譯成本，也使產品語氣不夠精確
  - **Fix**：決定中文優先或雙語術語表策略；若保留英文術語，首次出現附中文定義，後續維持同一命名
  - **Suggested command**：`$impeccable clarify`

- **[P2] 右側 Inspector 的選取上下文不夠強**
  - **Why it matters**：切換 Stats、Detail、Capabilities 時，很容易忘記目前檢查的是哪一個 session 或 turn；價格覆寫表單直接位於分析脈絡中，也干擾比較任務
  - **Fix**：將選取中的 session／turn 識別與篩選條件固定在 Inspector 頂部；把定價治理改為明確次要流程或分區
  - **Suggested command**：`$impeccable layout`

## Persona Red Flags

**Alex（重度使用者）**：要比較模型效率時，中央 KPI 主要是總量，尚未在同一趨勢比較 input／output／cache 的構成。他可能得反覆切換篩選與 Inspector 才能回答「是哪一種 token 導致成本上升」，效率受到限制。

**Jordan（第一次使用者）**：進入後必須同時理解日期 preset、Adapter、Token type、cache coverage、未知價格與 session ledger。沒有「推薦先做哪一步」的提示，他很可能只看總 token 而錯過來源或價格資料不完整的前提。

**Morgan（資料敏感的本機使用者）**：來源掃描與 session 證據讓人擔心私有工作紀錄是否會被顯示或匯出。目前缺少足夠醒目的資料範圍、脫敏及匯出內容提示，尤其在資料管理區更需要預先說明。

## Minor Observations

- 日期互動會影響全文搜尋條件時，應有可見的條件 chip 與明確撤銷方式
- SVG 趨勢圖若只有 `title` 與 `aria-label`，仍缺少軸、數值標記或表格替代，難以完成精確比較
- 小螢幕折疊後需特別驗證 Inspector 的選取摘要與匯出／刪除操作不會埋在長頁面末端

## Questions to Consider

- 如果這個畫面只能回答一個問題，會是「哪個模型最花錢」，還是「哪個工作流程最沒有效率」？目前畫面同時服務兩者，是否需要先選定主要比較維度？
- 當成本或快取資料不完整時，最有用的下一步是提醒使用者補資料、排除該來源，還是改以 token 組成比較？
- 定價治理是否真的必須與每日比較共處一頁，還是應該讓它在需要時才出現？
