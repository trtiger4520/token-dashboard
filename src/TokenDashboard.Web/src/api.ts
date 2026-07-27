import type { CapabilityRecord, ComparisonRow, DashboardData, DashboardQuery, DailyStat, PricingEntry, SearchResult, SessionRecord, TagRecord, TimelineEvent, TokenBreakdown, TurnRecord, OverviewStat } from './types'

type JsonRecord = Record<string, unknown>

export interface SyncRequest {
  adapter?: string
  paths?: string[]
  workspaceId?: string
  ownerId?: string
}

export interface SyncStatus {
  syncId: string
  status: 'queued' | 'running' | 'completed' | 'partial' | 'failed' | string
  error?: string | null
}

export interface SourceDiscoveryResult {
  adapter: string
  capabilities: CapabilityRecord | null
  paths: string[]
}

export interface TagRequest {
  scope: 'session' | 'source' | 'project'
  entityId: string
  key: string
  value: string
}

export interface PriceWriteRequest {
  provider: string
  model: string
  tokenType: string
  usdPerMillionTokens: number
  mode?: string
  minimumInputTokens?: number
  maximumInputTokens?: number | null
  effectiveFromUtc?: string | null
  effectiveToUtc?: string | null
  sourceName?: string | null
  sourceUrl?: string | null
}

export class ApiError extends Error {
  constructor(message: string, readonly status?: number) {
    super(message)
    this.name = 'ApiError'
  }
}

export function extractStartupKey(): string | null {
  const params = new URLSearchParams(window.location.hash.replace(/^#/, ''))
  const fragmentKey = params.get('key')
  if (fragmentKey) {
    const pathname = window.location.pathname.endsWith('/index.html')
      ? window.location.pathname.slice(0, -'index.html'.length) || '/'
      : window.location.pathname
    window.history.replaceState({}, document.title, `${pathname}${window.location.search}`)
    window.sessionStorage.setItem('token-dashboard-key', fragmentKey)
    return fragmentKey
  }

  return window.sessionStorage.getItem('token-dashboard-key')
}

function stringValue(record: JsonRecord | undefined, ...keys: string[]): string {
  for (const key of keys) {
    const value = record?.[key]
    if (typeof value === 'string') return value
  }
  return ''
}

function nullableString(record: JsonRecord | undefined, ...keys: string[]): string | null {
  const value = stringValue(record, ...keys)
  return value || null
}

function numberValue(record: JsonRecord | undefined, ...keys: string[]): number {
  for (const key of keys) {
    const value = record?.[key]
    if (typeof value === 'number') return value
    if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value)
  }
  return 0
}

function nullableNumber(record: JsonRecord | undefined, ...keys: string[]): number | null {
  for (const key of keys) {
    const value = record?.[key]
    if (value === null) return null
    if (typeof value === 'number') return value
    if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value)
  }
  return null
}

function records(value: unknown): JsonRecord[] {
  return Array.isArray(value) ? value.filter((item): item is JsonRecord => typeof item === 'object' && item !== null) : []
}

function queryString(query: DashboardQuery): string {
  const params = new URLSearchParams({ preset: query.preset, from: query.from, to: query.to, timeZone: query.timeZone })
  if (query.sourceId) params.set('sourceId', query.sourceId)
  if (query.tool) params.set('tool', query.tool)
  if (query.model) params.set('model', query.model)
  if (query.tokenType) params.set('tokenType', query.tokenType)
  return params.toString()
}

function tokenBreakdown(value: unknown): TokenBreakdown {
  const result: TokenBreakdown = {}
  for (const row of records(value)) {
    const type = stringValue(row, 'token_type', 'tokenType').toLowerCase().replace(/[_ ]/g, '-')
    const count = numberValue(row, 'token_count', 'tokenCount', 'count')
    if (type) result[type] = (result[type] ?? 0) + count
  }
  return result
}

function tokenCountsFromRecord(row: JsonRecord): TokenBreakdown {
  const result: TokenBreakdown = {}
  const nested = row.tokenCounts ?? row.token_counts ?? row.tokens ?? row.tokenTypes ?? row.token_types
  if (nested && typeof nested === 'object' && !Array.isArray(nested)) {
    for (const [key, value] of Object.entries(nested)) {
      if (typeof value === 'number') result[key.toLowerCase().replace(/[_ ]/g, '-')] = value
    }
  }
  for (const [key, value] of Object.entries(row)) {
    if (key.toLowerCase() === 'totaltokens' || !key.toLowerCase().endsWith('tokens') || typeof value !== 'number') continue
    const tokenType = key.slice(0, -6).replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`).replace(/^-/, '').replace(/[_ ]/g, '-')
    if (tokenType) result[tokenType] = value
  }
  return result
}

function rowTokenCounts(row: JsonRecord): TokenBreakdown {
  const result = tokenCountsFromRecord(row)
  if (Object.keys(result).length) return result
  return {
    input: numberValue(row, 'inputTokens', 'input_tokens'),
    'cached-input': numberValue(row, 'cachedInputTokens', 'cached_input_tokens'),
    output: numberValue(row, 'outputTokens', 'output_tokens')
  }
}

function sumTokenCounts(tokens: TokenBreakdown): number {
  return Object.values(tokens).reduce((sum, value) => sum + value, 0)
}

function normalizeDaily(row: JsonRecord): DailyStat {
  const tokenCounts = rowTokenCounts(row)
  return {
    date: stringValue(row, 'date'),
    tokens: sumTokenCounts(tokenCounts),
    costUsd: nullableNumber(row, 'costUsd', 'cost_usd'),
    sessions: numberValue(row, 'eventCount', 'event_count'),
    cacheHitRate: nullableNumber(row, 'cacheHitRate', 'cache_hit_rate')
  }
}

function normalizeComparison(row: JsonRecord, kind: ComparisonRow['kind']): ComparisonRow {
  const tokenCounts = rowTokenCounts(row)
  const tokens = sumTokenCounts(tokenCounts)
  const sessions = numberValue(row, 'eventCount', 'event_count')
  return {
    name: stringValue(row, 'key', kind === 'tool' ? 'tool' : 'model') || 'unknown',
    kind,
    tokens,
    sessions,
    averageTokens: sessions ? Math.round(tokens / sessions) : 0,
    costUsd: nullableNumber(row, 'costUsd', 'cost_usd'),
    cacheHitRate: nullableNumber(row, 'cacheHitRate', 'cache_hit_rate')
  }
}

function eventKind(value: string): TimelineEvent['kind'] {
  const normalized = value.toLowerCase()
  if (normalized.includes('tool')) return 'tool'
  if (normalized.includes('subagent')) return 'subagent'
  if (normalized.includes('workflow')) return 'workflow'
  if (normalized.includes('response') || normalized.includes('assistant')) return 'response'
  return 'prompt'
}

function normalizeEvent(row: JsonRecord, index: number): TimelineEvent {
  const kind = eventKind(stringValue(row, 'event_type', 'eventType', 'role'))
  const prompt = stringValue(row, 'prompt')
  const response = stringValue(row, 'response')
  const tool = stringValue(row, 'tool')
  const subagent = stringValue(row, 'subagent')
  const workflow = stringValue(row, 'workflow')
  const payload = stringValue(row, 'payload')
  const summary = kind === 'prompt' ? prompt : kind === 'response' ? response : tool || subagent || workflow || payload || stringValue(row, 'event_type', 'eventType')
  return {
    id: stringValue(row, 'event_fingerprint', 'eventFingerprint', 'id') || `event-${index + 1}`,
    kind,
    label: kind[0].toUpperCase() + kind.slice(1),
    summary: summary || '事件內容未提供',
    timestamp: stringValue(row, 'occurred_at_utc', 'occurredAtUtc'),
    detail: [prompt, response, payload].filter(Boolean).join('\n\n') || undefined,
    model: stringValue(row, 'model') || undefined,
    tool: tool || undefined
  }
}

function normalizeTurn(row: JsonRecord, index: number): TurnRecord {
  const subEvents = records(row.subEvents ?? row.sub_events)
  const contents = records(row.contents)
  const events = subEvents.length ? subEvents.map(normalizeEvent) : contents.map((content, contentIndex) => normalizeEvent({ ...content, eventType: stringValue(content, 'role') }, contentIndex))
  return {
    id: stringValue(row, 'id', 'turn_id', 'turnId') || `turn-${index + 1}`,
    number: numberValue(row, 'sequence') || index + 1,
    model: events.find((event) => event.model)?.model ?? '',
    tokens: tokenBreakdown(row.tokenUsage ?? row.token_usage),
    events
  }
}

function normalizeSession(summary: JsonRecord, detail: JsonRecord | undefined): SessionRecord {
  const detailSession = detail?.session as JsonRecord | undefined
  const turns = records(detail?.turns).map(normalizeTurn)
  const firstEvent = turns.flatMap((turn) => turn.events)[0]
  return {
    id: stringValue(summary, 'id', 'sessionId', 'session_id') || stringValue(detailSession, 'session_id', 'id'),
    title: stringValue(summary, 'title') || `Session ${stringValue(summary, 'id', 'sessionId', 'session_id').slice(0, 8)}`,
    source: stringValue(summary, 'sourceId', 'source_id') || stringValue(detailSession, 'source_id'),
    tool: turns.flatMap((turn) => turn.events).find((event) => event.tool)?.tool ?? (firstEvent?.kind === 'tool' ? firstEvent.summary : ''),
    model: turns[0]?.model || '',
    startedAt: stringValue(summary, 'startedAtUtc', 'started_at_utc') || stringValue(detailSession, 'started_at_utc'),
    endedAt: stringValue(summary, 'endedAtUtc', 'ended_at_utc', 'lastActivityAtUtc', 'last_activity_at_utc') || stringValue(detailSession, 'last_activity_at_utc'),
    tokens: turns.reduce<TokenBreakdown>((total, turn) => {
      for (const [key, value] of Object.entries(turn.tokens)) total[key] = (total[key] ?? 0) + value
      return total
    }, {}),
    costUsd: nullableNumber(summary, 'costUsd', 'cost_usd') ?? nullableNumber(detailSession, 'costUsd', 'cost_usd'),
    partialCostUsd: numberValue(summary, 'partialCostUsd', 'partial_cost_usd'),
    pricedTokenCount: numberValue(summary, 'pricedTokenCount', 'priced_token_count'),
    unpricedTokenCount: numberValue(summary, 'unpricedTokenCount', 'unpriced_token_count'),
    costCoverage: nullableNumber(summary, 'costCoverage', 'cost_coverage'),
    tags: [],
    turns
  }
}

function normalizeOverview(row: JsonRecord): OverviewStat {
  const inputTokens = numberValue(row, 'inputTokens', 'input_tokens')
  const cachedInputTokens = numberValue(row, 'cachedInputTokens', 'cached_input_tokens')
  const outputTokens = numberValue(row, 'outputTokens', 'output_tokens')
  return {
    fromUtc: stringValue(row, 'fromUtc', 'from_utc'),
    toUtc: stringValue(row, 'toUtc', 'to_utc'),
    timeZoneId: stringValue(row, 'timeZoneId', 'time_zone_id') || 'UTC',
    eventCount: numberValue(row, 'eventCount', 'event_count'),
    sessionCount: numberValue(row, 'sessionCount', 'session_count'),
    inputTokens,
    cachedInputTokens,
    outputTokens,
    cacheHitRate: nullableNumber(row, 'cacheHitRate', 'cache_hit_rate'),
    costUsd: nullableNumber(row, 'costUsd', 'cost_usd'),
    partialCostUsd: numberValue(row, 'partialCostUsd', 'partial_cost_usd'),
    pricedTokenCount: numberValue(row, 'pricedTokenCount', 'priced_token_count'),
    unpricedTokenCount: numberValue(row, 'unpricedTokenCount', 'unpriced_token_count'),
    costCoverage: nullableNumber(row, 'costCoverage', 'cost_coverage'),
    cacheReportedEventCount: numberValue(row, 'cacheReportedEventCount', 'cache_reported_event_count'),
    cacheUnreportedEventCount: numberValue(row, 'cacheUnreportedEventCount', 'cache_unreported_event_count'),
    cacheCoverage: nullableNumber(row, 'cacheCoverage', 'cache_coverage'),
    unpriced: Boolean(row.unpriced),
    unpricedCount: numberValue(row, 'unpricedCount', 'unpriced_count'),
    tokenCounts: Object.keys(tokenCountsFromRecord(row)).length ? tokenCountsFromRecord(row) : { input: inputTokens, 'cached-input': cachedInputTokens, output: outputTokens }
  }
}

function normalizeCapability(row: JsonRecord): CapabilityRecord {
  return { adapterKind: stringValue(row, 'adapterKind', 'adapter_kind'), status: stringValue(row, 'status'), formats: Array.isArray(row.formats) ? row.formats.filter((item): item is string => typeof item === 'string') : [], notes: stringValue(row, 'notes') }
}

function normalizePricing(row: JsonRecord): PricingEntry {
  return {
    provider: stringValue(row, 'provider'),
    model: stringValue(row, 'model'),
    mode: stringValue(row, 'mode'),
    tokenType: stringValue(row, 'tokenType', 'token_type'),
    minimumInputTokens: numberValue(row, 'minimumInputTokens', 'minimum_input_tokens'),
    maximumInputTokens: nullableNumber(row, 'maximumInputTokens', 'maximum_input_tokens'),
    usdPerMillionTokens: numberValue(row, 'usdPerMillionTokens', 'usd_per_million_tokens'),
    sourceName: stringValue(row, 'sourceName', 'source_name'),
    sourceUrl: stringValue(row, 'sourceUrl', 'source_url'),
    effectiveFromUtc: nullableString(row, 'effectiveFromUtc', 'effective_from_utc', 'effectiveFrom', 'effective_from', 'effectiveDate', 'effective_date'),
    effectiveToUtc: nullableString(row, 'effectiveToUtc', 'effective_to_utc', 'effectiveTo', 'effective_to'),
    isOverride: Boolean(row.isOverride ?? row.is_override)
  }
}

function normalizeTag(row: JsonRecord): TagRecord {
  return {
    id: stringValue(row, 'id', 'tagId', 'tag_id'),
    key: stringValue(row, 'key', 'tagKey', 'tag_key'),
    value: stringValue(row, 'value', 'tagValue', 'tag_value'),
    scope: nullableString(row, 'scope') ?? undefined,
    entityId: nullableString(row, 'entityId', 'entity_id') ?? undefined
  }
}

function normalizeTags(payload: unknown): TagRecord[] {
  if (Array.isArray(payload)) return records(payload).map(normalizeTag)
  if (!payload || typeof payload !== 'object') return []
  const envelope = payload as JsonRecord
  const tags = records(envelope.tags ?? envelope.items).map(normalizeTag)
  const assignments = records(envelope.assignments).map(normalizeTag)
  return assignments.length ? assignments.map((assignment) => {
    const source = tags.find((tag) => tag.id === assignment.id)
    return { ...(source ?? {}), ...assignment, id: assignment.id || source?.id || assignment.key }
  }) : tags
}

function normalizeSourceDiscovery(payload: unknown): SourceDiscoveryResult[] {
  const rows = Array.isArray(payload) ? records(payload) : payload && typeof payload === 'object' ? [payload as JsonRecord] : []
  const knownAdapters = ['ClaudeCodeApp', 'ClaudeCodeCli', 'CodexApp', 'CodexCli']
  const normalized = rows.map((row) => {
    const capabilities = row.capabilities && typeof row.capabilities === 'object' && !Array.isArray(row.capabilities) ? normalizeCapability(row.capabilities as JsonRecord) : null
    const paths = records(row.paths).map((path) => stringValue(path, 'path')).filter(Boolean)
    return { adapter: stringValue(row, 'adapter', 'adapterKind', 'adapter_kind'), capabilities, paths }
  })
  if (normalized.length !== knownAdapters.length) return normalized
  return knownAdapters.map((adapter) => normalized.find((item) => item.adapter.toLowerCase() === adapter.toLowerCase()) ?? { adapter, capabilities: null, paths: [] })
}

export class TokenDashboardClient {
  constructor(private readonly baseUrl = '') {}

  private async response(path: string, init: RequestInit = {}): Promise<Response> {
    const key = window.sessionStorage.getItem('token-dashboard-key')
    if (!key) throw new ApiError('需要有效的 localhost session key 才能存取資料', 401)
    const headers = new Headers(init.headers)
    headers.set('Accept', 'application/json')
    headers.set('X-Token-Dashboard-Key', key)
    if (init.body && typeof init.body === 'string') headers.set('Content-Type', 'application/json')
    const result = await fetch(`${this.baseUrl}${path}`, { ...init, headers })
    if (!result.ok) throw new ApiError(`API request failed: ${result.status}`, result.status)
    return result
  }

  private async json<T>(path: string, init: RequestInit = {}): Promise<T> {
    const result = await this.response(path, init)
    if (result.status === 204) return undefined as T
    return (await result.json()) as T
  }

  async getDashboard(query: DashboardQuery): Promise<DashboardData> {
    const range = queryString(query)
    const [overview, daily, monthly, modelComparisons, toolComparisons, heatmap, sessionRows, capabilities, pricing, tagPayload] = await Promise.all([
      this.json<JsonRecord>(`/api/overview?${range}`),
      this.json<unknown[]>(`/api/usage/daily?${range}`),
      this.json<unknown[]>(`/api/usage/monthly?${range}`),
      this.json<unknown[]>(`/api/comparisons?${range}&groupBy=model`),
      this.json<unknown[]>(`/api/comparisons?${range}&groupBy=tool`),
      this.json<unknown[]>(`/api/heatmap?${range}`),
      this.json<unknown[]>(`/api/sessions?${range}`),
      this.json<unknown[]>(`/api/sources/capabilities?${range}`),
      this.json<JsonRecord>(`/api/pricing?${range}`),
      this.json<unknown[]>(`/api/tags?${range}`)
    ])
    const summaries = records(sessionRows)
    const details = await Promise.all(summaries.map((summary) => this.json<JsonRecord>(`/api/sessions/${encodeURIComponent(stringValue(summary, 'id', 'sessionId', 'session_id'))}`)))
    const normalizedSessions = summaries.map((summary, index) => normalizeSession(summary, details[index]))
    const normalizedComparisons = [
      ...records(modelComparisons).map((row) => normalizeComparison(row, 'model')),
      ...records(toolComparisons).map((row) => normalizeComparison(row, 'tool'))
    ]
    const normalizedCapabilities = records(capabilities).map(normalizeCapability)
    const pricingEntries = records(pricing.entries).map(normalizePricing)
    const normalizedTags = normalizeTags(tagPayload)
    for (const session of normalizedSessions) {
      session.tags = normalizedTags.filter((tag) => tag.scope === 'session' && tag.entityId === session.id).map((tag) => tag.key || tag.value)
    }
    const normalizedOverview = normalizeOverview(overview)
    const tokenTypes = [...new Set([
      ...normalizedSessions.flatMap((session) => Object.keys(session.tokens)),
      ...pricingEntries.map((entry) => entry.tokenType),
      ...Object.keys(normalizedOverview.tokenCounts)
    ].filter(Boolean))]
    return {
      generatedAt: new Date().toISOString(),
      overview: normalizedOverview,
      sources: [...new Set(normalizedSessions.map((session) => session.source).filter(Boolean))],
      tools: [...new Set(normalizedComparisons.filter((row) => row.kind === 'tool').map((row) => row.name).filter(Boolean))],
      models: [...new Set(normalizedComparisons.filter((row) => row.kind === 'model').map((row) => row.name).filter(Boolean))],
      tokenTypes,
      daily: records(daily).map(normalizeDaily),
      monthly: records(monthly).map(normalizeDaily),
      heatmap: records(heatmap).map(normalizeDaily),
      comparisons: normalizedComparisons,
      sessions: normalizedSessions,
      tags: normalizedTags,
      capabilities: normalizedCapabilities.map((item) => `${item.adapterKind}: ${item.status} · ${item.formats.join(', ')}`),
      pricing: {
        version: stringValue(pricing, 'catalogVersion', 'version'),
        effectiveFrom: pricingEntries[0]?.effectiveFromUtc ?? '',
        unknownCount: normalizedOverview.unpricedCount,
        overrideCount: pricingEntries.filter((entry) => entry.isOverride).length,
        entries: pricingEntries
      }
    }
  }

  async search(query: string): Promise<SearchResult[]> {
    const payload = await this.json<{ results?: SearchResult[] }>(`/api/search?q=${encodeURIComponent(query)}`)
    return payload.results ?? []
  }

  async deleteAll(): Promise<void> {
    await this.json('/api/data', { method: 'DELETE', body: JSON.stringify({ clearAll: true }) })
  }

  async startSync(request: SyncRequest): Promise<SyncStatus> {
    return this.json<SyncStatus>('/api/sync', { method: 'POST', body: JSON.stringify(request) })
  }

  async getSyncStatus(syncId: string): Promise<SyncStatus> {
    return this.json<SyncStatus>(`/api/sync/${encodeURIComponent(syncId)}`)
  }

  async waitForSync(syncId: string, intervalMs = 100): Promise<SyncStatus> {
    for (let attempt = 0; attempt < 100; attempt++) {
      const status = await this.getSyncStatus(syncId)
      if (['completed', 'partial', 'failed'].includes(status.status)) return status
      await new Promise((resolve) => window.setTimeout(resolve, intervalMs))
    }
    throw new ApiError('同步狀態輪詢逾時')
  }

  async discoverSources(adapter: string, path?: string): Promise<SourceDiscoveryResult[]> {
    const params = new URLSearchParams({ adapter })
    if (path) params.set('path', path)
    return normalizeSourceDiscovery(await this.json(`/api/sources/discovery?${params.toString()}`))
  }

  async importFile(file: File, adapter?: string): Promise<unknown> {
    const content = typeof file.text === 'function' ? await file.text() : await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(String(reader.result ?? ''))
      reader.onerror = () => reject(reader.error ?? new ApiError('無法讀取匯入檔案'))
      reader.readAsText(file)
    })
    const selectedAdapter = adapter && adapter !== 'auto' ? adapter : this.adapterFromFileName(file.name)
    return this.json('/api/sources/import', { method: 'POST', body: JSON.stringify({ adapter: selectedAdapter, fileName: file.name, content }) })
  }

  async addTag(request: TagRequest): Promise<unknown> {
    return this.json('/api/tags', { method: 'POST', body: JSON.stringify(request) })
  }

  async deleteTag(scope: string, entityId: string, tagIdOrKey: string): Promise<void> {
    await this.json(`/api/tags/${encodeURIComponent(scope)}/${encodeURIComponent(entityId)}/${encodeURIComponent(tagIdOrKey)}`, { method: 'DELETE' })
  }

  async updatePricing(request: PriceWriteRequest): Promise<unknown> {
    return this.json('/api/pricing', { method: 'PUT', body: JSON.stringify(request) })
  }

  async export(format: 'csv' | 'json' | 'sqlite', query: Pick<DashboardQuery, 'from' | 'to' | 'timeZone'>): Promise<{ blob: Blob; warning: string | null }> {
    const response = await this.response('/api/export', { method: 'POST', body: JSON.stringify({ format, includeContent: format !== 'csv', from: query.from, to: query.to, timeZone: query.timeZone }) })
    return { blob: await response.blob(), warning: response.headers.get('X-Token-Dashboard-Export-Warning') }
  }

  private adapterFromFileName(fileName: string): string {
    const normalized = fileName.toLowerCase()
    if (normalized.includes('claude') && normalized.includes('app')) return 'claude-code-app'
    if (normalized.includes('claude')) return 'claude-code-cli'
    if (normalized.includes('codex') && normalized.includes('app')) return 'codex-app'
    if (normalized.includes('codex')) return 'codex-cli'
    throw new ApiError('無法從檔名判斷來源 adapter，請明確選取 adapter')
  }
}
