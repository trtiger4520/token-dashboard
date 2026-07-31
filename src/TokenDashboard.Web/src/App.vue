<script setup lang="ts">
/*
THESIS: Treat the dashboard as a working blueprint margin, not a card gallery; the comparison is the primary object
OWN-WORLD: Neutral paper surfaces, 1px hairlines, Inter data labels, JetBrains Mono measurements, and schematic blue only for action
STORY: A developer starts with a date and source, compares token efficiency, then opens a session down to its event evidence
FIRST VIEWPORT: The left rail fixes scope, the center places KPIs and comparison evidence, and the right rail holds the selected record
FORM: Operate-mode three-column control rail / comparison matrix / inspector, inherited from the route dashboard surface brief
*/
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { extractStartupKey, TokenDashboardClient, type SourceDiscoveryResult, type SyncRequest } from './api'
import { isValidDateRange, resolveDateRange, resolveDayRange, type DatePreset } from './dateRange'
import { cacheTokenCount, createEmptyDashboardData, formatDateLabel, formatNumber, formatTokenCount, formatUsd, inputTokenCount, outputTokenCount, totalTokens, type Budget, type BudgetSummary, type DashboardData, type DashboardQuery, type EventKind, type PricingEntry, type SavedView, type SearchResult, type SessionRecord, type TagRecord, type TokenType, type TrendPoint, type UnknownPricing } from './types'
import { layoutTreemap, type TreemapRect } from './treemap'

const client = new TokenDashboardClient()
const data = ref<DashboardData>(createEmptyDashboardData())
const unknownPricing = ref<UnknownPricing[]>([])
const controlRailOpen = ref(false)
const syncState = ref<'loading' | 'ready' | 'empty' | 'error' | 'partial'>('loading')
const errorMessage = ref('')
const operationMessage = ref('')
const selectedSessionId = ref('')
const selectedEventId = ref('')
const revealedEventFields = reactive<Record<string, string>>({})
const revealingEventFields = reactive<Record<string, boolean>>({})
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
const pricingSearchTerm = ref('')
const pricingProviderFilter = ref('all')
const pricingModeFilter = ref('all')
const pricingTokenTypeFilter = ref('all')
const selectedPricingKeys = ref<string[]>([])
const mergePricingConfirmation = ref(false)
const mergingPricing = ref(false)
const showDeleteConfirm = ref(false)
const pendingExport = ref<'json' | 'sqlite' | null>(null)
const deleteDialog = ref<HTMLDialogElement | null>(null)
const exportDialog = ref<HTMLDialogElement | null>(null)
const lastDialogTrigger = ref<HTMLElement | null>(null)
const isDark = ref(false)
const routePath = ref(window.location.pathname)
let routerPush: ((to: string) => Promise<unknown>) | undefined
try {
  const route = useRoute()
  const router = useRouter()
  routePath.value = route.path
  routerPush = async (to: string) => {
    routePath.value = to
    return router.push(to)
  }
} catch {
  // App is also mounted directly by unit tests without a router plugin
}
const currentRoute = computed(() => routePath.value === '/pricing' ? '/pricing' : '/dashboard')
const selectedDate = ref('')
const datePreset = ref<DatePreset>('30')
const dateRange = reactive(resolveDateRange('30'))
const filters = reactive({ sourceId: 'all', tool: 'all', model: 'all', tokenType: 'all' as TokenType | 'all' })
const projectFilter = ref('all')
const tagFilter = ref('all')
const savedViews = ref<SavedView[]>([])
const selectedViewName = ref('')
const viewName = ref('')
const budgets = ref<Budget[]>([])
const budgetSummaries = ref<BudgetSummary[]>([])
const budgetName = ref('')
const budgetAmount = ref('')
const budgetPeriod = ref<'daily' | 'monthly' | 'custom'>('monthly')
const budgetFromDate = ref('')
const budgetToDate = ref('')
const budgetProjectId = ref('')
const budgetTag = ref('')
const editingBudgetId = ref('')
type TrendMetric = 'cost' | 'tokens'
type TrendGranularity = 'daily' | 'weekly' | 'monthly'

function readDashboardPreference<T extends string>(key: string, fallback: T, allowed: readonly T[]): T {
  const value = window.localStorage.getItem(`token-dashboard.${key}`)
  return value !== null && allowed.includes(value as T) ? value as T : fallback
}

const trendMetric = ref<TrendMetric>(readDashboardPreference('trend-metric', 'cost', ['cost', 'tokens']))
const trendGranularity = ref<TrendGranularity>(readDashboardPreference('trend-granularity', 'daily', ['daily', 'weekly', 'monthly']))

const selectedSession = computed<SessionRecord | undefined>(() => data.value.sessions.find((session) => session.id === selectedSessionId.value))
const selectedEvent = computed(() => selectedSession.value?.turns.flatMap((turn) => turn.events).find((event) => event.id === selectedEventId.value))
const allTags = computed(() => [...new Set(data.value.tags.map((tag) => tag.key).concat(data.value.sessions.flatMap((session) => session.tags)))].sort())
const projectOptions = computed(() => [...new Set(data.value.sessions.map((session) => session.workspaceId).filter((value): value is string => Boolean(value)))].sort())
const visibleSessions = computed(() => [...data.value.sessions].sort((left, right) => (right.partialCostUsd ?? 0) - (left.partialCostUsd ?? 0)))
const totalTokenCount = computed(() => totalTokens(data.value.overview.tokenCounts))
const totalCost = computed(() => data.value.overview.costUsd)
const totalSessions = computed(() => data.value.overview.uniqueSessionCount)
const averageCache = computed(() => data.value.overview.cacheHitRate)
const displayedTrend = computed<TrendPoint[]>(() => trendGranularity.value === 'monthly'
  ? data.value.monthly.map((point) => ({ ...point, bucketStartUtc: `${point.date}-01T00:00:00`, bucketEndUtc: '' }))
  : data.value.trend)
const trendValue = (point: TrendPoint): number => trendMetric.value === 'cost' ? point.costUsd ?? point.partialCostUsd : point.tokens
const maxTrendValue = computed(() => Math.max(...displayedTrend.value.map(trendValue), 1))
const maxTrendTokens = computed(() => Math.max(...data.value.trend.map((point) => point.tokens), 1))
const heatmapDays = computed(() => data.value.heatmap.map((day) => ({ ...day, intensity: Math.max(1, Math.ceil((day.tokens / maxTrendTokens.value) * 5)) })))
const treemapContainer = ref<HTMLElement | null>(null)
const treemapSize = reactive({ width: 640, height: 360 })
const treemapViewBox = computed(() => `0 0 ${treemapSize.width} ${treemapSize.height}`)
const treemapRects = computed<TreemapRect[]>(() => layoutTreemap(data.value.comparisonTree, treemapSize.width, treemapSize.height))
const selectedTreemapRect = ref<TreemapRect | null>(null)
let treemapResizeObserver: ResizeObserver | null = null
const sourceStatus = computed(() => discoveredSources.value.length ? `已檢查 ${discoveredSources.value.length} 個 adapter` : data.value.sources.length ? `已載入 ${data.value.sources.length} 個來源` : '來源未提供')
const officialPricingEntries = computed(() => data.value.pricing.entries.filter((entry) => !entry.isOverride))
const overridePricingEntries = computed(() => data.value.pricing.entries.filter((entry) => entry.isOverride))
const pricingModelCount = computed(() => new Set(officialPricingEntries.value.map((entry) => entry.model)).size)
const pricingProviders = computed(() => [...new Set(officialPricingEntries.value.map((entry) => entry.provider))].sort())
const pricingModes = computed(() => [...new Set(officialPricingEntries.value.map((entry) => entry.mode))].sort())
const pricingTokenTypes = computed(() => [...new Set(officialPricingEntries.value.map((entry) => entry.tokenType))].sort())
const suggestedUnknownPricing = computed(() => unknownPricing.value.filter((entry) => entry.suggestion != null))
const selectedPricingSuggestions = computed(() => suggestedUnknownPricing.value.filter((entry) => selectedPricingKeys.value.includes(unknownPricingKey(entry))))
const allPricingSuggestionsSelected = computed(() => suggestedUnknownPricing.value.length > 0 && selectedPricingSuggestions.value.length === suggestedUnknownPricing.value.length)
const visibleOfficialPricing = computed(() => {
  const query = pricingSearchTerm.value.trim().toLowerCase()
  return officialPricingEntries.value
    .filter((entry) => pricingProviderFilter.value === 'all' || entry.provider === pricingProviderFilter.value)
    .filter((entry) => pricingModeFilter.value === 'all' || entry.mode === pricingModeFilter.value)
    .filter((entry) => pricingTokenTypeFilter.value === 'all' || entry.tokenType === pricingTokenTypeFilter.value)
    .filter((entry) => !query || `${entry.provider} ${entry.model} ${entry.mode} ${entry.tokenType}`.toLowerCase().includes(query))
    .sort((left, right) => `${left.provider}-${left.model}-${left.mode}-${left.tokenType}`.localeCompare(`${right.provider}-${right.model}-${right.mode}-${right.tokenType}`))
})

function pricingModeLabel(mode: string): string {
  return ({ standard: 'Standard', batch: 'Batch', flex: 'Flex', priority: 'Priority', 'long-context-1m': 'Long context', 'batch-long-context-1m': 'Batch · long context' } as Record<string, string>)[mode] ?? mode
}

function pricingTokenLabel(tokenType: string): string {
  return ({ input: 'Input', 'cached-input': 'Cached input', 'cache-write': 'Cache write', 'cache-write-5m': 'Cache write · 5m', 'cache-write-1h': 'Cache write · 1h', 'cache-read': 'Cache read', output: 'Output' } as Record<string, string>)[tokenType] ?? tokenType
}

function pricingLimitLabel(value: number | null): string {
  return value === null ? '不限' : formatNumber(value)
}

function pricingSourceLabel(entry: PricingEntry): string {
  return entry.sourceName || (entry.isOverride ? '本機覆寫' : '官方 catalog')
}

function unknownPricingKey(entry: UnknownPricing): string {
  return `${entry.provider}|${entry.model}|${entry.mode}|${entry.tokenType}`
}

function togglePricingSuggestion(entry: UnknownPricing): void {
  const key = unknownPricingKey(entry)
  selectedPricingKeys.value = selectedPricingKeys.value.includes(key)
    ? selectedPricingKeys.value.filter((item) => item !== key)
    : [...selectedPricingKeys.value, key]
}

function toggleAllPricingSuggestions(): void {
  selectedPricingKeys.value = allPricingSuggestionsSelected.value ? [] : suggestedUnknownPricing.value.map(unknownPricingKey)
  mergePricingConfirmation.value = false
}

async function mergeSelectedPricingSuggestions(): Promise<void> {
  const entries = selectedPricingSuggestions.value
  if (!entries.length) return
  mergingPricing.value = true
  let merged = 0
  try {
    for (const entry of entries) {
      const suggestion = entry.suggestion
      if (!suggestion) continue
      await client.updatePricing({
        provider: entry.provider,
        model: entry.model,
        mode: entry.mode,
        tokenType: entry.tokenType,
        usdPerMillionTokens: suggestion.usdPerMillionTokens,
        minimumInputTokens: suggestion.minimumInputTokens,
        maximumInputTokens: suggestion.maximumInputTokens,
        effectiveFromUtc: entry.earliestEventUtc,
        sourceName: `官方最新價格 · ${suggestion.catalogModel}`,
        sourceUrl: suggestion.sourceUrl
      })
      merged += 1
    }
    selectedPricingKeys.value = []
    mergePricingConfirmation.value = false
    operationMessage.value = `已整合 ${merged} 筆官方價格建議，建立本機 override`
    await refresh()
  } catch (error) {
    operationMessage.value = merged > 0 ? `已整合 ${merged} 筆，後續整合失敗` : error instanceof Error ? error.message : '價格整合失敗'
    await refresh()
  } finally {
    mergingPricing.value = false
  }
}

function navigate(route: '/dashboard' | '/pricing'): void {
  if (routerPush) void routerPush(route)
  else {
    window.history.pushState({}, '', route)
    routePath.value = route
  }
}

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
    tokenType: filters.tokenType === 'all' ? undefined : filters.tokenType,
    projectId: projectFilter.value === 'all' ? undefined : projectFilter.value,
    tag: tagFilter.value === 'all' ? undefined : tagFilter.value,
    trendInterval: trendGranularity.value === 'weekly' ? '7d' : '1d'
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

function jumpToDay(offset: number): void {
  const base = offset === 0 ? new Date() : new Date(`${dateRange.endDate}T00:00:00`)
  Object.assign(dateRange, resolveDayRange(offset, base))
  datePreset.value = 'custom'
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
    try {
      const unknownResult = await client.unknownPricing(dashboardQuery())
      const nextUnknownPricing = Array.isArray(unknownResult) ? unknownResult as UnknownPricing[] : []
      unknownPricing.value = nextUnknownPricing
      const availableKeys = new Set(nextUnknownPricing.map(unknownPricingKey))
      selectedPricingKeys.value = selectedPricingKeys.value.filter((key) => availableKeys.has(key))
    } catch {
      unknownPricing.value = []
    }
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

async function revealEventField(field: 'prompt' | 'response' | 'payload'): Promise<void> {
  const sessionId = selectedSession.value?.id
  const eventId = selectedEvent.value?.id
  if (!sessionId || !eventId) return
  const key = `${eventId}:${field}`
  if (revealedEventFields[key] !== undefined) {
    delete revealedEventFields[key]
    return
  }
  revealingEventFields[key] = true
  try {
    const result = await client.revealEventField(sessionId, eventId, field)
    revealedEventFields[key] = result.content
  } catch (error) {
    operationMessage.value = error instanceof Error ? error.message : '事件內容讀取失敗'
  } finally {
    revealingEventFields[key] = false
  }
}

function selectDate(date: string): void {
  selectedDate.value = date
  const day = (data.value.heatmap.length ? data.value.heatmap : data.value.daily).find((item) => item.date === date)
  if (day) searchTerm.value = day.date
}

function trendLabel(value: string): string {
  if (trendGranularity.value === 'monthly') return value.slice(0, 7)
  return new Intl.DateTimeFormat('zh-TW', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

function costCoverageLabel(coverage: number | null | undefined): string {
  return coverage === null || coverage === undefined ? '覆蓋率未知' : `覆蓋 ${Math.round(coverage * 100)}%`
}

function trendValueLabel(point: TrendPoint): string {
  if (trendMetric.value === 'tokens') return `${formatTokenCount(point.tokens)} Token`
  const cost = point.costUsd ?? point.partialCostUsd
  return `${point.costUsd === null ? '已知 ' : ''}${formatUsd(cost)} · ${costCoverageLabel(point.costCoverage)}`
}

function sessionCostLabel(session: SessionRecord): string {
  if (session.costUsd !== null) return formatUsd(session.costUsd)
  if (session.costCoverage === null || session.costCoverage === undefined) return '成本未知'
  return `已知 ${formatUsd(session.partialCostUsd ?? 0)} · ${costCoverageLabel(session.costCoverage)}`
}

function formatSessionStartedAt(value: string): string {
  return new Intl.DateTimeFormat('zh-TW', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

function tokenTitle(value: number): string {
  return `${value.toLocaleString('en-US')} tokens`
}

function treemapLabel(rect: TreemapRect): string {
  return `${rect.node.name} · ${formatTokenCount(rect.node.tokens)} tokens · ${formatUsd(rect.node.costUsd)}`
}

function selectTreemapRect(rect: TreemapRect): void {
  selectedTreemapRect.value = rect
}

function updateTreemapSize(): void {
  const container = treemapContainer.value
  if (!container) return
  const bounds = container.getBoundingClientRect()
  if (bounds.width > 0) treemapSize.width = Math.round(bounds.width)
  if (bounds.height > 0) treemapSize.height = Math.round(bounds.height)
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

function currentSavedView(): SavedView {
  return {
    name: viewName.value.trim(),
    query: {
      preset: datePreset.value === '7' ? '7d' : datePreset.value === '90' ? '90d' : datePreset.value === 'custom' ? 'custom' : datePreset.value,
      from: dateRange.startDate,
      to: dateRange.endDate,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
      sourceId: filters.sourceId === 'all' ? undefined : filters.sourceId,
      tool: filters.tool === 'all' ? undefined : filters.tool,
      model: filters.model === 'all' ? undefined : filters.model,
      tokenType: filters.tokenType === 'all' ? undefined : filters.tokenType,
      projectId: projectFilter.value === 'all' ? undefined : projectFilter.value,
      tag: tagFilter.value === 'all' ? undefined : tagFilter.value
    },
    trendMetric: trendMetric.value,
    trendGranularity: trendGranularity.value
  }
}

function persistSavedViews(): void {
  window.localStorage.setItem('token-dashboard.saved-views', JSON.stringify(savedViews.value))
}

function saveView(overwrite = false): void {
  const next = currentSavedView()
  if (!next.name) {
    operationMessage.value = '請提供檢視名稱'
    return
  }
  const existing = savedViews.value.findIndex((view) => view.name === next.name)
  if (existing >= 0 && !overwrite) {
    operationMessage.value = '檢視名稱已存在，請選擇覆寫'
    return
  }
  if (existing >= 0) savedViews.value.splice(existing, 1, next)
  else savedViews.value.push(next)
  persistSavedViews()
  selectedViewName.value = next.name
  operationMessage.value = existing >= 0 ? `檢視「${next.name}」已覆寫` : `檢視「${next.name}」已建立`
}

function applySavedView(view: SavedView | undefined): void {
  if (!view) return
  const query = view.query
  datePreset.value = query.preset === '7d' ? '7' : query.preset === '90d' ? '90' : query.preset === 'custom' ? 'custom' : query.preset as DatePreset
  dateRange.startDate = query.from
  dateRange.endDate = query.to
  filters.sourceId = query.sourceId ?? 'all'
  filters.tool = query.tool ?? 'all'
  filters.model = query.model ?? 'all'
  filters.tokenType = (query.tokenType ?? 'all') as TokenType | 'all'
  projectFilter.value = query.projectId ?? 'all'
  tagFilter.value = query.tag ?? 'all'
  trendMetric.value = view.trendMetric
  trendGranularity.value = view.trendGranularity
  selectedViewName.value = view.name
  viewName.value = view.name
  void refresh()
}

function deleteSavedView(name: string): void {
  savedViews.value = savedViews.value.filter((view) => view.name !== name)
  persistSavedViews()
  if (selectedViewName.value === name) selectedViewName.value = ''
  operationMessage.value = `檢視「${name}」已刪除`
}

function loadSavedViews(): void {
  const raw = window.localStorage.getItem('token-dashboard.saved-views')
  if (!raw) return
  try {
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) throw new Error('invalid saved views')
    savedViews.value = parsed.filter((view): view is SavedView => view && typeof view.name === 'string' && view.query && typeof view.query.from === 'string')
  } catch {
    savedViews.value = []
    window.localStorage.removeItem('token-dashboard.saved-views')
    operationMessage.value = '已清除損毀的檢視設定'
  }
}

function editBudget(budget: Budget): void {
  editingBudgetId.value = budget.id
  budgetName.value = budget.name
  budgetAmount.value = String(budget.amountUsd)
  budgetPeriod.value = budget.period === 'daily' || budget.period === 'custom' ? budget.period : 'monthly'
  budgetFromDate.value = budget.fromDate
  budgetToDate.value = budget.toDate ?? ''
  budgetProjectId.value = budget.projectId ?? ''
  budgetTag.value = budget.tag ?? ''
}

function resetBudgetForm(): void {
  editingBudgetId.value = ''
  budgetName.value = ''
  budgetAmount.value = ''
  budgetPeriod.value = 'monthly'
  budgetFromDate.value = ''
  budgetToDate.value = ''
  budgetProjectId.value = ''
  budgetTag.value = ''
}

async function saveBudget(): Promise<void> {
  const name = budgetName.value.trim()
  const amountUsd = Number(budgetAmount.value)
  if (!name || !Number.isFinite(amountUsd) || amountUsd <= 0) {
    operationMessage.value = '請提供有效的預算名稱與金額'
    return
  }
  await client.saveBudget({ id: editingBudgetId.value || undefined, name, amountUsd, period: budgetPeriod.value, fromDate: budgetFromDate.value || dateRange.startDate, toDate: budgetToDate.value || null, projectId: budgetProjectId.value || null, tag: budgetTag.value || null, enabled: true })
  operationMessage.value = editingBudgetId.value ? '預算已更新' : '預算已建立'
  resetBudgetForm()
  await loadBudgets()
}

async function loadBudgets(): Promise<void> {
  try {
    const query = dashboardQuery()
    budgets.value = await client.getBudgets(query)
    budgetSummaries.value = await client.getBudgetSummaries(query)
  } catch {
    budgets.value = []
    budgetSummaries.value = []
  }
}

async function removeBudget(id: string): Promise<void> {
  await client.deleteBudget(id)
  operationMessage.value = '預算已刪除'
  await loadBudgets()
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

function prefillUnknown(entry: UnknownPricing): void {
  pricingProvider.value = entry.provider
  pricingModel.value = entry.model
  pricingMode.value = entry.mode
  pricingTokenType.value = entry.tokenType
  pricingEffectiveFrom.value = entry.earliestEventUtc ? entry.earliestEventUtc.slice(0, 10) : ''
}

function revisePricing(entry: DashboardData['pricing']['entries'][number]): void {
  pricingProvider.value = entry.provider
  pricingModel.value = entry.model
  pricingMode.value = entry.mode
  pricingTokenType.value = entry.tokenType
  pricingAmount.value = String(entry.usdPerMillionTokens)
  pricingEffectiveFrom.value = entry.effectiveFromUtc?.slice(0, 10) ?? ''
  pricingEffectiveTo.value = entry.effectiveToUtc?.slice(0, 10) ?? ''
  pricingMinimum.value = String(entry.minimumInputTokens)
  pricingMaximum.value = entry.maximumInputTokens === null ? '' : String(entry.maximumInputTokens)
}

async function deactivatePricing(provider: string, model: string, tokenType: string, mode: string): Promise<void> {
  await client.deactivatePricing({ provider, model, tokenType, mode })
  operationMessage.value = 'pricing override 已停用，歷史紀錄保留'
  await refresh()
}

function eventClass(kind: EventKind): string {
  return `event-${kind}`
}

function eventCount(session: SessionRecord): number {
  return session.turns.reduce((sum, turn) => sum + turn.events.length, 0)
}

onMounted(() => {
  if (typeof ResizeObserver !== 'undefined') {
    treemapResizeObserver = new ResizeObserver(updateTreemapSize)
  }
  window.addEventListener('resize', updateTreemapSize)
  if (!extractStartupKey()) {
    errorMessage.value = '缺少 localhost session key，請從應用程式入口重新開啟 Dashboard'
    syncState.value = 'error'
    return
  }
  loadSavedViews()
  void refresh().then(() => loadBudgets())
})

watch(treemapContainer, async (container, previousContainer) => {
  if (previousContainer) treemapResizeObserver?.unobserve(previousContainer)
  if (!container) return
  await nextTick()
  updateTreemapSize()
  treemapResizeObserver?.observe(container)
})

watch([trendMetric, trendGranularity], ([metric, granularity]) => {
  window.localStorage.setItem('token-dashboard.trend-metric', metric)
  window.localStorage.setItem('token-dashboard.trend-granularity', granularity)
  if (granularity !== 'monthly') void refresh()
})

onBeforeUnmount(() => {
  treemapResizeObserver?.disconnect()
  window.removeEventListener('resize', updateTreemapSize)
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
        <span class="eyebrow">本機分析</span>
        <span class="topbar-divider" aria-hidden="true"></span>
        <span class="mono">{{ data.overview.timeZoneId || '本機時區' }} / {{ dateRange.startDate }} — {{ dateRange.endDate }}</span>
      </div>
      <nav class="topbar-nav" aria-label="主要導覽"><button type="button" :class="{ active: currentRoute === '/dashboard' }" @click="navigate('/dashboard')">儀錶板</button><button type="button" :class="{ active: currentRoute === '/pricing' }" @click="navigate('/pricing')">定價</button></nav>
      <div class="topbar-actions">
        <span class="sync-indicator" :class="`sync-${syncState}`" role="status">{{ syncState === 'loading' ? '更新資料中' : syncState === 'partial' ? '部分來源未更新' : syncState === 'error' ? '讀取失敗' : syncState === 'empty' ? '沒有資料' : '資料已更新' }}</span>
        <button class="button button-secondary" type="button" @click="void refresh()">重新載入資料</button>
        <button class="button button-icon" type="button" :aria-label="isDark ? '切換為亮色模式' : '切換為暗色模式'" @click="setTheme(!isDark)">{{ isDark ? 'Light' : 'Dark' }}</button>
      </div>
    </header>

    <section v-if="currentRoute === '/pricing'" class="pricing-route" aria-labelledby="pricing-route-heading">
      <div class="route-heading pricing-heading"><div><span class="eyebrow">PRICE GOVERNANCE / API CATALOG</span><h1 id="pricing-route-heading">價格治理</h1><p>用同一套歷史有效區間管理 OpenAI 與 Anthropic 的 API 成本規則，未知價格永遠保持未知</p></div><button class="button button-secondary" type="button" @click="navigate('/dashboard')">返回 Dashboard</button></div>

      <div class="pricing-summary" aria-label="價格 catalog 摘要">
        <article><span class="eyebrow">MODELS</span><strong>{{ pricingModelCount }}</strong><span>官方模型</span></article>
        <article><span class="eyebrow">RATE ROWS</span><strong>{{ officialPricingEntries.length }}</strong><span>輸入、快取與輸出規則</span></article>
        <article><span class="eyebrow">OVERRIDES</span><strong>{{ data.pricing.overrideCount }}</strong><span>本機有效覆寫</span></article>
        <article :class="{ 'summary-warning': unknownPricing.length > 0 }"><span class="eyebrow">UNKNOWN</span><strong>{{ unknownPricing.length }}</strong><span>尚未匹配價格的組合</span></article>
      </div>

      <section class="pricing-toolbar" aria-label="篩選官方價格">
        <div class="pricing-toolbar-heading"><div><span class="eyebrow">OFFICIAL CATALOG</span><h2>官方價格清單</h2></div><span class="catalog-stamp mono">USD / MTok · {{ data.pricing.version || '—' }}</span></div>
        <div class="pricing-filters">
          <label class="pricing-search">搜尋模型或計費項目<input v-model="pricingSearchTerm" type="search" placeholder="例如 gpt-5.6、cache-read" /></label>
          <label>Provider<select v-model="pricingProviderFilter"><option value="all">全部 provider</option><option v-for="provider in pricingProviders" :key="provider" :value="provider">{{ provider }}</option></select></label>
          <label>Mode<select v-model="pricingModeFilter"><option value="all">全部模式</option><option v-for="mode in pricingModes" :key="mode" :value="mode">{{ pricingModeLabel(mode) }}</option></select></label>
          <label>Token type<select v-model="pricingTokenTypeFilter"><option value="all">全部 token type</option><option v-for="tokenType in pricingTokenTypes" :key="tokenType" :value="tokenType">{{ pricingTokenLabel(tokenType) }}</option></select></label>
        </div>
        <p class="pricing-filter-result">顯示 {{ visibleOfficialPricing.length }} / {{ officialPricingEntries.length }} 筆 · 官方來源為唯讀，價格以生效日期與 input token 門檻判定</p>
        <div class="table-scroll pricing-table-scroll"><table class="pricing-table"><caption class="sr-only">官方 API 價格清單</caption><thead><tr><th>Provider</th><th>Model</th><th>Mode</th><th>Token type</th><th>Input min</th><th>Input max</th><th>USD / MTok</th><th>Effective</th><th>Source</th></tr></thead><tbody><tr v-for="entry in visibleOfficialPricing" :key="`official-${entry.provider}-${entry.model}-${entry.mode}-${entry.tokenType}-${entry.effectiveFromUtc}`"><td><span class="provider-mark" :class="`provider-${entry.provider}`">{{ entry.provider === 'openai' ? 'O' : 'A' }}</span>{{ entry.provider }}</td><td class="mono model-cell">{{ entry.model }}</td><td><span class="mode-badge">{{ pricingModeLabel(entry.mode) }}</span></td><td>{{ pricingTokenLabel(entry.tokenType) }}</td><td class="mono">{{ formatNumber(entry.minimumInputTokens) }}</td><td class="mono">{{ pricingLimitLabel(entry.maximumInputTokens) }}</td><td class="mono price-cell">{{ entry.usdPerMillionTokens.toFixed(4) }}</td><td class="mono">{{ entry.effectiveFromUtc }}</td><td><a :href="entry.sourceUrl" target="_blank" rel="noreferrer">{{ pricingSourceLabel(entry) }} ↗</a></td></tr><tr v-if="visibleOfficialPricing.length === 0"><td colspan="9" class="table-empty">沒有符合條件的價格規則</td></tr></tbody></table></div>
      </section>

      <div v-if="unknownPricing.length" class="pricing-unknown"><div class="section-heading-row"><div><span class="eyebrow">UNPRICED COMBINATIONS</span><h2>未知價格組合</h2></div><span class="unknown mono">{{ unknownPricing.length }} 筆</span></div><p>以下組合未套用價格，成本維持未知；可從官方最新 catalog 產生建議，確認後建立本機 override</p><div v-if="suggestedUnknownPricing.length" class="pricing-merge-panel"><div><strong>{{ suggestedUnknownPricing.length }} 筆可使用官方最新價格</strong><span>系統會保留事件原始 model、token type 與 mode，只將建議價格寫入歷史有效區間</span></div><div class="button-row"><button class="button button-secondary" type="button" @click="toggleAllPricingSuggestions">{{ allPricingSuggestionsSelected ? '取消全選' : '全選可整合' }}</button><button class="button button-primary" type="button" :disabled="selectedPricingSuggestions.length === 0" @click="mergePricingConfirmation = true">預覽整合 {{ selectedPricingSuggestions.length }} 筆</button></div></div><div v-if="mergePricingConfirmation" class="pricing-merge-confirm" role="group" aria-label="確認價格整合"><div><strong>確認建立 {{ selectedPricingSuggestions.length }} 筆本機 override？</strong><span>官方 catalog 不會被改寫；整合後會從各組合的首筆事件日期開始套用建議價格</span></div><div class="button-row"><button class="button button-secondary" type="button" :disabled="mergingPricing" @click="mergePricingConfirmation = false">取消</button><button class="button button-primary" type="button" :disabled="mergingPricing" @click="void mergeSelectedPricingSuggestions()">{{ mergingPricing ? '整合中' : '確認整合' }}</button></div></div><div class="table-scroll"><table><thead><tr><th>選取</th><th>Provider</th><th>Model</th><th>Mode</th><th>Token type</th><th>官方最新建議</th><th>首筆事件</th><th>Token 數</th><th></th></tr></thead><tbody><tr v-for="entry in unknownPricing" :key="`${entry.provider}-${entry.model}-${entry.mode}-${entry.tokenType}`"><td><input v-if="entry.suggestion" class="pricing-merge-checkbox" type="checkbox" :checked="selectedPricingKeys.includes(unknownPricingKey(entry))" :aria-label="`選取 ${entry.provider}/${entry.model}/${entry.tokenType}`" @change="togglePricingSuggestion(entry)" /><span v-else>—</span></td><td>{{ entry.provider }}</td><td class="mono">{{ entry.model }}</td><td>{{ entry.mode }}</td><td>{{ entry.tokenType }}</td><td v-if="entry.suggestion" class="pricing-suggestion-cell"><strong class="mono">{{ entry.suggestion.usdPerMillionTokens.toFixed(4) }} USD / MTok</strong><span>{{ entry.suggestion.reason }}</span></td><td v-else class="pricing-suggestion-cell"><span>沒有可安全建議</span></td><td class="mono">{{ entry.earliestEventUtc }}</td><td class="mono">{{ formatNumber(entry.tokenCount) }}</td><td><button class="button button-ghost" type="button" @click="prefillUnknown(entry)">手動建立</button></td></tr></tbody></table></div></div>

      <section class="pricing-editor"><div class="section-heading-row"><div><span class="eyebrow">LOCAL OVERRIDE</span><h2>建立或修訂本機覆寫</h2></div><span class="rail-note">半開有效區間 · 不改寫官方 catalog</span></div><div class="form-grid"><label>Provider<input v-model="pricingProvider" /></label><label>Model<input v-model="pricingModel" /></label><label>Token type<input v-model="pricingTokenType" /></label><label>Mode<input v-model="pricingMode" /></label><label>USD / MTok<input v-model="pricingAmount" inputmode="decimal" /></label><label>Effective from<input v-model="pricingEffectiveFrom" type="date" /></label><label>Effective to<input v-model="pricingEffectiveTo" type="date" /></label><label>Min input tokens<input v-model="pricingMinimum" inputmode="numeric" /></label><label>Max input tokens<input v-model="pricingMaximum" inputmode="numeric" placeholder="不限" /></label></div><button class="button button-primary" type="button" @click="void savePricing()">儲存 override</button></section>

      <section class="pricing-overrides"><div class="section-heading-row"><div><span class="eyebrow">OVERRIDE HISTORY</span><h2>本機覆寫歷史</h2></div><span class="catalog-stamp">{{ overridePricingEntries.length }} 筆</span></div><div v-if="overridePricingEntries.length" class="table-scroll"><table><thead><tr><th>Provider</th><th>Model</th><th>Mode</th><th>Token type</th><th>USD / MTok</th><th>有效區間</th><th>操作</th></tr></thead><tbody><tr v-for="entry in overridePricingEntries" :key="`override-${entry.provider}-${entry.model}-${entry.mode}-${entry.tokenType}-${entry.effectiveFromUtc}`"><td>{{ entry.provider }}</td><td class="mono">{{ entry.model }}</td><td>{{ pricingModeLabel(entry.mode) }}</td><td>{{ pricingTokenLabel(entry.tokenType) }}</td><td class="mono">{{ entry.usdPerMillionTokens.toFixed(4) }}</td><td class="mono">{{ entry.effectiveFromUtc }}{{ entry.effectiveToUtc ? ` → ${entry.effectiveToUtc}` : ' → open' }}</td><td><button class="button button-ghost" type="button" @click="revisePricing(entry)">修訂</button><button v-if="!entry.effectiveToUtc" class="button button-ghost" type="button" @click="void deactivatePricing(entry.provider, entry.model, entry.tokenType, entry.mode)">停用</button></td></tr></tbody></table></div><p v-else class="table-empty override-empty">尚未建立本機覆寫</p></section>
    </section>

    <div v-if="currentRoute === '/dashboard'" class="dashboard-layout">
      <aside id="control-rail" class="control-rail" :class="{ 'rail-open': controlRailOpen }" aria-label="控制與資料工具">
        <section class="rail-section" aria-labelledby="date-heading">
          <h2 id="date-heading" class="section-label">日期範圍</h2>
          <div class="preset-grid">
            <button v-for="preset in ([['30', '最近 30 天'], ['7', '最近 7 天'], ['90', '最近 90 天'], ['month', '本月'], ['last-month', '上月'], ['custom', '自訂']] as const)" :key="preset[0]" class="preset-button" :class="{ selected: datePreset === preset[0] }" type="button" @click="applyPreset(preset[0])">{{ preset[1] }}</button>
          </div>
          <div class="date-shortcuts" aria-label="單日日期快捷操作">
            <button class="button button-secondary" type="button" @click="jumpToDay(-1)">前一天</button>
            <button class="button button-secondary" type="button" @click="jumpToDay(0)">今日</button>
            <button class="button button-secondary" type="button" @click="jumpToDay(1)">後一天</button>
          </div>
          <div class="date-fields">
            <label>開始日期<input v-model="dateRange.startDate" type="date" @change="datePreset = 'custom'" /></label>
            <label>結束日期<input v-model="dateRange.endDate" type="date" @change="datePreset = 'custom'" /></label>
          </div>
          <button class="button button-primary button-full" type="button" aria-label="套用日期範圍" @click="void refresh()">套用範圍</button>
        </section>

        <section class="rail-section" aria-labelledby="filter-heading">
          <h2 id="filter-heading" class="section-label">篩選</h2>
          <label>來源<select v-model="filters.sourceId" @change="void refresh()"><option value="all">全部來源</option><option v-for="source in data.sources" :key="source" :value="source">{{ source }}</option></select></label>
          <label>工具<select v-model="filters.tool" @change="void refresh()"><option value="all">App / CLI</option><option v-for="tool in data.tools" :key="tool" :value="tool">{{ tool }}</option></select></label>
          <label>模型<select v-model="filters.model" @change="void refresh()"><option value="all">全部模型</option><option v-for="model in data.models" :key="model" :value="model">{{ model }}</option></select></label>
          <label>Token type<select v-model="filters.tokenType" @change="void refresh()"><option value="all">全部 token</option><option v-for="tokenType in data.tokenTypes" :key="tokenType" :value="tokenType">{{ tokenType }}</option></select></label>
          <label>專案<select v-model="projectFilter" @change="void refresh()"><option value="all">全部專案</option><option v-for="project in projectOptions" :key="project" :value="project">{{ project }}</option></select></label>
          <label>標籤<select v-model="tagFilter" @change="void refresh()"><option value="all">全部標籤</option><option v-for="tag in allTags" :key="tag" :value="tag">{{ tag }}</option></select></label>
        </section>

        <section class="rail-section" aria-labelledby="views-heading">
          <h2 id="views-heading" class="section-label">具名檢視</h2>
          <label>檢視名稱<input v-model="viewName" placeholder="例如 本週 review" /></label>
          <div class="button-row"><button class="button button-primary" type="button" @click="saveView(false)">建立檢視</button><button class="button button-secondary" type="button" :disabled="!viewName.trim()" @click="saveView(true)">覆寫</button></div>
          <label v-if="savedViews.length">套用檢視<select v-model="selectedViewName" @change="applySavedView(savedViews.find((view) => view.name === selectedViewName)!)"><option value="">選擇檢視</option><option v-for="view in savedViews" :key="view.name" :value="view.name">{{ view.name }}</option></select></label>
          <div v-if="savedViews.length" class="tag-assignment-list"><div v-for="view in savedViews" :key="view.name" class="tag-assignment"><span>{{ view.name }}</span><button type="button" :aria-label="`刪除檢視 ${view.name}`" @click="deleteSavedView(view.name)">刪除</button></div></div>
          <p class="rail-note">檢視設定儲存在本機；損毀資料會自動清除</p>
        </section>

        <section class="rail-section" aria-labelledby="budget-heading">
          <h2 id="budget-heading" class="section-label">預算管理</h2>
          <label>名稱<input v-model="budgetName" placeholder="例如 產品線月預算" /></label>
          <label>金額 USD<input v-model="budgetAmount" inputmode="decimal" placeholder="100" /></label>
          <label>週期<select v-model="budgetPeriod"><option value="monthly">每月</option><option value="daily">每日</option><option value="custom">自訂</option></select></label>
          <div class="date-fields"><label>開始<input v-model="budgetFromDate" type="date" /></label><label>結束<input v-model="budgetToDate" type="date" /></label></div>
          <label>專案（可選）<input v-model="budgetProjectId" placeholder="workspace / project id" /></label>
          <label>標籤（可選）<input v-model="budgetTag" placeholder="review" /></label>
          <div class="button-row"><button class="button button-primary" type="button" @click="void saveBudget()">{{ editingBudgetId ? '更新預算' : '建立預算' }}</button><button v-if="editingBudgetId" class="button button-secondary" type="button" @click="resetBudgetForm">取消</button></div>
          <div v-if="budgets.length" class="tag-assignment-list"><div v-for="budget in budgets" :key="budget.id" class="tag-assignment"><span><strong>{{ budget.name }}</strong><small class="mono"> {{ formatUsd(budget.amountUsd) }}</small></span><span class="button-row"><button type="button" @click="editBudget(budget)">編輯</button><button type="button" @click="void removeBudget(budget.id)">刪除</button></span></div></div>
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

      <main class="workspace" aria-label="Dashboard 工作區">
        <button id="control-toggle" class="mobile-control-toggle" type="button" aria-controls="control-rail" :aria-expanded="controlRailOpen" @click="controlRailOpen = !controlRailOpen"><span>篩選與資料工具</span><span class="mono">{{ controlRailOpen ? '收合' : '展開' }}</span></button>
        <div v-if="syncState === 'partial'" class="state-banner state-warning" role="status"><strong>部分同步</strong><span>{{ operationMessage || '來源同步只完成部分工作，請檢查來源狀態後重新同步' }}</span></div>
        <div v-if="syncState === 'error'" class="state-banner state-error" role="alert"><strong>讀取失敗</strong><span>{{ errorMessage }}</span><button class="button button-secondary" type="button" @click="void refresh()">重試</button></div>
        <div v-if="data.pricing.unknownCount > 0" class="state-banner state-info" role="status"><strong>{{ data.pricing.unknownCount }} 種計費組合尚未定價</strong><span>這些事件仍計入 Token，但不計入已知成本。請補齊定價規則，系統不會推估費用</span><button class="button button-ghost" type="button" @click="navigate('/pricing')">管理定價</button></div>

         <div class="workspace-meta mono">snapshot {{ data.generatedAt.replace('T', ' ').replace('Z', ' UTC') }} · {{ data.overview.eventCount }} events · {{ data.overview.uniqueSessionCount }} unique sessions · {{ data.overview.turnCount }} turns</div>

        <div v-if="syncState === 'loading'" class="loading-grid" aria-label="正在載入 dashboard"><div v-for="index in 4" :key="index" class="skeleton"></div></div>
        <div v-else-if="syncState === 'empty'" class="empty-state"><span class="eyebrow">沒有本機事件</span><h3>目前選取的日期與篩選條件沒有事件</h3><p>調整日期或來源篩選，或從左側匯入 JSON／CSV 資料</p><button class="button button-primary" type="button" @click="applyPreset('30')">查看最近 30 天</button></div>
        <template v-else>
          <section class="kpi-grid" aria-label="總覽指標">
            <article class="kpi-panel"><span class="eyebrow">總 Token</span><strong :title="tokenTitle(totalTokenCount)">{{ formatTokenCount(totalTokenCount) }}</strong><span class="kpi-meta">輸入、輸出與快取合計，用於判斷工作量</span></article>
            <article class="kpi-panel"><span class="eyebrow">已知成本</span><strong>{{ formatUsd(totalCost) }}</strong><span class="kpi-meta">USD · 套用價格版本 {{ data.pricing.version }}</span><span class="kpi-meta">成本覆蓋率 {{ data.overview.costCoverage === null ? '未知' : `${Math.round(data.overview.costCoverage * 100)}%` }}<template v-if="data.overview.costUsd === null"> · 已知費用 {{ formatUsd(data.overview.partialCostUsd) }}</template></span></article>
             <article class="kpi-panel"><span class="eyebrow">事件／工作階段</span><strong>{{ formatNumber(data.overview.eventCount) }} / {{ formatNumber(totalSessions) }}</strong><span class="kpi-meta">事件 · 不重複工作階段 · 已載入 {{ visibleSessions.length }} 筆</span></article>
            <article class="kpi-panel"><span class="eyebrow">快取命中率</span><strong>{{ averageCache === null ? '未知' : `${Math.round(averageCache * 100)}%` }}</strong><span class="kpi-meta">資料覆蓋 {{ data.overview.cacheCoverage === null ? '未知' : `${Math.round(data.overview.cacheCoverage * 100)}%` }} · {{ data.overview.cacheUnreportedEventCount }} 筆未回報</span></article>
          </section>

          <div class="evidence-grid">
             <section class="panel trend-panel" aria-labelledby="trend-heading"><div class="panel-header"><div><span class="eyebrow">費用與 Token 趨勢</span><h3 id="trend-heading">{{ trendMetric === 'cost' ? '各時段的已知成本' : '各時段的 Token 工作量' }}</h3><p>{{ trendMetric === 'cost' ? '部分定價資料只計入已知費用，並保留覆蓋率' : '用於比較工作量的變化，不代表費用' }}</p></div><div class="trend-controls"><div role="group" aria-label="趨勢指標"><button v-for="option in ([['cost', '已知成本'], ['tokens', 'Token']] as const)" :key="option[0]" type="button" :class="{ selected: trendMetric === option[0] }" :aria-pressed="trendMetric === option[0]" @click="trendMetric = option[0]">{{ option[1] }}</button></div><div role="group" aria-label="趨勢彙總方式"><button v-for="option in ([['daily', '每日'], ['weekly', '每週'], ['monthly', '每月']] as const)" :key="option[0]" type="button" :class="{ selected: trendGranularity === option[0] }" :aria-pressed="trendGranularity === option[0]" @click="trendGranularity = option[0]">{{ option[1] }}</button></div></div></div><div v-if="displayedTrend.length" class="chart-wrap"><svg class="daily-chart trend-chart" viewBox="0 0 620 180" role="img" :aria-label="trendMetric === 'cost' ? '已知成本趨勢折線圖' : 'Token 工作量趨勢折線圖'"><line x1="24" y1="150" x2="600" y2="150" class="chart-rule" /><polyline :points="displayedTrend.map((point, index) => `${28 + index * (560 / Math.max(displayedTrend.length - 1, 1))},${150 - (trendValue(point) / maxTrendValue) * 112}`).join(' ')" class="chart-line" /><circle v-for="(point, index) in displayedTrend" :key="point.bucketStartUtc" :cx="28 + index * (560 / Math.max(displayedTrend.length - 1, 1))" :cy="150 - (trendValue(point) / maxTrendValue) * 112" r="3" class="chart-point" tabindex="0" :title="`${trendLabel(point.bucketStartUtc)} · ${trendValueLabel(point)}`" :aria-label="`${trendLabel(point.bucketStartUtc)} ${trendValueLabel(point)}，${point.eventCount} 筆事件`" /></svg><div class="chart-labels trend-labels"><span v-for="point in displayedTrend" :key="`${point.bucketStartUtc}-label`" :title="trendValueLabel(point)">{{ trendLabel(point.bucketStartUtc) }}</span></div></div><div v-else class="panel-empty">目前範圍沒有趨勢資料</div><div class="stat-strip"><span><b>{{ trendMetric === 'cost' ? formatUsd(displayedTrend.reduce((sum, item) => sum + trendValue(item), 0)) : formatTokenCount(displayedTrend.reduce((sum, item) => sum + item.tokens, 0)) }}</b> {{ trendMetric === 'cost' ? '已知成本' : 'Token' }}</span><span><b>{{ formatNumber(displayedTrend.reduce((sum, item) => sum + item.eventCount, 0)) }}</b> 筆事件</span><span><b>{{ formatNumber(displayedTrend.reduce((sum, item) => sum + item.uniqueSessionCount, 0)) }}</b> 個工作階段</span><span v-if="trendMetric === 'cost'"><b>{{ data.overview.costCoverage === null ? '未知' : `${Math.round(data.overview.costCoverage * 100)}%` }}</b> 成本覆蓋率</span></div></section>
           <section class="panel heatmap-panel" aria-labelledby="heatmap-heading"><div class="panel-header"><div><span class="eyebrow">ACTIVITY MAP</span><h3 id="heatmap-heading">日期熱力圖</h3></div><span class="mono">tokens / day</span></div><div class="heatmap" role="grid" aria-label="每日 token 熱力圖"><button v-for="day in heatmapDays" :key="day.date" class="heatmap-cell" :class="[`intensity-${day.intensity}`, { selected: selectedDate === day.date }]" type="button" role="gridcell" :aria-label="`${day.date} ${tokenTitle(day.tokens)}`" @click="selectDate(day.date)"><span>{{ new Date(`${day.date}T00:00:00`).getDate() }}</span></button></div><div class="heatmap-legend"><span>少</span><i v-for="level in 5" :key="level" :class="`intensity-${level}`"></i><span>多</span></div><div class="panel-footnote">選取日期會將事件日期加入全文搜尋條件</div></section>
          </div>

            <section class="panel comparison-panel" aria-label="模型與工具 token 矩形樹狀圖"><div v-if="treemapRects.length" ref="treemapContainer" class="treemap-wrap"><svg class="treemap-chart" :viewBox="treemapViewBox" role="img" aria-label="模型與工具 token 矩形樹狀圖" preserveAspectRatio="none"><defs><clipPath v-for="(rect, index) in treemapRects" :id="`treemap-clip-${index}`" :key="`clip-${rect.node.kind}-${rect.node.name}-${index}`"><rect :x="rect.x + 4" :y="rect.y + 2" :width="Math.max(0, rect.width - 8)" :height="Math.max(0, rect.height - 4)" /></clipPath></defs><g v-for="(rect, index) in treemapRects" :key="`${rect.node.kind}-${rect.node.name}-${rect.x}-${rect.y}`" class="treemap-node" :class="`treemap-depth-${rect.depth}`" @mouseenter="selectTreemapRect(rect)" @focus="selectTreemapRect(rect)" @click="selectTreemapRect(rect)"><rect :x="rect.x" :y="rect.y" :width="rect.width" :height="rect.height" tabindex="0" :aria-label="treemapLabel(rect)" :title="treemapLabel(rect)" rx="2" /><text v-if="rect.width > 48 && rect.height > 24" :x="rect.x + 6" :y="rect.y + 17" :clip-path="`url(#treemap-clip-${index})`">{{ rect.node.name }}</text></g></svg></div><div v-else class="panel-empty">目前範圍沒有模型或工具資料</div><div v-if="selectedTreemapRect" class="treemap-detail" aria-live="polite"><strong>{{ selectedTreemapRect.node.name }}</strong><span>{{ selectedTreemapRect.node.kind === 'model' ? '模型' : '工具' }} · {{ formatTokenCount(selectedTreemapRect.node.tokens) }} tokens</span><span>{{ selectedTreemapRect.node.eventCount }} events · {{ selectedTreemapRect.node.uniqueSessionCount }} sessions · {{ formatUsd(selectedTreemapRect.node.costUsd) }}</span></div></section>

          <section class="panel sessions-panel" aria-labelledby="sessions-heading"><div class="panel-header"><div><span class="eyebrow">工作階段費用</span><h3 id="sessions-heading">依工作階段追查費用與 Token</h3><p>依已知費用排序；部分定價資料會顯示已知費用與覆蓋率</p></div><div class="search-control"><label class="sr-only" for="full-search">搜尋工作階段、Turn、提示、回應或工具</label><input id="full-search" v-model="searchTerm" type="search" placeholder="搜尋工作階段、提示或工具" @input="void runSearch()" /><kbd>Ctrl K</kbd></div></div><div v-if="searchTerm" class="search-results" aria-live="polite"><span class="eyebrow">搜尋結果</span><span>{{ searchError || `${searchResults.length} 筆相符` }}</span><button v-for="result in searchResults" :key="result.itemId" class="result-link" type="button" @click="result.sessionId && selectSession(data.sessions.find((session) => session.id === result.sessionId)!)">{{ result.title }}</button></div><div class="session-list"><button v-for="session in visibleSessions" :key="session.id" class="session-row" :class="{ selected: selectedSessionId === session.id }" type="button" @click="selectSession(session)"><span class="session-main"><strong>{{ session.workspaceId || session.title }}</strong><span>{{ formatSessionStartedAt(session.startedAt) }} · {{ session.source }} · {{ session.model || '模型未提供' }}<template v-if="session.additionalModelCount"> · + {{ session.additionalModelCount }} 個模型</template><template v-if="session.effort"> · 推理強度 {{ session.effort }}<template v-if="session.additionalEffortCount"> · + {{ session.additionalEffortCount }} 種設定</template></template></span><span class="session-token-summary"><span :title="tokenTitle(inputTokenCount(session.tokens))">輸入 {{ formatTokenCount(inputTokenCount(session.tokens)) }}</span><span :title="tokenTitle(outputTokenCount(session.tokens))">輸出 {{ formatTokenCount(outputTokenCount(session.tokens)) }}</span><span :title="tokenTitle(cacheTokenCount(session.tokens))">快取 {{ formatTokenCount(cacheTokenCount(session.tokens)) }}</span><span :title="tokenTitle(totalTokens(session.tokens))">合計 {{ formatTokenCount(totalTokens(session.tokens)) }}</span></span></span><span class="session-meta"><span>{{ eventCount(session) }} 筆事件</span><span class="mono" :class="{ unknown: session.costUsd === null }" :title="sessionCostLabel(session)">{{ sessionCostLabel(session) }}</span></span></button></div></section>

            <section class="panel monthly-panel" aria-labelledby="monthly-heading"><div class="panel-header"><div><span class="eyebrow">每月彙總</span><h3 id="monthly-heading">每月費用與工作量</h3></div><span class="mono">以 {{ data.overview.timeZoneId }} 劃分月份</span></div><div class="monthly-list"><div v-for="month in data.monthly" :key="month.date" class="monthly-row"><strong>{{ month.date.slice(0, 7) }}</strong><span class="mono">{{ formatTokenCount(month.tokens) }} Token</span><span>{{ month.eventCount }} 筆事件 · {{ month.uniqueSessionCount }} 個工作階段 · {{ month.turnCount }} 個 Turn</span><span class="mono" :class="{ unknown: month.costUsd === null }">{{ formatUsd(month.costUsd) }}</span><span>{{ month.cacheHitRate === null ? '快取資料未知' : `快取命中 ${Math.round(month.cacheHitRate * 100)}%` }}</span></div></div></section>
            <section v-if="budgets.length" class="panel budget-panel" aria-labelledby="budget-summary-heading"><div class="panel-header"><div><span class="eyebrow">BUDGET MONITOR</span><h3 id="budget-summary-heading">預算使用狀態</h3></div><span class="mono">{{ budgets.length }} 個預算</span></div><div class="monthly-list"><div v-for="budget in budgets" :key="budget.id" class="monthly-row"><strong>{{ budget.name }}</strong><span>{{ budget.period }}</span><span class="mono">{{ formatUsd(budgetSummaries.find((summary) => summary.budgetId === budget.id)?.spentUsd ?? 0) }} / {{ formatUsd(budget.amountUsd) }}</span><span>{{ Math.round(budgetSummaries.find((summary) => summary.budgetId === budget.id)?.percentUsed ?? 0) }}% 已使用</span></div></div></section>
        </template>
      </main>

      <aside class="inspector" aria-label="選取資料檢視器">
        <div class="inspector-heading"><span class="eyebrow">詳細資料</span><h2>{{ selectedSession ? '已選取的工作階段' : '資料檢視器' }}</h2><p>{{ selectedSession ? `工作階段 ID：${selectedSession.id}` : '從工作階段費用清單選取一筆資料，查看 Token 與成本明細' }}</p></div>
        <nav class="inspector-tabs" aria-label="檢視器分頁"><button v-for="tab in ([['detail', '明細'], ['stats', '定價'], ['capabilities', '功能']] as const)" :key="tab[0]" type="button" :class="{ active: inspectorTab === tab[0] }" @click="inspectorTab = tab[0]">{{ tab[1] }}</button></nav>
         <div v-if="inspectorTab === 'detail'" class="inspector-content"><template v-if="selectedSession"><section class="inspector-section"><span class="eyebrow">Token 明細</span><h3>{{ selectedSession.model || '模型未提供' }}</h3><p v-if="selectedSession.effort" class="mono">推理強度 {{ selectedSession.effort }}</p><dl class="detail-list"><div><dt>輸入</dt><dd class="mono">{{ formatTokenCount(inputTokenCount(selectedSession.tokens)) }}</dd></div><div><dt>輸出</dt><dd class="mono">{{ formatTokenCount(outputTokenCount(selectedSession.tokens)) }}</dd></div><div><dt>快取</dt><dd class="mono">{{ formatTokenCount(cacheTokenCount(selectedSession.tokens)) }}</dd></div><div><dt>合計</dt><dd class="mono">{{ formatTokenCount(totalTokens(selectedSession.tokens)) }}</dd></div><div><dt>已知成本</dt><dd class="mono" :class="{ unknown: selectedSession.costUsd === null }">{{ sessionCostLabel(selectedSession) }}</dd></div><div v-if="selectedSession.costUsd === null"><dt>成本覆蓋率</dt><dd class="mono">{{ costCoverageLabel(selectedSession.costCoverage) }}</dd></div></dl></section><section class="inspector-section"><span class="eyebrow">來源與時間</span><p>{{ selectedSession.source }} · {{ selectedSession.tool }}</p><p class="mono">開始 {{ selectedSession.startedAt }}</p><p class="mono">結束 {{ selectedSession.endedAt }}</p></section></template><div v-else class="inspector-empty">從工作階段費用清單選取資料後，這裡會顯示 Token 明細、已知成本與來源時間</div></div>
        <div v-else-if="inspectorTab === 'stats'" class="inspector-content"><section class="inspector-section"><span class="eyebrow">PRICING VERSION</span><h3>{{ data.pricing.version }}</h3><dl class="detail-list"><div><dt>Effective from</dt><dd class="mono">{{ data.pricing.effectiveFrom }}</dd></div><div><dt>Unknown price</dt><dd class="unknown mono">{{ data.pricing.unknownCount }}</dd></div><div><dt>Overrides</dt><dd class="mono">{{ data.pricing.overrideCount }}</dd></div></dl></section><section class="inspector-section"><span class="eyebrow">PRICE OVERRIDE</span><label>Provider<input v-model="pricingProvider" placeholder="openai" /></label><label>Model<input v-model="pricingModel" placeholder="gpt-5-codex" /></label><label>Token type<select v-model="pricingTokenType"><option v-for="tokenType in data.tokenTypes" :key="tokenType" :value="tokenType">{{ tokenType }}</option></select></label><label>Mode<input v-model="pricingMode" placeholder="standard" /></label><label>USD / MTok<input v-model="pricingAmount" inputmode="decimal" placeholder="3.00" /></label><label>Effective from<input v-model="pricingEffectiveFrom" type="date" /></label><label>Effective to<input v-model="pricingEffectiveTo" type="date" /></label><label>Min input tokens<input v-model="pricingMinimum" inputmode="numeric" /></label><label>Max input tokens<input v-model="pricingMaximum" inputmode="numeric" placeholder="不限" /></label><button class="button button-primary button-full" type="button" @click="void savePricing()">儲存 override</button><p class="rail-note">有效區間採半開區間；未知價格不會被推估</p></section></div>
        <div v-else class="inspector-content"><section class="inspector-section"><span class="eyebrow">CAPABILITY MAP</span><h3>目前可用能力</h3><ul class="capability-list"><li v-for="capability in data.capabilities" :key="capability">{{ capability }}</li></ul></section><section class="inspector-section"><span class="eyebrow">TAG MANAGEMENT</span><label>Scope<select v-model="tagScope"><option value="session">Session</option><option value="project">Project</option><option value="source">Source</option></select></label><label>Entity target<input v-model="tagEntityId" :placeholder="tagScope === 'session' ? selectedSession?.id ?? 'session-id' : 'source-or-project-id'" /></label><label>Tag key<input id="tag-management-input" v-model="tagInput" placeholder="例如 review" @keyup.enter="addTag" /></label><label>Value<input v-model="tagValue" placeholder="可選值" /></label><button class="button button-secondary" type="button" @click="addTag">新增 tag</button><div class="tag-assignment-list"><div v-for="assignment in data.tags" :key="`${assignment.scope}-${assignment.entityId}-${assignment.id || assignment.key}`" class="tag-assignment"><span class="tag tag-neutral">{{ assignment.key }}<span v-if="assignment.value">={{ assignment.value }}</span></span><span class="mono">{{ assignment.scope }} / {{ assignment.entityId }}</span><button type="button" :aria-label="`刪除 ${assignment.key} tag`" @click="removeAssignment(assignment)">刪除</button></div><span v-if="!data.tags.length" class="rail-note">目前沒有 tag assignment</span></div></section><section class="inspector-section"><span class="eyebrow">TAGS</span><div class="tag-list"><button v-for="tag in allTags" :key="tag" class="tag tag-neutral" type="button" @click="tagFilter = tag; void refresh()">{{ tag }}</button></div></section></div>
        <div class="inspector-footer"><span class="eyebrow">SESSION STORAGE</span><p>Startup fragment key 讀取後立即移除，API 只使用 <code>X-Token-Dashboard-Key</code></p></div>
      </aside>
    </div>

    <dialog v-if="pendingExport" ref="exportDialog" class="confirm-dialog" aria-labelledby="export-heading" @cancel="cancelDialog('export', $event)" @close="finalizeDialog('export')"><div class="dialog-panel"><span class="eyebrow">CONTENT EXPORT</span><h2 id="export-heading">匯出完整內容？</h2><p>此 {{ pendingExport.toUpperCase() }} 檔案會包含 prompt、response 與 tool payload，可能含敏感內容。</p><div class="dialog-actions"><button class="button button-secondary" type="button" data-autofocus @click="closeDialog('export')">取消</button><button class="button button-primary" type="button" @click="void performExport(pendingExport)">確認匯出</button></div></div></dialog>
    <dialog v-if="showDeleteConfirm" ref="deleteDialog" class="confirm-dialog" aria-labelledby="delete-heading" @cancel="cancelDialog('delete', $event)" @close="finalizeDialog('delete')"><div class="dialog-panel"><span class="eyebrow">DESTRUCTIVE ACTION</span><h2 id="delete-heading">刪除所有本機資料？</h2><p>這會刪除 Session、Turn、事件、tag 與搜尋索引。匯出備份無法在此操作後自動恢復。</p><div class="dialog-actions"><button class="button button-secondary" type="button" data-autofocus @click="closeDialog('delete')">取消</button><button class="button button-danger" type="button" @click="void confirmDelete()">確認刪除</button></div></div></dialog>
  </div>
</template>
