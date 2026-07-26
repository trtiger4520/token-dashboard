# Token Dashboard 架構基線

## 已鎖定的執行模型

Token Dashboard 使用單一 .NET executable 作為唯一啟動程序

該 executable 啟動 loopback Kestrel，提供 Minimal API，並提供已建置的 SPA 靜態檔案

啟動流程由同一 executable 啟動 loopback listener，確認 listener 可用後開啟使用者預設瀏覽器指向該 loopback 位址

資料持久化只使用單一 SQLite DB，所有核心資料與後續 ingestion、storage 功能都以此 DB 為唯一資料來源

## 邊界

- `TokenDashboard.Core` 只放跨功能共享的資料契約、不變條件與純計算規則
- Minimal API 是後端 HTTP 邊界
- 已建置 SPA 是瀏覽器端 UI 邊界
- loopback Kestrel 是本機傳輸邊界
- 單一 SQLite DB 是持久化邊界

## 核心契約規則

- 所有 UTC 欄位必須以 UTC `DateTimeOffset` 表示
- 來源時區以每筆資料的 `SourceTimeZone` 保留
- Token 類型使用可擴充值，不以封閉列舉限制
- `PriceVersion` 以 USD 與半開有效區間計算
- 找不到有效價格時保留未知狀態，不推估價格
- `EventFingerprint` 由穩定輸入以決定性 SHA-256 計算，重複掃描使用相同輸入會得到相同指紋
- Session 結束時間由最後活動時間加上 30 分鐘 inactivity timeout 推導
- 可擴充欄位 `WorkspaceId` 與 `OwnerId` 保留在共享契約範圍

## 啟動與交付順序

1. executable 啟動 loopback Kestrel
2. Minimal API 掛載已建置 SPA 與 API endpoints
3. executable 開啟 loopback 瀏覽器入口
4. API 與後續功能透過單一 SQLite DB 讀寫

本文件不保留替代 hosting、替代資料庫或多程序部署選項

## 來源格式驗證風險

四個 adapter 對應 Claude Code App、Claude Code CLI、Codex App 與 Codex CLI

真實供應商格式尚待使用者提供脫敏樣本驗證，樣本需要涵蓋檔案路徑、格式版本、事件識別、UTC 與來源時區、Session/Turn、subagent、tool、workflow 與完整對話欄位

在樣本驗證完成前，不得把推測格式當成相容性證據，也不得以 tests/fixtures/private 的真實或合成資料替代使用者提供的脫敏樣本

## localhost session key 安全邊界

- Kestrel 只繫結 loopback 位址，不監聽公開網卡或 `0.0.0.0`
- 每次啟動產生至少 256-bit 的密碼學安全隨機 localhost session key
- 開啟瀏覽器時只透過 URL fragment 傳入 key，例如 `#key=...`
- SPA 讀取 fragment 內的 key 後立即呼叫 `history.replaceState` 移除 fragment
- key 只保存在瀏覽器 `sessionStorage`，不持久化到檔案、資料庫或其他跨 session 儲存
- 所有敏感 API 都必須使用 `X-Token-Dashboard-Key` header 傳送 key
- CORS 只允許實際使用的 loopback origin、必要 HTTP methods 與 `X-Token-Dashboard-Key` header
- 不使用 cookie，不使用 query string 傳送 key
- key 不寫入 log、匯出檔或頁面內容
- 未持有有效 session key 的請求拒絕存取 API 與匯入、刪除、匯出功能
- session key 只保護本機應用程式邊界，不宣稱提供雲端同步、登入或多人權限

## FTS 與匯出備份方向

SQLite FTS 使用單一 SQLite DB 內的可重建全文索引，索引來源是 canonical Source、Session、Turn、Content、SubEvent 與相關對話資料

FTS 索引遺失時可由 canonical 資料重建，不另設搜尋服務或第二個資料庫

CSV 與 JSON 匯出提供分析與交換用途，SQLite 匯出提供完整資料庫備份用途

匯出必須從一致性的 SQLite snapshot 產生，並保留 UTC、來源時區、價格有效區間、未知價格與事件指紋語意

匯出是使用者主動觸發的本機操作，不上傳雲端，不改變單一 SQLite DB 的資料來源原則

## Published SPA delivery

API executable serves the built Vue distribution from `wwwroot` through default files, static files, and a fallback to `index.html`. The API project conditionally copies `src/TokenDashboard.Web/dist` into output and publish directories, so a clean API build remains valid when the distribution is absent while the integration build creates it first

The service uses an IPv4 loopback dynamic port at `127.0.0.1`. This is the deliberate cross-platform binding strategy for a single published process and avoids trying to coordinate one random port across separate IPv4 and IPv6 listeners
