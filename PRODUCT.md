# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

主要使用者是個人開發者

資料模型兼容未來團隊使用，但 MVP 不包含多人登入、成員或權限功能

使用情境是於本機跨平台追蹤 Claude Code App、Claude Code CLI、Codex App 與 Codex CLI 的 token、成本、快取、Session、Turn、subagent、tool、workflow 與完整對話

## Product Purpose

Token Dashboard 用於比較工具與模型的消耗效率

核心成功是讓使用者能比較不同工具與模型的 token、成本、快取、Session、Turn、subagent、tool、workflow 與完整對話消耗

## MVP

- 四個來源 adapter：Claude Code App、Claude Code CLI、Codex App、Codex CLI
- 自動偵測來源與自訂來源路徑
- JSON 與 CSV 匯入
- 單一 SQLite DB 與 SQLite FTS 全文搜尋
- 日統計、月統計與日期熱力圖
- 模型與工具消耗比較
- 快取命中率
- USD 歷史定價與有效區間
- 標籤與資料刪除
- CSV、JSON 與 SQLite 匯出
- localhost session key 安全邊界
- 跨平台單一 .NET executable

非目標：雲端同步、登入、多人成員權限

## Capabilities and Constraints

- 平台是 web
- 後端基線是 .NET 10
- 後續架構使用單一 .NET executable、loopback Kestrel、Minimal API、已建置 SPA、瀏覽器啟動與單一 SQLite DB
- 核心契約需要保留來源時區與 UTC 時間
- Token 類型必須可擴充
- 價格使用 USD 與生效區間，未知價格不得推估
- 事件指紋需要穩定、決定性，並支援重複掃描去重
- 根目錄保留 pnpm workspace 基線供後續 Vue app 使用
- 來源格式必須以使用者提供的真實供應商脫敏樣本驗證，樣本尚待提供

## Brand Commitments

- 介面需繼承 DESIGN.md 的 Stratum Docs
- 介面需繼承 DESIGN.md 的 Blueprint Margin

## Evidence on Hand

- DESIGN.md 是目前已確認的視覺語言來源
- 本輪不建立或使用 tests/fixtures/private 下的資料
- 真實供應商格式尚未有使用者提供的脫敏樣本，四個 adapter 的格式相容性不得視為已驗證

## Product Principles

- 核心資料契約先保留可擴充性
- 缺少價格資料時保持未知，不以推估值取代事實
- 事件身份必須可在重複掃描中重現
- 時間資料同時保留 UTC 與來源時區語意
