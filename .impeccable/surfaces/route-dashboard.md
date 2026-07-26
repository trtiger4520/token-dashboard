---
version: 1
slug: "route-dashboard"
primary_target: "route:/"
related_targets: []
---

# Dashboard Operate

## Scope and visitor mode

這是 Token Dashboard 根路由的 Dashboard surface，模式是 Operate

## Audience, job, action, proof, and constraints

- 主要使用者是個人開發者，資料模型兼容未來團隊使用
- 使用情境是本機跨平台追蹤 Claude Code App/CLI 與 Codex App/CLI
- 預設日期範圍是最近 30 天，並提供常用範圍與自訂日期
- surface 需支援 overview、日統計、月統計、日期熱力圖與模型／工具比較
- 使用者可從 Session 進入 Turn，再進入子事件時間軸查看完整對話、subagent、tool 與 workflow
- 使用者可進行全文搜尋、來源設定、標籤、匯出與刪除
- 資料正確性以 UTC、來源時區、USD 歷史價格有效區間、快取命中率與事件指紋去重語意為依據

## Chosen direction and memorable moment

DESIGN.md 是不可偏離的既有視覺真相，Dashboard 必須繼承 Stratum Docs 與 Blueprint Margin

介面以既有髮絲線、平面紙張層次、克制的資訊排列與圖紙邊界承載操作狀態，讓使用者能直接比較工具與模型的消耗效率，並辨識資料時間、來源、價格有效性與未知價格

## Unresolved decisions

none
