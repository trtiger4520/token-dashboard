export type SyncState = 'loading' | 'ready' | 'empty' | 'error' | 'partial'
export type TokenType = string
export type EventKind = 'prompt' | 'response' | 'tool' | 'subagent' | 'workflow'

export interface OverviewStat {
  fromUtc: string
  toUtc: string
  timeZoneId: string
  eventCount: number
  sessionCount: number
  uniqueSessionCount: number
  turnCount: number
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  cacheHitRate: number | null
  costUsd: number | null
  partialCostUsd: number
  pricedTokenCount: number
  unpricedTokenCount: number
  costCoverage: number | null
  cacheReportedEventCount: number
  cacheUnreportedEventCount: number
  cacheCoverage: number | null
  unpriced: boolean
  unpricedCount: number
  tokenCounts: TokenBreakdown
}

export type TokenBreakdown = Record<string, number>

export interface DailyStat {
  date: string
  tokens: number
  costUsd: number | null
  partialCostUsd: number
  costCoverage: number | null
  eventCount: number
  turnCount: number
  uniqueSessionCount: number
  sessions: number
  cacheHitRate: number | null
}

export interface TrendPoint extends DailyStat {
  bucketStartUtc: string
  bucketEndUtc: string
}

export interface ComparisonRow {
  name: string
  kind: 'model' | 'tool'
  tokens: number
  eventCount: number
  turnCount: number
  uniqueSessionCount: number
  sessions: number
  averageTokens: number
  costUsd: number | null
  cacheHitRate: number | null
}

export interface ComparisonTreeNode {
  name: string
  kind: 'model' | 'tool'
  tokens: number
  eventCount: number
  turnCount: number
  uniqueSessionCount: number
  costUsd: number | null
  partialCostUsd: number
  cacheHitRate: number | null
  children: ComparisonTreeNode[]
}

export interface TimelineEvent {
  id: string
  kind: EventKind
  label: string
  summary: string
  timestamp: string
  tokens?: number
  detail?: string
  model?: string
  tool?: string
}

export interface TurnRecord {
  id: string
  number: number
  model: string
  effort: string | null
  tokens: TokenBreakdown
  events: TimelineEvent[]
}

export interface SessionRecord {
  id: string
  title: string
  workspaceId: string | null
  source: string
  tool: string
  model: string
  effort: string | null
  additionalModelCount: number
  additionalEffortCount: number
  startedAt: string
  endedAt: string
  tokens: TokenBreakdown
  costUsd: number | null
  eventCount?: number
  turnCount?: number
  partialCostUsd?: number
  pricedTokenCount?: number
  unpricedTokenCount?: number
  costCoverage?: number | null
  tags: string[]
  turns: TurnRecord[]
}

export interface TagRecord {
  id: string
  key: string
  value: string
  scope?: string
  entityId?: string
}

export interface DashboardData {
  generatedAt: string
  overview: OverviewStat
  sources: string[]
  tools: string[]
  models: string[]
  tokenTypes: TokenType[]
  trend: TrendPoint[]
  daily: DailyStat[]
  monthly: DailyStat[]
  heatmap: DailyStat[]
  comparisons: ComparisonRow[]
  comparisonTree: ComparisonTreeNode[]
  sessions: SessionRecord[]
  capabilities: string[]
  tags: TagRecord[]
  pricing: {
    version: string
    effectiveFrom: string
    unknownCount: number
    overrideCount: number
    entries: PricingEntry[]
  }
}

export interface DashboardQuery {
  preset: string
  from: string
  to: string
  timeZone: string
  sourceId?: string
  tool?: string
  model?: string
  tokenType?: string
  trendInterval?: string
}

export interface CapabilityRecord {
  adapterKind: string
  status: string
  formats: string[]
  notes: string
}

export interface PricingEntry {
  provider: string
  model: string
  mode: string
  tokenType: string
  minimumInputTokens: number
  maximumInputTokens: number | null
  usdPerMillionTokens: number
  sourceName: string
  sourceUrl: string
  effectiveFromUtc: string | null
  effectiveToUtc: string | null
  isOverride: boolean
  catalogVersion?: string
  createdAtUtc?: string
  sourceKind?: string
}

export interface UnknownPricing {
  provider: string
  model: string
  mode: string
  tokenType: string
  earliestEventUtc: string
  latestEventUtc: string
  tokenCount: number
  suggestion: PricingSuggestion | null
}

export interface PricingSuggestion {
  catalogModel: string
  catalogMode: string
  catalogTokenType: string
  minimumInputTokens: number
  maximumInputTokens: number | null
  usdPerMillionTokens: number
  effectiveFrom: string
  sourceName: string
  sourceUrl: string
  reason: string
}

export interface SearchResult {
  itemId: string
  sourceId: string
  sessionId: string | null
  turnId: string | null
  rank: number
}

export const EMPTY_TOKENS: TokenBreakdown = {}

export function totalTokens(tokens: TokenBreakdown): number {
  return Object.values(tokens).reduce((sum, value) => sum + value, 0)
}

export function tokenValue(tokens: TokenBreakdown, ...aliases: string[]): number {
  const normalized = new Set(aliases.map((alias) => alias.toLowerCase().replace(/[_ ]/g, '-')))
  return Object.entries(tokens).reduce((sum, [key, value]) => normalized.has(key.toLowerCase().replace(/[_ ]/g, '-')) ? sum + value : sum, 0)
}

export function formatNumber(value: number): string {
  return new Intl.NumberFormat('en-US').format(value)
}

export function formatTokenCount(value: number): string {
  const absolute = Math.abs(value)
  if (absolute < 1_000) return formatNumber(value)
  const divisor = absolute >= 1_000_000 ? 1_000_000 : 1_000
  const suffix = divisor === 1_000_000 ? 'm' : 'k'
  const compact = (value / divisor).toFixed(1).replace(/\.0$/, '')
  return `${compact}${suffix}`
}

export function inputTokenCount(tokens: TokenBreakdown): number {
  return tokenValue(tokens, 'input', 'cacheable-input')
}

export function outputTokenCount(tokens: TokenBreakdown): number {
  return tokenValue(tokens, 'output', 'reasoning')
}

export function cacheTokenCount(tokens: TokenBreakdown): number {
  return Object.entries(tokens).reduce((sum, [key, value]) => /cache/i.test(key) ? sum + value : sum, 0)
}

export function formatUsd(value: number | null): string {
  return value === null ? '未知價格' : `$${value.toFixed(2)}`
}

export function formatDateLabel(value: string): string {
  return new Intl.DateTimeFormat('zh-TW', { month: 'short', day: 'numeric' }).format(new Date(`${value}T00:00:00`))
}

export function createEmptyDashboardData(): DashboardData {
  return {
    generatedAt: '',
    overview: {
      fromUtc: '',
      toUtc: '',
      timeZoneId: 'UTC',
      eventCount: 0,
      sessionCount: 0,
      uniqueSessionCount: 0,
      turnCount: 0,
      inputTokens: 0,
      cachedInputTokens: 0,
      outputTokens: 0,
      cacheHitRate: null,
      costUsd: null,
      partialCostUsd: 0,
      pricedTokenCount: 0,
      unpricedTokenCount: 0,
      costCoverage: null,
      cacheReportedEventCount: 0,
      cacheUnreportedEventCount: 0,
      cacheCoverage: null,
      unpriced: false,
      unpricedCount: 0,
      tokenCounts: {}
    },
    sources: [],
    tools: [],
    models: [],
    tokenTypes: ['input', 'output', 'cache-read', 'cache-write'],
    trend: [],
    daily: [],
    monthly: [],
    heatmap: [],
    comparisons: [],
    comparisonTree: [],
    sessions: [],
    tags: [],
    capabilities: [],
    pricing: { version: '', effectiveFrom: '', unknownCount: 0, overrideCount: 0, entries: [] }
  }
}
