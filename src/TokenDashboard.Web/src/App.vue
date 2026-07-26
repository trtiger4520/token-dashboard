<script setup lang="ts">
/*
THESIS: Treat the dashboard as a working blueprint margin, not a card gallery; the comparison is the primary object
OWN-WORLD: Neutral paper surfaces, 1px hairlines, Inter data labels, JetBrains Mono measurements, and schematic blue only for action
STORY: A developer starts with a date and source, compares token efficiency, then opens a session down to its event evidence
FIRST VIEWPORT: The left rail fixes scope, the center places KPIs and comparison evidence, and the right rail holds the selected record
FORM: Operate-mode three-column control rail / comparison matrix / inspector, inherited from the route dashboard surface brief
*/
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import { extractStartupKey, TokenDashboardClient, type SourceDiscoveryResult, type SyncRequest } from './api'
import { isValidDateRange, resolveDateRange, type DatePreset } from './dateRange'
import { createEmptyDashboardData, formatDateLabel, formatNumber, formatUsd, totalTokens, type DashboardData, type DashboardQuery, type EventKind, type SearchResult, type SessionRecord, type TagRecord, type TokenType } from './types'

const client = new TokenDashboardClient()
const data = ref<DashboardData>(createEmptyDashboardData())
const controlRailOpen = ref(false)
const syncState = ref<'loading' | 'ready' | 'empty' | 'error' | 'partial'>('loading')
const errorMessage = ref('')
const operationMessage = ref('')
const selectedSessionId = ref('')
const selectedEventId = ref('')
const inspectorTab = ref<'detail' | 'stats' | 'capabilities'>('detail')
const searchTerm = ref('')
const searchResults = ref<Array<SearchResult & { title: string }>>([])
const searchError = ref('')
const sourcePath = ref('')
const sourceAdapter = ref('auto')
const discoveredSources = ref<SourceDiscoveryResult[]>([])
const tagInput = ref('')
const tagValue = ref('')
const tagScope = ref<'source' | 'project' | 'session'>('session')
const tagEntityId = ref('')
const pricingProvider = ref('openai')
const pricingModel = ref('')
const pricingTokenType = ref('')
const pricingMode = ref('standard')
const pricingAmount = ref('')
const pricingEffectiveFrom = ref('')
const pricingEffectiveTo = ref('')
const pricingMinimum = ref('0')
const pricingMaximum = ref('')
const showDeleteConfirm = ref(false)
const pendingExport = ref<'json' | 'sqlite' | null>(null)
const deleteDialog = ref<HTMLDialogElement | null>(null)
const exportDialog = ref<HTMLDialogElement | null>(null)
const lastDialogTrigger = ref<HTMLElement | null>(null)
const isDark = ref(false)
const selectedDate = ref('')
const datePreset = ref<DatePreset>('30')
const dateRange = reactive(resolveDateRange('30'))
const filters = reactive({ sourceId: 'all', tool: 'all', model: 'all', tokenType: 'all' as TokenType | 'all' })

const selectedSession = computed<SessionRecord | undefined>(() => data.value.sessions.find((session) => session.id === selectedSessionId.value))
const selectedEvent = computed(() => selectedSession.value?.turns.flatMap((turn) => turn.events).find((event) => event.id === selectedEventId.value))
const allTags = computed(() => [...new Set(data.value.tags.map((tag) => tag.key).concat(data.value.sessions.flatMap((session) => session.tags)))].sort())
const visibleSessions = computed(() => data.value.sessions)
const totalTokenCount = computed(() => totalTokens(data.value.overview.tokenCounts))
const totalCost = computed(() => data.value.overview.costUsd)
const totalSessions = computed(() => data.value.overview.sessionCount)
const averageCache = computed(() => data.value.overview.cacheHitRate)
const maxDailyTokens = computed(() => Math.max(...data.value.daily.map((day) => day.tokens), 1))
const heatmapDays = computed(() => (data.value.heatmap.length ? data.value.heatmap : data.value.daily).map((day) => ({ ...day, intensity: Math.max(1, Math.ceil((day.tokens / maxDailyTokens.value) * 5)) })))
const sourceStatus = computed(() => discoveredSources.value.length ? `已檢查 ${discoveredSources.value.length} 個 adapter` : data.value.sources.length ? `已載入 ${data.value.sources.length} 個來源` : '來源未提供')

function dashboardQuery(): DashboardQuery {
  const preset = datePreset.value === '7' ? '7d' : datePreset.value === '90' ? '90d' : datePreset.value === 'custom' ? 'custom' : datePreset.value
  return {
    preset,
    from: dateRange.startDate,
    to: dateRange.endDate,
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
    sourceId: filters.sourceId === 'all' ? undefined : filters.sourceId,
    tool: filters.tool === 'all' ? undefined : filters.tool,
    model: filters.model === 'all' ? undefined : filters.model,
    tokenType: filters.tokenType === 'all' ? undefined : filters.tokenType
  }
}

function setTheme(dark: boolean): void {
  isDark.value = dark
  document.documentElement.dataset.mode = dark ? 'dark' : 'light'
}

function applyPreset(preset: DatePreset): void {
  datePreset.value = preset
  if (preset !== 'custom') Object.assign(dateRange, resolveDateRange(preset))
  void refresh()
}

async function refresh(): Promise<void> {
  if (!isValidDateRange(dateRange)) {
    errorMessage.value = '日期範圍無效，請確認開始日期不晚於結束日期'
    syncState.value = 'error'
    return
  }
  syncState.value = 'loading'
  errorMessage.value = ''
  try {
    const result = await client.getDashboard(dashboardQuery())
    data.value = result
    syncState.value = result.sessions.length === 0 ? 'empty' : 'ready'
    if (!data.value.sessions.some((session) => session.id === selectedSessionId.value)) selectedSessionId.value = data.value.sessions[0]?.id ?? ''
  } catch (error) {
    syncState.value = 'error'
    errorMessage.value = error instanceof Error ? error.message : '無法讀取本機資料'
  }
}

function selectSession(session: SessionRecord): void {
  selectedSessionId.value = session.id
  selectedEventId.value = ''
  inspectorTab.value = 'detail'
}

function selectDate(date: string): void {
  selectedDate.value = date
  const day = (data.value.heatmap.length ? data.value.heatmap : data.value.daily).find((item) => item.date === date)
  if (day) searchTerm.value = day.date
}

function addTag(): void {
  const tag = tagInput.value.trim()
  const entityId = tagEntityId.value.trim() || (tagScope.value === 'session' ? selectedSession.value?.id ?? '' : '')
  if (!tag || !entityId) {
    operationMessage.value = '請提供 tag scope 與 entity target'
    return
  }
  void client.addTag({ scope: tagScope.value, entityId, key: tag, value: tagValue.value.trim() }).then(async () => {
    tagInput.value = ''
    tagValue.value = ''
    operationMessage.value = `tag ${tag} 已儲存`
    await refresh()
  }).catch((error: unknown) => {
    operationMessage.value = error instanceof Error ? error.message : 'tag 儲存失敗'
  })
}

function removeTag(tag: string): void {
  if (!selectedSession.value) return
  const assignment = data.value.tags.find((item: TagRecord) => item.scope === 'session' && item.entityId === selectedSession.value?.id && item.key === tag)
  removeAssignment(assignment ?? { id: tag, key: tag, value: '', scope: 'session', entityId: selectedSession.value.id })
}

function removeAssignment(assignment: TagRecord): void {
  const scope = assignment.scope || tagScope.value
  const entityId = assignment.entityId || tagEntityId.value.trim() || (scope === 'session' ? selectedSession.value?.id ?? '' : '')
  if (!entityId) {
    operationMessage.value = '刪除 tag 需要 entity target'
    return
  }
  void client.deleteTag(scope, entityId, assignment.id || assignment.key).then(async () => {
    operationMessage.value = `tag ${assignment.key} 已刪除`
    await refresh()
  }).catch((error: unknown) => {
    operationMessage.value = error instanceof Error ? error.message : 'tag 刪除失敗'
  })
}

async function onImport(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  try {
    await client.importFile(file, sourceAdapter.value)
    operationMessage.value = `${file.name} 已送出匯入`
    await refresh()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '匯入失敗，請確認檔案格式與來源欄位'
    syncState.value = 'error'
  }
}

async function performExport(format: 'csv' | 'json' | 'sqlite'): Promise<void> {
  try {
    const result = await client.export(format, dashboardQuery())
    const url = URL.createObjectURL(result.blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = `token-dashboard.${format}`
    anchor.click()
    URL.revokeObjectURL(url)
    closeDialog('export')
    operationMessage.value = result.warning ?? `${format.toUpperCase()} 匯出完成`
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '匯出需要有效的 localhost session key 與 API 連線'
    syncState.value = 'error'
  }
}

function downloadExport(format: 'csv' | 'json' | 'sqlite'): void {
  if (format === 'csv') {
    void performExport(format)
    return
  }
  pendingExport.value = format
}

function focusDialog(dialog: HTMLDialogElement | null): void {
  void nextTick(() => dialog?.querySelector<HTMLElement>('[data-autofocus]')?.focus())
}

function openDeleteDialog(event: MouseEvent): void {
  lastDialogTrigger.value = event.currentTarget as HTMLElement
  showDeleteConfirm.value = true
  void nextTick(() => {
    if (deleteDialog.value && !deleteDialog.value.open) deleteDialog.value.showModal()
    focusDialog(deleteDialog.value)
  })
}

function openExportDialog(format: 'json' | 'sqlite', event: MouseEvent): void {
  lastDialogTrigger.value = event.currentTarget as HTMLElement
  pendingExport.value = format
  void nextTick(() => {
    if (exportDialog.value && !exportDialog.value.open) exportDialog.value.showModal()
    focusDialog(exportDialog.value)
  })
}

function closeDialog(kind: 'delete' | 'export'): void {
  const dialog = kind === 'delete' ? deleteDialog.value : exportDialog.value
  if (dialog?.open) {
    dialog.close()
    return
  }
  finalizeDialog(kind)
}

function finalizeDialog(kind: 'delete' | 'export'): void {
  if (kind === 'delete') showDeleteConfirm.value = false
  else pendingExport.value = null
  const trigger = lastDialogTrigger.value
  lastDialogTrigger.value = null
  void nextTick(() => trigger?.focus())
}

function cancelDialog(kind: 'delete' | 'export', event: Event): void {
  event.preventDefault()
  closeDialog(kind)
}

async function confirmDelete(): Promise<void> {
  try {
    await client.deleteAll()
    data.value = createEmptyDashboardData()
    selectedSessionId.value = ''
    syncState.value = 'empty'
    closeDialog('delete')
  } catch {
    errorMessage.value = '資料刪除失敗，沒有變更本機資料'
    syncState.value = 'error'
  }
}

async function discoverSources(): Promise<void> {
  try {
    discoveredSources.value = await client.discoverSources(sourceAdapter.value, sourcePath.value || undefined)
    operationMessage.value = `來源掃描完成，共檢查 ${discoveredSources.value.length} 個 adapter`
  } catch (error) {
    operationMessage.value = error instanceof Error ? error.message : '來源掃描失敗'
  }
}

async function syncSources(): Promise<void> {
  try {
    const request: SyncRequest = { adapter: sourceAdapter.value === 'auto' ? undefined : sourceAdapter.value, paths: sourcePath.value ? [sourcePath.value] : undefined }
    const started = await client.startSync(request)
    operationMessage.value = `同步 ${started.status}`
    const status = await client.waitForSync(started.syncId)
    operationMessage.value = status.error ?? `同步${status.status === 'partial' ? '部分完成' : '完成'}`
    const partialSync = status.status === 'partial'
    syncState.value = partialSync ? 'partial' : status.status === 'failed' ? 'error' : syncState.value
    await refresh()
    if (partialSync && syncState.value === 'ready') syncState.value = 'partial'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '同步失敗'
    syncState.value = 'error'
  }
}

async function runSearch(): Promise<void> {
  const query = searchTerm.value.trim()
  if (!query) {
    searchResults.value = []
    searchError.value = ''
    return
  }
  try {
    const results = await client.search(query)
    searchResults.value = results.map((result) => ({ ...result, title: data.value.sessions.find((session) => session.id === result.sessionId)?.title ?? result.itemId }))
    searchError.value = ''
  } catch (error) {
    searchResults.value = []
    searchError.value = error instanceof Error ? error.message : '全文搜尋失敗'
  }
}

async function savePricing(): Promise<void> {
  const provider = pricingProvider.value.trim()
  const model = pricingModel.value.trim()
  const tokenType = pricingTokenType.value.trim()
  const mode = pricingMode.value.trim()
  const amount = Number(pricingAmount.value)
  const minimumInputTokens = Number(pricingMinimum.value)
  const maximumInputTokens = pricingMaximum.value.trim() ? Number(pricingMaximum.value) : null
  if (pricingEffectiveFrom.value && pricingEffectiveTo.value && pricingEffectiveFrom.value >= pricingEffectiveTo.value) {
    operationMessage.value = '有效日期錯誤：effective to 必須晚於 effective from'
    return
  }
  if (!provider || !model || !tokenType || !mode || !Number.isFinite(amount) || amount < 0 || !Number.isFinite(minimumInputTokens) || minimumInputTokens < 0 || (maximumInputTokens !== null && (!Number.isFinite(maximumInputTokens) || maximumInputTokens < minimumInputTokens))) {
    operationMessage.value = '請提供有效的 provider、model、token type、mode 與 token 門檻'
    return
  }
  try {
    await client.updatePricing({
      provider,
      model,
      tokenType,
      mode,
      usdPerMillionTokens: amount,
      minimumInputTokens,
      maximumInputTokens,
      effectiveFromUtc: pricingEffectiveFrom.value ? `${pricingEffectiveFrom.value}T00:00:00Z` : null,
      effectiveToUtc: pricingEffectiveTo.value ? `${pricingEffectiveTo.value}T00:00:00Z` : null
    })
    operationMessage.value = 'pricing override 已儲存'
    await refresh()
  } catch (error) {
    operationMessage.value = error instanceof Error ? error.message : 'pricing override 儲存失敗'
  }
}

function eventClass(kind: EventKind): string {
  return `event-${kind}`
}

function eventCount(session: SessionRecord): number {
  return session.turns.reduce((sum, turn) => sum + turn.events.length, 0)
}

onMounted(() => {
  if (!extractStartupKey()) {
    errorMessage.value = '缺少 localhost session key，請從應用程式入口重新開啟 Dashboard'
    syncState.value = 'error'
    return
  }
  void refresh()
})
</script>

<template>
  <div class="app-frame">
    <header class="topbar">
      <div class="wordmark" aria-label="Token Dashboard">
        <span class="wordmark-mark">TD</span>
        <span>Token Dashboard</span>
      </div>
      <div class="topbar-context">
        <span class="eyebrow">LOCAL ANALYSIS</span>
        <span class="topbar-divider" aria-hidden="true"></span>
        <span class="mono">UTC / {{ dateRange.startDate }} — {{ dateRange.endDate }}</span>
      </div>
      <div class="topbar-actions">
        <span class="sync-indicator" :class="`sync-${syncState}`" role="status">{{ syncState === 'loading' ? '同步中' : syncState === 'partial' ? '部分同步' : syncState === 'error' ? '同步錯誤' : syncState === 'empty' ? '無資料' : '已同步' }}</span>
        <button class="button button-secondary" type="button" @click="void refresh()">重新同步</button>
        <button class="button button-icon" type="button" :aria-label="isDark ? '切換為亮色模式' : '切換為暗色模式'" @click="setTheme(!isDark)">{{ isDark ? 'Light' : 'Dark' }}</button>
      </div>
    </header>

    <div class="dashboard-layout">
      <aside id="control-rail" class="control-rail" :class="{ 'rail-open': controlRailOpen }" aria-label="控制與資料工具">
        <div class="rail-section rail-heading">
          <span class="eyebrow">CONTROL RAIL</span>
          <h1>Dashboard</h1>
          <p>以時間、來源與模型固定比較範圍</p>
        </div>

        <section class="rail-section" aria-labelledby="date-heading">
          <h2 id="date-heading" class="section-label">日期範圍</h2>
          <div class="preset-grid">
            <button v-for="preset in ([['30', '最近 30 天'], ['7', '最近 7 天'], ['90', '最近 90 天'], ['month', '本月'], ['last-month', '上月'], ['custom', '自訂']] as const)" :key="preset[0]" class="preset-button" :class="{ selected: datePreset === preset[0] }" type="button" @click="applyPreset(preset[0])">{{ preset[1] }}</button>
          </div>
          <div class="date-fields">
            <label>開始日期<input v-model="dateRange.startDate" type="date" @change="datePreset = 'custom'" /></label>
            <label>結束日期<input v-model="dateRange.endDate" type="date" @change="datePreset = 'custom'" /></label>
          </div>
        </section>

        <section class="rail-section" aria-labelledby="filter-heading">
          <h2 id="filter-heading" class="section-label">篩選</h2>
          <label>來源<select v-model="filters.sourceId" @change="void refresh()"><option value="all">全部來源</option><option v-for="source in data.sources" :key="source" :value="source">{{ source }}</option></select></label>
          <label>工具<select v-model="filters.tool" @change="void refresh()"><option value="all">App / CLI</option><option v-for="tool in data.tools" :key="tool" :value="tool">{{ tool }}</option></select></label>
          <label>模型<select v-model="filters.model" @change="void refresh()"><option value="all">全部模型</option><option v-for="model in data.models" :key="model" :value="model">{{ model }}</option></select></label>
          <label>Token type<select v-model="filters.tokenType" @change="void refresh()"><option value="all">全部 token</option><option v-for="tokenType in data.tokenTypes" :key="tokenType" :value="tokenType">{{ tokenType }}</option></select></label>
        </section>

        <section class="rail-section" aria-labelledby="source-heading">
          <div class="section-heading-row"><h2 id="source-heading" class="section-label">來源與工具</h2><span class="badge badge-neutral">{{ sourceStatus }}</span></div>
          <p class="rail-note">自動發現、匯入或指定本機路徑。真實供應商格式仍需脫敏樣本驗證。</p>
          <label>Adapter<select v-model="sourceAdapter"><option value="auto">依檔名判斷</option><option value="claude-code-app">Claude Code App</option><option value="claude-code-cli">Claude Code CLI</option><option value="codex-app">Codex App</option><option value="codex-cli">Codex CLI</option></select></label>
          <label>自訂來源路徑<input v-model="sourcePath" placeholder="C:\\workspace\\logs" /></label>
          <div class="button-row"><button class="button button-secondary" type="button" @click="void discoverSources()">掃描來源</button><button class="button button-primary" type="button" @click="void syncSources()">開始同步</button></div>
          <p v-if="discoveredSources.length" class="rail-note"><span v-for="source in discoveredSources" :key="source.adapter" class="discovery-result"><strong>{{ source.adapter }}</strong> · {{ source.paths.length ? source.paths.join(' · ') : '未發現路徑' }}</span></p>
          <label class="file-button">匯入 JSON / CSV<input type="file" accept=".json,.csv,application/json,text/csv" @change="void onImport($event)" /></label>
        </section>

        <section class="rail-section" aria-labelledby="data-heading">
          <h2 id="data-heading" class="section-label">資料工具</h2>
          <div class="button-row"><button class="button button-secondary" type="button" @click="void downloadExport('csv')">CSV</button><button class="button button-secondary" type="button" @click="openExportDialog('json', $event)">JSON</button><button class="button button-secondary" type="button" @click="openExportDialog('sqlite', $event)">SQLite</button></div>
          <p v-if="operationMessage" class="rail-note" role="status">{{ operationMessage }}</p><p class="rail-note warning-copy">JSON / SQLite 匯出可能包含 prompt、response 與 tool 輸入，送出前會再次確認。</p>
          <button class="button button-danger button-full" type="button" aria-label="刪除資料" @click="openDeleteDialog($event)">刪除資料</button>
        </section>
      </aside>

      <main class="workspace" aria-labelledby="workspace-heading">
        <button id="control-toggle" class="mobile-control-toggle" type="button" aria-controls="control-rail" :aria-expanded="controlRailOpen" @click="controlRailOpen = !controlRailOpen"><span>篩選與資料工具</span><span class="mono">{{ controlRailOpen ? '收合' : '展開' }}</span></button>
        <div v-if="syncState === 'partial'" class="state-banner state-warning" role="status"><strong>部分同步</strong><span>{{ operationMessage || '來源同步只完成部分工作，請檢查來源狀態後重新同步' }}</span></div>
        <div v-if="syncState === 'error'" class="state-banner state-error" role="alert"><strong>讀取失敗</strong><span>{{ errorMessage }}</span><button class="button button-secondary" type="button" @click="void refresh()">重試</button></div>
        <div v-if="data.pricing.unknownCount > 0" class="state-banner state-info" role="status"><strong>未知價格 {{ data.pricing.unknownCount }} 筆</strong><span>找不到有效的歷史價格時保留未知，不以推估值替代</span><button class="button button-ghost" type="button" @click="inspectorTab = 'stats'">查看定價</button></div>

        <div class="workspace-heading">
          <div><span class="eyebrow">OVERVIEW / {{ data.pricing.version }}</span><h2 id="workspace-heading">效率比較矩陣</h2><p>{{ dateRange.startDate }} 至 {{ dateRange.endDate }} · 來源時區保留於事件層</p></div>
          <div class="workspace-actions"><button class="button button-primary" type="button" @click="void refresh()">套用範圍</button><span class="mono generated-at">snapshot {{ data.generatedAt.replace('T', ' ').replace('Z', ' UTC') }}</span></div>
        </div>

        <div v-if="syncState === 'loading'" class="loading-grid" aria-label="正在載入 dashboard"><div v-for="index in 4" :key="index" class="skeleton"></div></div>
        <div v-else-if="syncState === 'empty'" class="empty-state"><span class="eyebrow">NO LOCAL EVENTS</span><h3>目前日期範圍沒有事件</h3><p>調整日期或來源篩選，或從左側匯入 JSON / CSV 來源</p><button class="button button-primary" type="button" @click="applyPreset('30')">回到最近 30 天</button></div>
        <template v-else>
          <section class="kpi-grid" aria-label="總覽指標">
            <article class="kpi-panel"><span class="eyebrow">TOTAL TOKENS</span><strong>{{ formatNumber(totalTokenCount) }}</strong><span class="kpi-meta">輸入、輸出與快取合計</span></article>
            <article class="kpi-panel"><span class="eyebrow">EST. COST</span><strong>{{ formatUsd(totalCost) }}</strong><span class="kpi-meta">USD · price {{ data.pricing.version }}</span></article>
            <article class="kpi-panel"><span class="eyebrow">SESSIONS</span><strong>{{ totalSessions }}</strong><span class="kpi-meta">overview 全域總覽 · {{ visibleSessions.length }} 筆載入</span></article>
            <article class="kpi-panel"><span class="eyebrow">CACHE HIT</span><strong>{{ averageCache === null ? '未知' : `${Math.round(averageCache * 100)}%` }}</strong><span class="kpi-meta">僅以有快取事件計算</span></article>
          </section>

          <div class="evidence-grid">
            <section class="panel daily-panel" aria-labelledby="daily-heading"><div class="panel-header"><div><span class="eyebrow">DAILY STATISTICS</span><h3 id="daily-heading">日統計</h3></div><span class="badge badge-neutral">{{ data.daily.length }} 日</span></div><div class="chart-wrap"><svg class="daily-chart" viewBox="0 0 620 180" role="img" aria-label="每日 token 使用量折線圖"><line x1="24" y1="150" x2="600" y2="150" class="chart-rule" /><polyline :points="data.daily.map((day, index) => `${28 + index * (560 / Math.max(data.daily.length - 1, 1))},${150 - (day.tokens / maxDailyTokens) * 112}`).join(' ')" class="chart-line" /><circle v-for="(day, index) in data.daily" :key="day.date" :cx="28 + index * (560 / Math.max(data.daily.length - 1, 1))" :cy="150 - (day.tokens / maxDailyTokens) * 112" r="3" class="chart-point" /></svg><div class="chart-labels"><button v-for="day in data.daily" :key="day.date" type="button" :class="{ selected: selectedDate === day.date }" @click="selectDate(day.date)">{{ formatDateLabel(day.date) }}</button></div></div><div class="stat-strip"><span><b>{{ formatNumber(data.daily.reduce((sum, item) => sum + item.tokens, 0)) }}</b> tokens</span><span><b>{{ formatNumber(data.daily.reduce((sum, item) => sum + item.sessions, 0)) }}</b> sessions</span><span><b>{{ formatUsd(data.daily.find((item) => item.costUsd === null)?.costUsd ?? data.daily.reduce<number>((sum, item) => sum + (item.costUsd ?? 0), 0)) }}</b> cost</span></div></section>
            <section class="panel heatmap-panel" aria-labelledby="heatmap-heading"><div class="panel-header"><div><span class="eyebrow">ACTIVITY MAP</span><h3 id="heatmap-heading">日期熱力圖</h3></div><span class="mono">tokens / day</span></div><div class="heatmap" role="grid" aria-label="每日 token 熱力圖"><button v-for="day in heatmapDays" :key="day.date" class="heatmap-cell" :class="[`intensity-${day.intensity}`, { selected: selectedDate === day.date }]" type="button" role="gridcell" :aria-label="`${day.date} ${formatNumber(day.tokens)} tokens`" @click="selectDate(day.date)"><span>{{ new Date(`${day.date}T00:00:00`).getDate() }}</span></button></div><div class="heatmap-legend"><span>少</span><i v-for="level in 5" :key="level" :class="`intensity-${level}`"></i><span>多</span></div><div class="panel-footnote">選取日期會將事件日期加入全文搜尋條件</div></section>
          </div>

          <section class="panel comparison-panel" aria-labelledby="comparison-heading"><div class="panel-header"><div><span class="eyebrow">MODEL / TOOL COMPARISON</span><h3 id="comparison-heading">比較矩陣</h3></div><span class="badge badge-neutral">{{ data.comparisons.length }} rows</span></div><div class="table-scroll"><table><caption class="sr-only">模型與工具 token 消耗比較</caption><thead><tr><th scope="col">名稱</th><th scope="col">類型</th><th scope="col">Tokens</th><th scope="col">Sessions</th><th scope="col">Avg / session</th><th scope="col">Cost</th><th scope="col">Cache hit</th></tr></thead><tbody><tr v-for="row in data.comparisons" :key="`${row.kind}-${row.name}`"><th scope="row" class="name-cell">{{ row.name }}</th><td><span class="badge badge-neutral">{{ row.kind === 'model' ? '模型' : '工具' }}</span></td><td class="mono">{{ formatNumber(row.tokens) }}</td><td class="mono">{{ row.sessions }}</td><td class="mono">{{ formatNumber(row.averageTokens) }}</td><td class="mono" :class="{ unknown: row.costUsd === null }">{{ formatUsd(row.costUsd) }}</td><td class="mono">{{ row.cacheHitRate === null ? '未知' : `${Math.round(row.cacheHitRate * 100)}%` }}</td></tr></tbody></table></div></section>

          <section class="panel sessions-panel" aria-labelledby="sessions-heading"><div class="panel-header"><div><span class="eyebrow">SESSION LEDGER</span><h3 id="sessions-heading">Session ledger</h3></div><div class="search-control"><label class="sr-only" for="full-search">搜尋 Session、Turn、Prompt、Response、tool</label><input id="full-search" v-model="searchTerm" type="search" placeholder="搜尋全文" @input="void runSearch()" /><kbd>Ctrl K</kbd></div></div><div v-if="searchTerm" class="search-results" aria-live="polite"><span class="eyebrow">SEARCH RESULTS</span><span>{{ searchError || `${searchResults.length} 筆相符` }}</span><button v-for="result in searchResults" :key="result.itemId" class="result-link" type="button" @click="result.sessionId && selectSession(data.sessions.find((session) => session.id === result.sessionId)!)">{{ result.title }}</button></div><div class="session-list"><button v-for="session in visibleSessions" :key="session.id" class="session-row" :class="{ selected: selectedSessionId === session.id }" type="button" @click="selectSession(session)"><span class="session-main"><strong>{{ session.title }}</strong><span>{{ session.source }} · {{ session.model }}</span></span><span class="session-meta"><span class="mono">{{ formatNumber(totalTokens(session.tokens)) }}</span><span>{{ eventCount(session) }} events</span><span class="mono" :class="{ unknown: session.costUsd === null }">{{ formatUsd(session.costUsd) }}</span></span></button></div></section>

          <section v-if="selectedSession" class="panel timeline-panel" aria-labelledby="timeline-heading"><div class="panel-header"><div><span class="eyebrow">SESSION · TURN · EVENT</span><h3 id="timeline-heading">{{ selectedSession.title }}</h3><p>{{ selectedSession.startedAt }} — {{ selectedSession.endedAt }} · {{ selectedSession.source }} · {{ selectedSession.model }}</p></div><div class="tag-list"><span v-for="tag in selectedSession.tags" :key="tag" class="tag">{{ tag }} <button type="button" :aria-label="`移除標籤 ${tag}`" @click="removeTag(tag)">移除</button></span></div></div><div class="timeline"><div v-for="turn in selectedSession.turns" :key="turn.id" class="turn-block"><div class="turn-label"><span class="turn-number">{{ turn.number }}</span><span>Turn {{ turn.number }}</span><span class="mono">{{ formatNumber(totalTokens(turn.tokens)) }} tokens</span></div><div class="event-list"><button v-for="event in turn.events" :key="event.id" class="event-row" :class="[eventClass(event.kind), { selected: selectedEventId === event.id }]" type="button" @click="selectedEventId = event.id"><span class="event-kind">{{ event.label }}</span><span class="event-summary">{{ event.summary }}</span><span class="mono">{{ event.tokens ? formatNumber(event.tokens) : '—' }}</span></button></div></div></div><div v-if="selectedEvent" class="event-detail"><span class="eyebrow">EVENT DETAIL</span><strong>{{ selectedEvent.label }} · {{ selectedEvent.timestamp }}</strong><p>{{ selectedEvent.detail ?? selectedEvent.summary }}</p></div><div class="tag-editor"><label for="tag-input">新增 tag<input id="tag-input" v-model="tagInput" placeholder="例如 review" @keyup.enter="addTag" /></label><label>Scope<select v-model="tagScope"><option value="session">Session</option><option value="project">Project</option><option value="source">Source</option></select></label><label>Entity target<input v-model="tagEntityId" :placeholder="tagScope === 'session' ? selectedSession.id : 'source-or-project-id'" /></label><label>Value<input v-model="tagValue" placeholder="可選值" /></label><button class="button button-secondary" type="button" @click="addTag">加入</button></div></section>
          <section class="panel monthly-panel" aria-labelledby="monthly-heading"><div class="panel-header"><div><span class="eyebrow">MONTHLY ROLLUP</span><h3 id="monthly-heading">月統計</h3></div><span class="mono">UTC month boundary</span></div><div class="monthly-list"><div v-for="month in data.monthly" :key="month.date" class="monthly-row"><strong>{{ month.date.slice(0, 7) }}</strong><span class="mono">{{ formatNumber(month.tokens) }} tokens</span><span>{{ month.sessions }} sessions</span><span class="mono" :class="{ unknown: month.costUsd === null }">{{ formatUsd(month.costUsd) }}</span><span>{{ month.cacheHitRate === null ? '未知快取' : `${Math.round(month.cacheHitRate * 100)}% cache` }}</span></div></div></section>
        </template>
      </main>

      <aside class="inspector" aria-label="選取資料檢視器">
        <div class="inspector-heading"><span class="eyebrow">RIGHT INSPECTOR</span><h2>{{ selectedSession ? 'Selected session' : 'Data inspector' }}</h2><p>{{ selectedSession ? selectedSession.id : '選取一筆 Session 查看詳細資料' }}</p></div>
        <nav class="inspector-tabs" aria-label="檢視器分頁"><button v-for="tab in ([['detail', 'Detail'], ['stats', 'Stat'], ['capabilities', 'Capabilities']] as const)" :key="tab[0]" type="button" :class="{ active: inspectorTab === tab[0] }" @click="inspectorTab = tab[0]">{{ tab[1] }}</button></nav>
        <div v-if="inspectorTab === 'detail'" class="inspector-content"><template v-if="selectedSession"><section class="inspector-section"><span class="eyebrow">TOKEN DETAIL</span><h3>{{ selectedSession.model }}</h3><dl class="detail-list"><div v-for="([tokenType, count]) in Object.entries(selectedSession.tokens)" :key="tokenType"><dt>{{ tokenType }}</dt><dd class="mono">{{ formatNumber(count) }}</dd></div><div><dt>Total</dt><dd class="mono">{{ formatNumber(totalTokens(selectedSession.tokens)) }}</dd></div><div><dt>Cost</dt><dd class="mono" :class="{ unknown: selectedSession.costUsd === null }">{{ formatUsd(selectedSession.costUsd) }}</dd></div></dl></section><section class="inspector-section"><span class="eyebrow">SOURCE CONTEXT</span><p>{{ selectedSession.source }} · {{ selectedSession.tool }}</p><p class="mono">Started {{ selectedSession.startedAt }}</p><p class="mono">Ended {{ selectedSession.endedAt }}</p></section></template><div v-else class="inspector-empty">從 Session ledger 選取資料後，這裡會顯示 token breakdown 與來源時間</div></div>
        <div v-else-if="inspectorTab === 'stats'" class="inspector-content"><section class="inspector-section"><span class="eyebrow">PRICING VERSION</span><h3>{{ data.pricing.version }}</h3><dl class="detail-list"><div><dt>Effective from</dt><dd class="mono">{{ data.pricing.effectiveFrom }}</dd></div><div><dt>Unknown price</dt><dd class="unknown mono">{{ data.pricing.unknownCount }}</dd></div><div><dt>Overrides</dt><dd class="mono">{{ data.pricing.overrideCount }}</dd></div></dl></section><section class="inspector-section"><span class="eyebrow">PRICE OVERRIDE</span><label>Provider<input v-model="pricingProvider" placeholder="openai" /></label><label>Model<input v-model="pricingModel" placeholder="gpt-5-codex" /></label><label>Token type<select v-model="pricingTokenType"><option v-for="tokenType in data.tokenTypes" :key="tokenType" :value="tokenType">{{ tokenType }}</option></select></label><label>Mode<input v-model="pricingMode" placeholder="standard" /></label><label>USD / MTok<input v-model="pricingAmount" inputmode="decimal" placeholder="3.00" /></label><label>Effective from<input v-model="pricingEffectiveFrom" type="date" /></label><label>Effective to<input v-model="pricingEffectiveTo" type="date" /></label><label>Min input tokens<input v-model="pricingMinimum" inputmode="numeric" /></label><label>Max input tokens<input v-model="pricingMaximum" inputmode="numeric" placeholder="不限" /></label><button class="button button-primary button-full" type="button" @click="void savePricing()">儲存 override</button><p class="rail-note">有效區間採半開區間；未知價格不會被推估</p></section></div>
        <div v-else class="inspector-content"><section class="inspector-section"><span class="eyebrow">CAPABILITY MAP</span><h3>目前可用能力</h3><ul class="capability-list"><li v-for="capability in data.capabilities" :key="capability">{{ capability }}</li></ul></section><section class="inspector-section"><span class="eyebrow">TAG MANAGEMENT</span><label>Scope<select v-model="tagScope"><option value="session">Session</option><option value="project">Project</option><option value="source">Source</option></select></label><label>Entity target<input v-model="tagEntityId" :placeholder="tagScope === 'session' ? selectedSession?.id ?? 'session-id' : 'source-or-project-id'" /></label><label>Tag key<input id="tag-management-input" v-model="tagInput" placeholder="例如 review" @keyup.enter="addTag" /></label><label>Value<input v-model="tagValue" placeholder="可選值" /></label><button class="button button-secondary" type="button" @click="addTag">新增 tag</button><div class="tag-assignment-list"><div v-for="assignment in data.tags" :key="`${assignment.scope}-${assignment.entityId}-${assignment.id || assignment.key}`" class="tag-assignment"><span class="tag tag-neutral">{{ assignment.key }}<span v-if="assignment.value">={{ assignment.value }}</span></span><span class="mono">{{ assignment.scope }} / {{ assignment.entityId }}</span><button type="button" :aria-label="`刪除 ${assignment.key} tag`" @click="removeAssignment(assignment)">刪除</button></div><span v-if="!data.tags.length" class="rail-note">目前沒有 tag assignment</span></div></section><section class="inspector-section"><span class="eyebrow">TAGS</span><div class="tag-list"><span v-for="tag in allTags" :key="tag" class="tag tag-neutral">{{ tag }}</span></div></section></div>
        <div class="inspector-footer"><span class="eyebrow">SESSION STORAGE</span><p>Startup fragment key 讀取後立即移除，API 只使用 <code>X-Token-Dashboard-Key</code></p></div>
      </aside>
    </div>

    <dialog v-if="pendingExport" ref="exportDialog" class="confirm-dialog" aria-labelledby="export-heading" @cancel="cancelDialog('export', $event)" @close="finalizeDialog('export')"><div class="dialog-panel"><span class="eyebrow">CONTENT EXPORT</span><h2 id="export-heading">匯出完整內容？</h2><p>此 {{ pendingExport.toUpperCase() }} 檔案會包含 prompt、response 與 tool payload，可能含敏感內容。</p><div class="dialog-actions"><button class="button button-secondary" type="button" data-autofocus @click="closeDialog('export')">取消</button><button class="button button-primary" type="button" @click="void performExport(pendingExport)">確認匯出</button></div></div></dialog>
    <dialog v-if="showDeleteConfirm" ref="deleteDialog" class="confirm-dialog" aria-labelledby="delete-heading" @cancel="cancelDialog('delete', $event)" @close="finalizeDialog('delete')"><div class="dialog-panel"><span class="eyebrow">DESTRUCTIVE ACTION</span><h2 id="delete-heading">刪除所有本機資料？</h2><p>這會刪除 Session、Turn、事件、tag 與搜尋索引。匯出備份無法在此操作後自動恢復。</p><div class="dialog-actions"><button class="button button-secondary" type="button" data-autofocus @click="closeDialog('delete')">取消</button><button class="button button-danger" type="button" @click="void confirmDelete()">確認刪除</button></div></div></dialog>
  </div>
</template>
