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
pnpm install --frozen-lockfile
pnpm --filter token-dashboard-web build
dotnet run --project src/TokenDashboard.Api/TokenDashboard.Api.csproj
```

macOS 或 Linux：

```bash
pnpm install --frozen-lockfile
pnpm --filter token-dashboard-web build
dotnet run --project src/TokenDashboard.Api/TokenDashboard.Api.csproj
```

完整驗證可執行 `scripts/build.ps1` 或 `bash scripts/build.sh`

發布可執行 `scripts/publish.ps1` 或 `bash scripts/publish.sh`，預設產生 `win-x64`、`linux-x64`、`osx-x64` 的 self-contained 產物

self-contained 產物已包含 .NET runtime，不需要另外安裝 .NET 10

執行 `TokenDashboard.Api --version` 或 `TokenDashboard.Api -v` 可輸出目前版本

安裝最新版可從 GitHub Release 下載 `install.ps1` 或 `install.sh`，安裝器預設使用 latest，也支援指定版本；安裝器會驗證 SHA-256、建立使用者範圍的 `token-dashboard` 命令，並在重跑時更新既有安裝

PowerShell 安裝：

```powershell
Invoke-WebRequest 'https://github.com/trtiger4520/token-dashboard/releases/latest/download/install.ps1' -OutFile "$env:TEMP\token-dashboard-install.ps1"
& "$env:TEMP\token-dashboard-install.ps1"
```

macOS 或 Linux 安裝：

```bash
curl -fsSL https://github.com/trtiger4520/token-dashboard/releases/latest/download/install.sh -o /tmp/token-dashboard-install.sh
bash /tmp/token-dashboard-install.sh
```

指定版本時使用 `-Version 0.1.0` 或 `--version 0.1.0`；安裝器不會自動啟動服務，重跑相同指令即可更新。若 Dashboard 正在執行，請先關閉後再更新

## 自動發版

`main` 的 push 會執行完整驗證，只有 `feat`、`fix`、`perf` 或 Conventional Commits 的 breaking change 會建立 GitHub Release；PR title 必須符合 Conventional Commits 並使用 squash merge

第一次發版使用 `.github/workflows/bootstrap-release.yml` 手動建立 `v0.1.0`，成功後應刪除該一次性 workflow。後續由 `.github/workflows/release.yml` 自動產生版本、三平台 self-contained archive、SHA-256 與安裝腳本

GitHub repository 建議設定：

- `main` 必須透過 Pull Request 合併
- 啟用 squash merge 並停用不需要的 merge 方式
- 將 CI 與 commitlint 設為必要檢查
- 允許 GitHub Actions 使用 `GITHUB_TOKEN` 建立 tag 與 Release
- 發版失敗時保留既有 tag，依 GitHub Actions log 手動補上傳缺少的 asset

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
