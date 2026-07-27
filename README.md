# Token Dashboard

Token Dashboard 是本機跨平台的單一 .NET executable，提供 Claude Code App/CLI 與 Codex App/CLI 的 token、成本、快取、Session/Turn、工具、subagent、workflow 與完整對話追蹤

主要使用者是個人開發者，資料模型保留未來團隊相容欄位

## 需求

- .NET SDK 10.0.302
- Node.js 24.14.0
- pnpm 11.9.0

## 開發與建置

Windows PowerShell：

```powershell
pnpm --dir src/TokenDashboard.Web install --lockfile=false
pnpm --dir src/TokenDashboard.Web build
dotnet run --project src/TokenDashboard.Api/TokenDashboard.Api.csproj
```

macOS 或 Linux：

```bash
pnpm --dir src/TokenDashboard.Web install --lockfile=false
pnpm --dir src/TokenDashboard.Web build
dotnet run --project src/TokenDashboard.Api/TokenDashboard.Api.csproj
```

完整驗證可執行 `scripts/build.ps1` 或 `bash scripts/build.sh`

發布可執行 `scripts/publish.ps1` 或 `bash scripts/publish.sh`，預設產生 `win-x64`、`linux-x64`、`osx-x64` 的 framework-dependent 產物

framework-dependent 產物需要目標平台預先安裝相容的 .NET 10 runtime

使用 Docker Compose 時執行 `docker compose up --build`，直接開啟 `http://localhost:18080` 即可進入 Dashboard，Compose 會將 session key 放在 URL fragment 並由 SPA 讀取後移除；若有修改 `TOKEN_DASHBOARD_PORT`，請改用對應的 port

## 資料與啟動安全

預設 SQLite 檔案是目前工作目錄的 `token-dashboard.db`，可用 `TokenDashboard__ConnectionString` 指定資料路徑

服務只綁定 loopback 動態連接埠，啟動時產生至少 256-bit 的 session key，瀏覽器 URL 只用 fragment 傳遞 key，SPA 讀取後會移除 fragment 並放入 sessionStorage，敏感 API 使用 `X-Token-Dashboard-Key` header

key 不使用 cookie、不放 query string、不持久化、不寫入 log、匯出或頁面內容；只有 Development environment 且明確設定 `TokenDashboard__EmitStartupDiagnostics=true` 時才會輸出含 key 的啟動診斷，production 不輸出 key

## 匯入與匯出

來源 discovery 支援四個 adapter、三平台候選路徑與自訂路徑，匯入支援 JSON、JSONL、CSV，也支援瀏覽器把內容送到 `/api/sources/import`；內容只寫入暫存檔供解析，解析後刪除 raw snapshot，單次內容上限為 10 MiB

真實供應商格式仍需使用者提供脫敏樣本驗證，adapter 採 tolerant capability/fallback，不宣稱完全相容

CSV 匯出只含統計，不含 prompt 或 response；JSON 與 SQLite 匯出含完整對話內容，response 會附上敏感資料警示；SQLite 匯出使用一致性備份

## 私人 fixtures

`tests/fixtures/public` 僅放明確合成資料，可追蹤的 private fixture README 只說明目錄用途；`tests/fixtures/private` 下的真實或測試資料維持 gitignored，請勿加入真實或合成內容

## 價格來源與限制

內建 USD 價格只收錄有官方來源的規則，目前包含 OpenAI GPT-5.4 與 Anthropic Claude Sonnet 4，規則保留 provider、model、mode、有效時間與 input threshold，user override 優先於內建規則

未知 model、mode、token type 或未落在有效區間的價格回傳 `null`，不做推估；Codex credits 不當作 USD

官方來源 metadata：

- OpenAI API pricing: https://openai.com/api/pricing/
- Anthropic API pricing: https://docs.anthropic.com/en/docs/about-claude/pricing
