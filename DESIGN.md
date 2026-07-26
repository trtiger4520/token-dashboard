---
name: Stratum Docs
description: 開發者文件的排版與介面語言：髮絲線構成結構，平面不浮起，一種有彩訊號。
colors:
  background: "oklch(0.99 0 0)"
  foreground: "oklch(0.21 0 0)"
  card: "oklch(1 0 0)"
  muted: "oklch(0.985 0 0)"
  muted-foreground: "oklch(0.556 0 0)"
  accent: "oklch(0.967 0 0)"
  surface-sunken: "oklch(0.975 0 0)"
  primary: "oklch(0.55 0.17 258)"
  primary-hover: "oklch(0.49 0.17 258)"
  primary-foreground: "oklch(0.99 0.005 258)"
  border: "oklch(0.92 0 0)"
  border-strong: "oklch(0.87 0 0)"
  ring: "oklch(0.145 0 0 / 0.35)"
  info: "oklch(0.45 0.18 255)"
  info-muted: "oklch(0.965 0.015 255)"
  success: "oklch(0.42 0.14 152)"
  success-muted: "oklch(0.965 0.015 152)"
  warning: "oklch(0.48 0.15 65)"
  warning-muted: "oklch(0.97 0.02 70)"
  danger: "oklch(0.47 0.18 25)"
  danger-muted: "oklch(0.965 0.015 25)"
  grid-line: "oklch(0 0 0 / 0.08)"
  grid-line-accent: "oklch(0 0 0 / 0.12)"
typography:
  display:
    fontFamily: "Inter, Inter Variable, system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "2.1875rem"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "-0.025em"
    fontFeature: "cv02, cv03, cv04, cv11"
  headline:
    fontFamily: "Inter, Inter Variable, system-ui, sans-serif"
    fontSize: "1.3rem"
    fontWeight: 600
    lineHeight: 1.4
    letterSpacing: "-0.015em"
  title:
    fontFamily: "Inter, Inter Variable, system-ui, sans-serif"
    fontSize: "1.1rem"
    fontWeight: 600
    lineHeight: 1.4
    letterSpacing: "-0.01em"
  lead:
    fontFamily: "Inter, Inter Variable, system-ui, sans-serif"
    fontSize: "1.125rem"
    fontWeight: 400
    lineHeight: 1.6
  body:
    fontFamily: "Inter, Inter Variable, system-ui, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.75
  label:
    fontFamily: "Inter, Inter Variable, system-ui, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 500
    lineHeight: 1.4
  nav:
    fontFamily: "Inter, Inter Variable, system-ui, sans-serif"
    fontSize: "0.8125rem"
    fontWeight: 500
    lineHeight: 1.4
  eyebrow:
    fontFamily: "Inter, Inter Variable, system-ui, sans-serif"
    fontSize: "0.6875rem"
    fontWeight: 600
    lineHeight: 1.4
    letterSpacing: "0.05em"
  code:
    fontFamily: "JetBrains Mono, JetBrains Mono Variable, ui-monospace, Cascadia Code, Menlo, Consolas, monospace"
    fontSize: "0.875rem"
    fontWeight: 450
    lineHeight: 1.625
rounded:
  xs: "0.25rem"
  sm: "0.375rem"
  md: "0.5rem"
  lg: "0.75rem"
  full: "9999px"
spacing:
  "1": "0.25rem"
  "2": "0.5rem"
  "3": "0.75rem"
  "4": "1rem"
  "5": "1.25rem"
  "6": "1.5rem"
  "8": "2rem"
  "10": "2.5rem"
  flow-block: "1.25rem"
  flow-h2: "2.5rem"
  flow-h3: "2rem"
  flow-figure: "1.5rem"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.primary-foreground}"
    typography: "{typography.label}"
    rounded: "{rounded.full}"
    padding: "0 0.75rem"
    height: "2.25rem"
  button-primary-hover:
    backgroundColor: "{colors.primary-hover}"
  button-secondary:
    backgroundColor: "{colors.card}"
    textColor: "{colors.foreground}"
    typography: "{typography.label}"
    rounded: "{rounded.full}"
    padding: "0 0.75rem"
    height: "2.25rem"
  button-secondary-hover:
    backgroundColor: "{colors.accent}"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.foreground}"
    typography: "{typography.label}"
    rounded: "{rounded.full}"
    padding: "0 0.75rem"
    height: "2.25rem"
  button-ghost-hover:
    backgroundColor: "{colors.accent}"
  button-icon-square:
    backgroundColor: "{colors.card}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.md}"
    padding: "0"
    height: "2.25rem"
    width: "2.25rem"
  badge-neutral:
    backgroundColor: "{colors.muted}"
    textColor: "{colors.muted-foreground}"
    rounded: "{rounded.full}"
    padding: "0.125rem 0.5rem"
  card:
    backgroundColor: "{colors.card}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.lg}"
    padding: "1rem 1.125rem"
  aside-note:
    backgroundColor: "{colors.info-muted}"
    textColor: "{colors.foreground}"
    typography: "{typography.body}"
    rounded: "{rounded.md}"
    padding: "1rem"
  code-figure:
    backgroundColor: "{colors.card}"
    textColor: "{colors.foreground}"
    typography: "{typography.code}"
    rounded: "{rounded.md}"
    padding: "0.75rem 1rem"
  code-figure-titlebar:
    backgroundColor: "{colors.surface-sunken}"
    textColor: "{colors.foreground}"
    padding: "0.5rem 0.875rem"
  sidebar-link:
    backgroundColor: "transparent"
    textColor: "{colors.muted-foreground}"
    typography: "{typography.nav}"
    rounded: "{rounded.md}"
    padding: "0.25rem 0.75rem"
    height: "2rem"
  sidebar-link-current:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.foreground}"
  search-field:
    backgroundColor: "{colors.card}"
    textColor: "{colors.muted-foreground}"
    typography: "{typography.label}"
    rounded: "{rounded.md}"
    padding: "0 0.625rem"
    height: "2.25rem"
    width: "16rem"
  search-field-hover:
    backgroundColor: "{colors.accent}"
---

# Design System: Stratum Docs

## Overview

**Creative North Star: "The Blueprint Margin"**

這是一張技術圖面，不是一疊卡片。整套系統的結構全部由 1px 髮絲線與虛線區隔完成——邊框、分隔線、步驟串接線、TOC 的側邊軌、landing 頁上標示內容欄範圍的虛線規則。沒有任何東西從紙面上浮起來，因為圖紙上的線條本來就不會浮起來。四層中性面彼此只差一階（0.99 / 1.0 / 0.985 / 0.967），它們是紙張的紋理差異，不是海拔高度。

氣質是冷靜、精確、克制。介面不負責取悅，只負責把事實擺正：字級刻意壓平（h2 只比內文大 30%），階層由字重、間距與規則線承擔；全頁只有一種有彩訊號，而它同時就是連結色；互動回饋是 150ms 的顏色交叉淡入，從不是位移或縮放。頁面預設是安靜的——複製按鈕與標題錨點停在 `opacity: 0`，你把游標移過去它們才出現。

刻意拒絕的東西是明確的：沒有攝影、沒有主視覺大圖、沒有裝飾性漸層、沒有材質貼圖、沒有 emoji、沒有進場動畫、沒有按壓回饋。唯二的半透明用途是黏頂頁首（85% 背景 + 8px 背景模糊）與 modal 遮罩（50% 黑 + 4px 模糊）。長內容永遠在有邊框的容器裡捲動，絕不用「保護漸層」淡出。

**Key Characteristics:**
- 結構來自 1px 髮絲線，不來自陰影
- 平壓的字級尺度：階層靠字重、間距、規則線
- 43.5rem 閱讀欄（1536px 以上 52rem）、16px/1.75 內文
- 唯一有彩訊號 = 主色 = 連結色
- 顏色交叉淡入取代所有位移動畫
- 需要時才顯形的工具（hover 才淡入的複製鈕與錨點）
- 明暗模式是同一套語意名的兩組值，不是兩套設計

## Colors

中性驅動、近乎單色的調色盤：四層只差一階的紙面、一組做了 90% 工作的前景對、一個有彩訊號、四個只在狀態發生時現身的狀態色。

### Primary
- **Schematic Blue｜圖紙藍** (`oklch(0.55 0.17 258)`)：技術圖面上的線條顏色，不是行銷主色。用於主要動作按鈕、所有連結（`--sd-link` 直接別名到它）、程式碼行高亮的左側 2px 標記、文字選取底色（10% 混色）。它的職責是標示「這裡可以走」。
- **Schematic Blue Pressed｜圖紙藍（深）** (`oklch(0.49 0.17 258)`)：主要按鈕 hover 時的唯一變化。
- **Blueprint Paper｜圖紙白** (`oklch(0.99 0.005 258)`)：壓在主色上的文字色，帶極微藍調而非純白。

### Neutral
- **Graphite｜石墨** (`oklch(0.21 0 0)`)：正文、標題、當前導覽項的文字色。TOC 的當前項用它畫 1px 左側規則段。
- **Pencil Gray｜鉛筆灰** (`oklch(0.556 0 0)`)：同一支筆較輕的力道——次級資訊、閒置導覽連結、卡片內文、程式碼標題列的語言標籤、圖示 chrome。與石墨的這一對做了系統 90% 的工作。
- **Sheet｜紙面** (`oklch(0.99 0 0)`)：頁面底色。
- **Card｜卡面** (`oklch(1 0 0)`)：卡片、程式碼圖框、次級按鈕、搜尋觸發器的面。比頁面亮一階。
- **Muted｜淡面** (`oklch(0.985 0 0)`)：行內程式碼、表頭、步驟編號圓標、鍵盤提示 chip 的底。
- **Accent｜覆手面** (`oklch(0.967 0 0)`)：唯一的 hover／當前狀態填色。導覽列、ghost 按鈕、卡片、次級按鈕全部共用它。
- **Sunken｜下沉面** (`oklch(0.975 0 0)`)：坐在卡面之下的條狀區——程式碼檔名列、分頁條。
- **Hairline｜髮絲線** (`oklch(0.92 0 0)`) / **Hairline Strong｜髮絲線（強）** (`oklch(0.87 0 0)`)：靜止與 hover 兩階。系統裡所有「結構」都是這兩個值畫的。
- **Ring｜焦點環** (`oklch(0.145 0 0 / 0.35)`)：半透明中性焦點環，不帶色相，因此不與主色搶訊號。
- **Grid Line｜圖紙格線** (`oklch(0 0 0 / 0.08)`、強調 `oklch(0 0 0 / 0.12)`)：landing 頁標示內容欄邊界的髮絲／虛線規則。這是全系統唯一的裝飾性圖形母題。

### Status
- **Info** (`oklch(0.45 0.18 255)`)、**Success** (`oklch(0.42 0.14 152)`)、**Caution** (`oklch(0.48 0.15 65)`)、**Danger** (`oklch(0.47 0.18 25)`)：每一個都有一個 `-muted` 填底夥伴。base 值只出現在文字、圖示與 4px 側規則上；`-muted` 只出現在底色上。永不互換。

### Dark mode
暗色以 `[data-mode="dark"]` 反轉同一組語意名（值見 `.impeccable/design.json` 的 `colorMeta.*.dark`）。兩件事必須保留：前景 (`oklch(0.925 0.008 258)`) 帶一絲冷調，永遠不是純白；主色在暗色下提亮並降彩度 (`oklch(0.68 0.15 258)`)，維持與底的對比而不刺眼。

### Named Rules
**The One Voice Rule.** 圖紙藍是全頁唯一的有彩訊號，而且它同時就是連結色。除了連結、主要動作、程式碼行高亮與選取底色，任何區塊都不得為了「有點顏色」而使用它。狀態色只在狀態真的發生時出現，不當裝飾色。

**The Paired Surface Rule.** 四層中性面（0.99 → 1.0 → 0.985 → 0.967）彼此只差一階，且每一層都必須與既定前景成對使用。不得為了做出層次而新增第五層、加深某一層，或把 `accent` 拿去當靜止底色——它專屬於 hover 與當前狀態。

## Typography

**Display Font:** Inter Variable（fallback：system-ui、-apple-system、Segoe UI、Roboto）
**Body Font:** Inter Variable（同上；全系統只有一套無襯線）
**Label/Mono Font:** JetBrains Mono Variable（fallback：ui-monospace、Cascadia Code、Menlo、Consolas）

**Character:** Inter 啟用字符變體 `cv02 cv03 cv04 cv11`——可辨識的 `l`／`1`、開口的 `6`／`9`。這不是美學選擇，是因為文件的正文裡混著設定鍵與旗標，字母必須在內文尺寸下不會被讀錯。搭配灰階抗鋸齒，讓小字級的 UI chrome 保持乾淨的筆畫。

### Hierarchy
- **Display**（600、35px、行高 1.2、字距 -0.025em）：頁面 h1，一頁一次。
- **Headline**（600、20.8px、字距 -0.015em）：h2 章節標記。上方留 2.5rem。
- **Title**（600、17.6px、字距 -0.01em）：h3、卡片標題、Aside 標題、Step 標題。上方留 2rem。
- **Lead**（400、18px、行高 1.6）：頁面描述的單句開場。
- **Body**（400、16px、行高 1.75）：正文。欄寬 43.5rem（約 72ch），1536px 以上放寬到 52rem。
- **Label**（500、14px）：按鈕、次級 UI、程式碼內文。
- **Nav**（500、13px）：側欄連結、TOC 連結、表頭。當前項升到 600。
- **Eyebrow**（600、11px、字距 0.05em、全大寫）：側欄／TOC 的分區標題、語言標籤。
- **Code**（450、14px、行高 1.625）：行內程式碼字重刻意比正文重半階（450）以在 16px 內文中站得住。

### Named Rules
**The Flat Scale Rule.** h2 只比正文大 30%（20.8px vs 16px）。要製造階層時，先動字重、留白與規則線，不動字級。任何新增的尺寸都必須落在既有的 11 / 12 / 13 / 14 / 14.4 / 16 / 18 / 17.6 / 20.8 / 35px 之內，不新增級距。

**The Measure Rule.** 內文欄寬固定 43.5rem、行高 1.75。不得為了填滿寬螢幕而加寬閱讀欄——寬螢幕的空間給側欄與 TOC，不給正文。

**The Literal-in-Code Rule.** 檔名、指令、旗標、值、型別一律進行內程式碼 chip（淡面底 + 髮絲邊 + 6px 圓角 + 0.875em）。正文絕不用普通字體拼出一段指令。

## Layout

三欄外殼，全部黏頂：4rem 頁首（85% 背景 + 8px 背景模糊）、18.75rem 左側導覽軌（自有捲動）、居中的 43.5rem 內容欄、18rem「On this page」右軌。水平區塊內距隨視窗分三階：1rem → 1.5rem（64rem 起）→ 2rem（80rem 起）。內容欄寬在 1536px 以上放寬到 52rem，那是唯一的欄寬變化。

間距只有一套八階（0.25 / 0.5 / 0.75 / 1 / 1.25 / 1.5 / 2 / 2.5rem）。垂直文章節奏是四個值，不是自由發揮：區塊之間 1.25rem、h2 之上 2.5rem、h3 之上 2rem、圖框（程式碼、表格、步驟）上下 1.5rem。標題的 `scroll-margin-top` 是 5rem，讓錨點跳轉不會被黏頂頁首吃掉。

寬表格不撐破欄寬：表格一律包在 `.sd-table-scroll` 容器裡（1px 邊框 + 12px 圓角），在原地水平捲動。

### Named Rules
**The Finite Column Rule.** 閱讀欄居中且有上限。滿版區塊是例外而非常態；需要更多資訊密度時，往側欄與 TOC 借空間，不往正文借。

## Elevation & Depth

這套系統本質上是平的。深度由色調分層（四層只差一階的中性面）與 1px 邊框表達，不由陰影表達。陰影詞彙存在，但被壓到幾乎看不見——四階裡最常用的 `xs` 只是 `0 1px 2px oklch(0 0 0 / 0.04)`，它的作用是讓按鈕不至於看起來像被剪下來貼上，而不是製造高度。

### Shadow Vocabulary
- **xs**（`0 1px 2px oklch(0 0 0 / 0.04)`）：按鈕（primary／secondary／destructive）的靜止定著感。
- **sm**（`0 1px 3px oklch(0 0 0 / 0.06), 0 1px 2px oklch(0 0 0 / 0.04)`）：極少用；需要與背景稍作區隔的小型浮層。
- **lg**（`0 4px 12px oklch(0 0 0 / 0.08), 0 2px 4px oklch(0 0 0 / 0.04)`）：popover、下拉、combobox 選單。
- **overlay**（`0 24px 80px oklch(0 0 0 / 0.4)`）：只給 modal 與搜尋覆蓋層。

暗色模式下四階的不透明度整體加重（0.2 / 0.3 / 0.4），因為深色底需要更多才看得見同樣的分離感。

### Named Rules
**The Floating Signal Rule.** 陰影唯一的意思是「這個元件不在文件流裡，它蓋在上面」。靜止的卡片、Aside、程式碼圖框、表格一律無影。hover 從不改變陰影——它只改變顏色與邊框強度。

**The Hairline Structure Rule.** 所有結構性分隔一律是 1px `border`（`oklch(0.92 0 0)`），hover 時升級為 `border-strong`（`oklch(0.87 0 0)`）。系統裡沒有內陰影體系：`inset` 只用於 1px 環（按鈕邊框、核取方塊）與程式碼行高亮的 2px 左標記。

## Shapes

圓角語言把「可點擊的動作」與「承載內容的面」分成兩類形狀。動作是全圓角膠囊（`9999px`）——按鈕、Badge、步驟編號圓標；面是方角面板，圓角隨面積上升：4px（語言標籤）、6px（行內程式碼、圖示按鈕、複製鈕）、8px（程式碼圖框、側欄連結、Aside、搜尋觸發器）、12px（卡片、表格容器）。

邊框永遠是 1px，永遠是實線——唯一的虛線出現在 landing 頁的圖紙格線上。沒有任何元件使用裁切、斜角、不對稱圓角或造型遮罩。Aside 是唯一帶方向性形狀的元件：行首側 4px 實色規則，其餘三邊無框。

### Named Rules
**The Pill-or-Panel Rule.** 帶文字的動作永遠是膠囊（`9999px`），承載內容的面永遠是 4–12px 的方角面板。兩者不互換：卡片不做膠囊，文字按鈕不做 8px。唯一例外是 icon-only 的方形按鈕，用 8px 圓角以維持正方比例。

## Components

**元件手感：紙上構件——它們是印上去的框，不是可按壓的實體。** 沒有按壓回饋、沒有浮起、沒有縮放。狀態變化只發生在顏色與邊框強度上。

### Buttons
- **Shape:** 膠囊全圓角（`9999px`）；`shape="square"` 的 icon-only 版本用 8px（`--sd-radius-md`）。
- **Sizes:** 四階高度 20 / 28 / 36 / 40px（`1.25` / `1.75` / `2.25` / `2.5rem`），字級 12 / 12 / 14 / 14px。
- **Primary:** 圖紙藍底 + 圖紙白字 + `shadow-xs`，內距 `0 0.75rem`。
- **Secondary（文件頁的預設）:** 卡面底 + 石墨字 + `inset 0 0 0 1px border` 的 1px 環。頁面因此保持安靜——文件裡大多數按鈕都不是主色。
- **Hover:** primary 換成深一階的藍；secondary／ghost 填入 `accent` 面，secondary 的 1px 環同時升到 `border-strong`。**只換顏色，不縮放、不位移、不改陰影。**
- **Ghost / Outline:** 透明底；ghost hover 填 `accent`，outline 只把 1px 環升強。
- **Disabled:** `opacity: 0.5` + `cursor: not-allowed`，不改顏色。

### Badges / Chips
- **Style:** 膠囊、`-muted` 底、base 色文字、25% 混色的同色系 1px 邊。中性版用淡面底 + 鉛筆灰字 + 髮絲邊。
- **Sizes:** base（12px 字、`0.125rem 0.5rem`）與 sm（11px 字、`0.0625rem 0.375rem`）。
- 用於方案層級、可用性、beta 標記與變更日誌分類。它是標籤，不是按鈕：沒有 hover 狀態。

### Cards / Containers
- **Corner Style:** 12px（`--sd-radius-lg`）。
- **Background:** 卡面（比頁底亮一階）。
- **Shadow Strategy:** 靜止無影（見 The Floating Signal Rule）。
- **Border:** 1px 髮絲線；可點擊的 LinkCard hover 時填 `accent` 並升到 `border-strong`。
- **Internal Padding:** `1rem 1.125rem`。標題走 Title 級距、內文走 14px／行高 1.6 的鉛筆灰。

### Inputs / Fields
- **Style:** 卡面底、1px 髮絲邊、8px 圓角、36px 高。搜尋觸發器右端固定帶一顆鍵盤提示 chip（等寬字、11px、4px 圓角、淡面底）。
- **Hover:** 填 `accent` 面並把邊框升到 `border-strong`。
- **Focus:** `2px solid var(--sd-ring)`、`outline-offset: 2px`。焦點環是無色相的半透明中性——它不與主色搶訊號。密集導覽列裡改用 `-2px` 內偏移，避免環被裁切。
- **Checkbox:** 方框以 1px `inset` 環表達，勾記使用 Phosphor bold 字重（小型填色形狀內唯一用 bold 的地方）。
- 系統目前**沒有**通用單行文字輸入框。需要時照上述 8px／髮絲邊／`accent` hover／`ring` focus 的規則新建，不要另起一套。

### Navigation
- **Sidebar:** 18.75rem 軌、13px 連結、32px 最小列高、8px 圓角。閒置為鉛筆灰、hover 填 `accent`、當前項填 `accent` 且字重升到 600。群組可收合：`grid-template-rows` 由 `0fr` 動到 `1fr`，250ms `cubic-bezier(0.87,0,0.13,1)`，插入符同曲線旋轉 90°。子層以 1px 左側規則線縮排，不用縮排空白。
- **Toc:** 18rem 右軌，整條 1px 左側規則；當前標題把該段規則換成石墨色並升到 600 字重。這是全系統最小的狀態訊號，也是最典型的一個——用一段線，不用一塊底色。
- **Breadcrumbs / Pagination:** 12–14px、鉛筆灰、分隔用排版符號（`›`、`·`），不用圖示。
- **Header:** 4rem 黏頂，85% 背景 + 8px 背景模糊——這是全系統僅有的兩處半透明之一。

### Code Figure（signature）
文件的主角元件。1px 邊框 + 8px 圓角 + 卡面底 + `overflow: hidden` 的 `<figure>`；帶檔名時上方加一條下沉面標題列（等寬 12px 檔名靠左、大寫語言標籤靠右）。無檔名時語言標籤浮在右上角，`opacity: 0.75`，並在 hover 時淡出讓位給複製鈕。複製鈕停在 `opacity: 0`，容器 hover 才淡入；成功複製時圖示換成勾記並轉為 success 色 1.2 秒。行高亮用 12% 混色的 info 底 + `inset 2px 0 0` 的左側標記。

### Steps（signature）
編號程序列。每步左側一顆 24px 圓標（膠囊、淡面底、1px 邊、12px 600 字重數字），圓標之間由一條 1px 垂直規則串起來——那條線就是「這是一個連續程序」的全部視覺說明。最後一步不畫線。

### Aside（signature）
四種語意（note／tip／caution／danger）共用一個形狀：行首側 4px 實色規則、`-muted` 底、8px 圓角、1rem 內距，標題列用 Phosphor **fill** 字重圖示 + 對應狀態色 + Title 級距。它是唯一以顏色承載語意的內容元件，因此它的顏色永遠來自狀態 token，絕不自訂。

### Motion & States
- **Transitions:** 150ms 的 `color`／`background-color`／`border-color`／`box-shadow` 交叉淡入，`ease`。
- **Disclosure:** 250ms `cubic-bezier(0.87,0,0.13,1)`，只用於 `grid-template-rows` 展開與插入符旋轉。
- **Hover-revealed:** 複製鈕、標題錨點停在 `opacity: 0`，容器 hover 才淡入。
- **Reduced motion:** `prefers-reduced-motion` 把所有動畫與轉場壓到 0.01ms 並關閉平滑捲動。

### Named Rules
**The Crossfade Rule.** 介面回饋是顏色交叉淡入，永遠不是位移。沒有 `transform`、沒有 `scale`、沒有進場動畫、沒有視差、沒有彈跳。唯一被允許移動的東西是揭露元件的插入符旋轉。

**The Reach-For-It Rule.** 工具性的可操作元素（複製、錨點）靜止時不可見，只在容器 hover 時淡入。頁面在你伸手之前保持安靜。

## Do's and Don'ts

### Do:
- **Do** 用 1px `border`（`oklch(0.92 0 0)`）表達所有結構性分隔，hover 時升到 `border-strong`（`oklch(0.87 0 0)`）。
- **Do** 把正文欄寬鎖在 43.5rem（1536px 以上 52rem）、行高 1.75。
- **Do** 把 hover 與當前狀態一律填 `accent`（`oklch(0.967 0 0)`），這是系統裡唯一的狀態填色。
- **Do** 讓文件頁的按鈕預設用 `secondary` 變體，主色只留給該頁真正的主要動作。
- **Do** 把檔名、指令、旗標、值放進行內程式碼 chip。
- **Do** 讓可點擊的動作維持膠囊形（`9999px`），承載內容的面維持 4–12px 方角。
- **Do** 用狀態 token 的 base 值畫文字／圖示／4px 側規則，用 `-muted` 值畫底色。
- **Do** 讓寬表格在 `.sd-table-scroll`（1px 邊 + 12px 圓角）裡原地捲動。
- **Do** 給 icon-only 按鈕一定要加 `aria-label`。
- **Do** 新增元件時先在既有的間距八階、圓角五階、字級表裡找值。

### Don't:
- **Don't** 用陰影表示層次或狀態。陰影只代表「浮在文件之上」，靜止元件一律無影。
- **Don't** 在 hover／press 時做任何位移、縮放或浮起——那些狀態只換顏色與邊框強度。
- **Don't** 為了做出對比而新增第五層中性面，或加深現有四層之間的差距。
- **Don't** 把主色拿來當裝飾底色、區塊背景或圖表填色。它是連結與主要動作的專屬訊號。
- **Don't** 新增字級。要更強的階層就加字重、加留白、加規則線。
- **Don't** 加入攝影、主視覺大圖、裝飾性漸層、材質貼圖或插畫底紋。
- **Don't** 用漸層淡出遮蔽長內容——用有邊框的捲動容器。
- **Don't** 使用 emoji，或把 unicode 符號當圖示用。內容裡允許的 unicode 只有排版符號（—、·、›）與程式碼區塊的 `+`／`−` 差異標記。
- **Don't** 複製或改造他人的品牌資產（logo、品牌色、產品圖示）。沒有 logo 時就用文字 wordmark，缺的資產留白。
- **Don't** 讓焦點環帶色相。它必須是無彩的半透明中性環，才不會與主色搶訊號。
