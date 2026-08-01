import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import App from './App.vue'
import { extractStartupKey, TokenDashboardClient } from './api'
import { isValidDateRange, resolveDateRange, resolveDayRange } from './dateRange'
import { formatTokenCount, formatUsd, totalTokens } from './types'

const fetchMock = vi.fn()

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } })
}

function dashboardFetch(input: RequestInfo | URL, init?: RequestInit): Response {
  const url = String(input)
  if (url.endsWith('/api/data') && init?.method === 'DELETE') return new Response(null, { status: 204 })
  if (url.endsWith('/api/tags') && init?.method === 'POST') return jsonResponse({ id: 'tag-1' })
  if (url.includes('/api/tags/session/s1/review') && init?.method === 'DELETE') return new Response(null, { status: 204 })
  if (url.includes('/api/tags?')) return jsonResponse([{ id: 'tag-1', key: 'review', value: 'keep', scope: 'session', entityId: 's1' }])
  if (url.endsWith('/api/sources/import') && init?.method === 'POST') return jsonResponse({ status: 'completed' })
  if (url.endsWith('/api/import-jobs/active')) return new Response(null, { status: 204 })
  if (url.includes('/api/dashboard-snapshot')) return jsonResponse({
    overview: { fromUtc: '2026-07-01T00:00:00Z', toUtc: '2026-07-08T00:00:00Z', timeZoneId: 'Asia/Taipei', eventCount: 1, sessionCount: 9, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, reasoningTokens: 5, fastTokens: 6, tokens: { input: 3, 'cached-input': 2, output: 4, reasoning: 5, fast: 6 }, cacheHitRate: 0.4, costUsd: null, partialCostUsd: 0.02, costCoverage: 0.6, unpriced: true, unpricedCount: 7 },
    trend: [{ bucketStartUtc: '2026-07-01T00:00:00Z', bucketEndUtc: '2026-07-01T01:00:00Z', date: '2026-07-01', eventCount: 1, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, cacheHitRate: 0.4, costUsd: null, partialCostUsd: 0.02, costCoverage: 0.6 }],
    monthly: [{ date: '2026-07', eventCount: 1, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, cacheHitRate: 0.4, costUsd: null, partialCostUsd: 0.02, costCoverage: 0.6 }],
    heatmap: [{ date: '2026-07-01', eventCount: 1, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, cacheHitRate: 0.4, costUsd: 0.02 }],
    comparisonTree: [{ kind: 'model', name: 'gpt-5-codex', tokens: 12, eventCount: 1, uniqueSessionCount: 1, turnCount: 1, averageTokens: 12, costUsd: 0.02, children: [{ kind: 'tool', name: 'rg', tokens: 12, eventCount: 1, uniqueSessionCount: 1, turnCount: 1, averageTokens: 12, costUsd: 0.02, children: [] }] }],
    sessions: [{ id: 's1', sourceId: 'codex-cli', workspaceId: 'C:/workspace/token-dashboard', startedAtUtc: '2026-07-01T00:00:00Z', lastActivityAtUtc: '2026-07-01T00:05:00Z', endedAtUtc: '2026-07-01T00:35:00Z', model: 'gpt-5-codex', tool: 'rg', tokens: { input: 3, output: 4, reasoning: 5, fast: 6 }, costUsd: null, partialCostUsd: 0.12, costCoverage: 0.5 }],
    nextCursor: null,
    hasMore: false
  })
  if (url.includes('/api/overview')) return jsonResponse({ fromUtc: '2026-07-01T00:00:00Z', toUtc: '2026-07-08T00:00:00Z', timeZoneId: 'Asia/Taipei', eventCount: 1, sessionCount: 9, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, reasoningTokens: 5, fastTokens: 6, tokens: { input: 3, 'cached-input': 2, output: 4, reasoning: 5, fast: 6 }, cacheHitRate: 0.4, costUsd: null, partialCostUsd: 0.02, costCoverage: 0.6, unpriced: true, unpricedCount: 7 })
  if (url.includes('/api/usage/trend')) return jsonResponse([{ bucketStartUtc: '2026-07-01T00:00:00Z', bucketEndUtc: '2026-07-01T01:00:00Z', date: '2026-07-01', eventCount: 1, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, cacheHitRate: 0.4, costUsd: null, partialCostUsd: 0.02, costCoverage: 0.6 }])
  if (url.includes('/api/usage/monthly')) return jsonResponse([{ date: '2026-07', eventCount: 1, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, cacheHitRate: 0.4, costUsd: null, partialCostUsd: 0.02, costCoverage: 0.6 }])
  if (url.includes('/api/comparisons/tree')) return jsonResponse([{ kind: 'model', name: 'gpt-5-codex', tokens: 12, eventCount: 1, uniqueSessionCount: 1, turnCount: 1, averageTokens: 12, costUsd: 0.02, children: [{ kind: 'tool', name: 'rg', tokens: 12, eventCount: 1, uniqueSessionCount: 1, turnCount: 1, averageTokens: 12, costUsd: 0.02, children: [] }] }])
  if (url.includes('/api/comparisons')) return jsonResponse([{ key: 'gpt-5-codex', eventCount: 1, inputTokens: 3, reasoningTokens: 5, outputTokens: 4, cacheHitRate: 0.4, costUsd: 0.02 }])
  if (url.includes('/api/heatmap')) return jsonResponse([{ date: '2026-07-01', eventCount: 1, inputTokens: 3, cachedInputTokens: 2, outputTokens: 4, cacheHitRate: 0.4, costUsd: 0.02 }])
  if (url.includes('/api/sessions/s1/timeline')) return jsonResponse({ sessionId: 's1', items: [], nextCursor: null, hasMore: false })
  if (url.includes('/api/sessions')) return jsonResponse([{ id: 's1', sourceId: 'codex-cli', workspaceId: 'C:/workspace/token-dashboard', startedAtUtc: '2026-07-01T00:00:00Z', lastActivityAtUtc: '2026-07-01T00:05:00Z', endedAtUtc: '2026-07-01T00:35:00Z', costUsd: null, partialCostUsd: 0.12, costCoverage: 0.5 }])
  if (url.includes('/api/sources/capabilities')) return jsonResponse([{ adapterKind: 'CodexCli', status: 'Available', formats: ['json'], notes: 'ready' }])
  if (url.includes('/api/pricing/unknown')) return jsonResponse([])
  if (url.includes('/api/pricing')) return jsonResponse({ catalogVersion: '2026-07-26', entries: [{ provider: 'openai', model: 'gpt-5-codex', tokenType: 'input', usdPerMillionTokens: 1, effectiveFrom: '2026-07-01', effectiveTo: '2026-08-01', isOverride: true }] })
  if (url.includes('/api/search')) return jsonResponse({ results: [{ itemId: 'e1', sourceId: 'codex-cli', sessionId: 's1', turnId: 't1', rank: 0.5 }] })
  return jsonResponse({}, 404)
}

beforeEach(() => {
  window.sessionStorage.clear()
  window.localStorage.clear()
  window.sessionStorage.setItem('token-dashboard-key', 'local-secret')
  window.history.replaceState({}, document.title, '/dashboard')
  fetchMock.mockReset()
  fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => dashboardFetch(input, init))
  vi.stubGlobal('fetch', fetchMock)
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: function (this: HTMLDialogElement) { this.open = false; this.dispatchEvent(new Event('close')) } })
})

describe('date range and statistics helpers', () => {
  it('resolves inclusive recent ranges and month boundaries', () => {
    expect(resolveDateRange('7', new Date('2026-07-07T10:00:00Z'))).toEqual({ startDate: '2026-07-01', endDate: '2026-07-07' })
    expect(resolveDateRange('last-month', new Date('2026-07-07T10:00:00Z'))).toEqual({ startDate: '2026-06-01', endDate: '2026-06-30' })
    expect(resolveDayRange(0, new Date('2026-07-07T10:00:00'))).toEqual({ startDate: '2026-07-07', endDate: '2026-07-07' })
    expect(resolveDayRange(-1, new Date('2026-07-07T10:00:00'))).toEqual({ startDate: '2026-07-06', endDate: '2026-07-06' })
    expect(isValidDateRange({ startDate: '2026-07-08', endDate: '2026-07-07' })).toBe(false)
  })

  it('formats token details and keeps unknown prices explicit', () => {
    expect(totalTokens({ input: 10, output: 20, cacheRead: 5, cacheWrite: 2 })).toBe(37)
    expect(formatUsd(null)).toBe('未知價格')
    expect(formatTokenCount(999)).toBe('999')
    expect(formatTokenCount(240300)).toBe('240.3k')
    expect(formatTokenCount(12800000)).toBe('12.8m')
  })
})

describe('startup key boundary', () => {
  it('extracts, stores, and removes the startup fragment while preserving path and search', () => {
    window.history.replaceState({}, document.title, '/dashboard?tab=overview#key=local-secret')
    expect(extractStartupKey()).toBe('local-secret')
    expect(window.sessionStorage.getItem('token-dashboard-key')).toBe('local-secret')
    expect(window.location.pathname + window.location.search).toBe('/dashboard?tab=overview')
    expect(window.location.hash).toBe('')
  })

  it('normalizes the startup entry back to the root path', () => {
    window.history.replaceState({}, document.title, '/index.html#key=local-secret')
    expect(extractStartupKey()).toBe('local-secret')
    expect(window.location.pathname).toBe('/')
    expect(window.location.hash).toBe('')
  })

  it('returns null when no fragment or session key exists', () => {
    window.sessionStorage.clear()
    expect(extractStartupKey()).toBeNull()
  })
})

describe('formal API client contract', () => {
  it('loads one snapshot and keeps session details lazy', async () => {
    const client = new TokenDashboardClient()
    const data = await client.getDashboard({ preset: '7d', from: '2026-07-01', to: '2026-07-07', timeZone: 'Asia/Taipei' })
    expect(data.overview.eventCount).toBe(1)
    expect(data.sessions[0]?.tokens).toEqual({ input: 3, output: 4, reasoning: 5, fast: 6 })
    expect(data.comparisonTree[0]?.name).toBe('gpt-5-codex')
    expect(data.comparisonTree[0]?.children[0]?.name).toBe('rg')
    expect(data.comparisonTree[0]?.children[0]?.tokens).toBe(12)
    expect(data.tokenTypes).toEqual(expect.arrayContaining(['reasoning', 'fast']))
    expect(data.overview.sessionCount).toBe(9)
    expect(data.sessions[0]?.costUsd).toBeNull()
    expect(data.sessions[0]?.partialCostUsd).toBe(0.12)
    expect(data.sessions[0]?.costCoverage).toBe(0.5)
    expect(data.sessions[0]?.workspaceId).toBe('C:/workspace/token-dashboard')
    expect(data.tags[0]?.key).toBe('review')
    expect(data.pricing.overrideCount).toBe(1)
    expect(data.pricing.unknownCount).toBe(7)
    expect(data.pricing.entries[0]?.effectiveFromUtc).toBe('2026-07-01')
    expect(data.pricing.entries[0]?.effectiveToUtc).toBe('2026-08-01')
    const urls = fetchMock.mock.calls.map(([input]) => String(input))
    expect(urls.some((url) => url.includes('/api/dashboard?') || url.endsWith('/api/dashboard'))).toBe(false)
    expect(urls.some((url) => url.includes('/api/dashboard-snapshot?preset=7d&from=2026-07-01&to=2026-07-07&timeZone=Asia%2FTaipei'))).toBe(true)
    expect(urls.some((url) => url.includes('/api/sessions/s1'))).toBe(false)
    expect(fetchMock).toHaveBeenCalledTimes(4)
  })

  it('passes every dashboard filter to the snapshot route', async () => {
    await new TokenDashboardClient().getDashboard({ preset: '7d', from: '2026-07-01', to: '2026-07-07', timeZone: 'UTC', sourceId: 'codex-cli', tool: 'rg', model: 'gpt-5-codex', tokenType: 'reasoning' })
    const urls = fetchMock.mock.calls.map(([input]) => String(input)).filter((url) => url.includes('/api/dashboard-snapshot') || url.includes('/api/sources/capabilities') || url.includes('/api/pricing?') || url.includes('/api/tags?'))
    expect(urls.length).toBe(4)
    for (const url of urls) {
      expect(url).toContain('sourceId=codex-cli')
      expect(url).not.toContain('source=codex-cli')
      expect(url).toContain('tool=rg')
      expect(url).toContain('model=gpt-5-codex')
      expect(url).toContain('tokenType=reasoning')
    }
  })

  it('does not fallback to fixture data when a formal route fails', async () => {
    fetchMock.mockRejectedValueOnce(new Error('network down'))
    await expect(new TokenDashboardClient().getDashboard({ preset: '30d', from: '2026-07-01', to: '2026-07-30', timeZone: 'UTC' })).rejects.toThrow('network down')
  })

  it('parses the search envelope and sends formal bodies', async () => {
    const client = new TokenDashboardClient()
    await expect(client.search('tool')).resolves.toHaveLength(1)
    await client.deleteAll()
    const deleteCall = fetchMock.mock.calls.find(([input, init]) => String(input).endsWith('/api/data') && init?.method === 'DELETE')
    expect(JSON.parse(String(deleteCall?.[1]?.body))).toMatchObject({ clearAll: true, removeManagedSources: false })

    await client.addTag({ scope: 'session', entityId: 's1', key: 'review', value: '' })
    await client.deleteTag('session', 's1', 'review')
    await client.updatePricing({ provider: 'openai', model: 'gpt-5-codex', tokenType: 'input', usdPerMillionTokens: 1 })
    const tagCall = fetchMock.mock.calls.find(([input]) => String(input).endsWith('/api/tags'))
    const deleteTagCall = fetchMock.mock.calls.find(([input, init]) => String(input).includes('/api/tags/session/s1/review') && init?.method === 'DELETE')
    const pricingCall = fetchMock.mock.calls.find(([input, init]) => String(input).endsWith('/api/pricing') && init?.method === 'PUT')
    expect(JSON.parse(String(tagCall?.[1]?.body)).scope).toBe('session')
    expect(String(deleteTagCall?.[0])).toBe('/api/tags/session/s1/review')
    expect(String(pricingCall?.[1]?.method)).toBe('PUT')
  })

  it('reads import content, posts adapter payload, and exports with warning headers', async () => {
    const client = new TokenDashboardClient()
    const file = new File(['{"event":"tool"}'], 'codex-log.json', { type: 'application/json' })
    await client.importFile(file, 'codex-cli')
    const importCall = fetchMock.mock.calls.find(([input]) => String(input).endsWith('/api/sources/import'))
    expect(JSON.parse(String(importCall?.[1]?.body))).toEqual({ adapter: 'codex-cli', fileName: 'codex-log.json', content: '{"event":"tool"}' })

    fetchMock.mockImplementationOnce(async () => new Response('sqlite', { headers: { 'X-Token-Dashboard-Export-Warning': 'contains content' } }))
    const exported = await client.export('sqlite', { from: '2026-07-01', to: '2026-07-07', timeZone: 'UTC' })
    expect(exported.warning).toBe('contains content')
    const exportCall = fetchMock.mock.calls.at(-1)
    expect(String(exportCall?.[0])).toBe('/api/export')
    expect(JSON.parse(String(exportCall?.[1]?.body))).toMatchObject({ format: 'sqlite', includeContent: true, from: '2026-07-01', to: '2026-07-07', timeZone: 'UTC' })
  })

  it('starts sync, polls status, and supports source discovery', async () => {
    fetchMock.mockImplementationOnce(async () => jsonResponse({ syncId: 'sync-1', status: 'queued' }))
      .mockImplementationOnce(async () => jsonResponse({ syncId: 'sync-1', status: 'completed' }))
      .mockImplementationOnce(async () => jsonResponse({ paths: [{ path: 'C:/logs', exists: true }] }))
    const client = new TokenDashboardClient()
    const started = await client.startSync({ adapter: 'codex-cli', paths: ['C:/logs'] })
    const status = await client.waitForSync(started.syncId, 0)
    await client.discoverSources('codex-cli', 'C:/logs')
    expect(status.status).toBe('completed')
    expect(JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body))).toEqual({ adapter: 'codex-cli', paths: ['C:/logs'] })
    expect(String(fetchMock.mock.calls[2]?.[0])).toContain('/api/sources/discovery?adapter=codex-cli&path=C%3A%2Flogs')
  })

  it('passes auto discovery through and normalizes all four adapter results', async () => {
    fetchMock.mockImplementationOnce(async () => jsonResponse([
      { adapter: 'ClaudeCodeApp', capabilities: { adapterKind: 'ClaudeCodeApp', status: 'Available', formats: ['json'], notes: '' }, paths: [{ path: 'C:/claude-app', exists: true }] },
      { adapter: 'ClaudeCodeCli', capabilities: { adapterKind: 'ClaudeCodeCli', status: 'NotFound', formats: ['jsonl'], notes: '' }, paths: [] },
      { adapter: 'CodexApp', capabilities: { adapterKind: 'CodexApp', status: 'Available', formats: ['json'], notes: '' }, paths: [{ path: 'C:/codex-app', exists: true }] },
      { adapter: 'CodexCli', capabilities: { adapterKind: 'CodexCli', status: 'Available', formats: ['jsonl'], notes: '' }, paths: [{ path: 'C:/codex-cli', exists: true }] }
    ]))
    const results = await new TokenDashboardClient().discoverSources('auto')
    expect(String(fetchMock.mock.calls[0]?.[0])).toBe('/api/sources/discovery?adapter=auto')
    expect(results.map((result) => result.adapter)).toEqual(['ClaudeCodeApp', 'ClaudeCodeCli', 'CodexApp', 'CodexCli'])
    expect(results[2]?.paths).toEqual(['C:/codex-app'])
  })
})

describe('dashboard API states and interaction surface', () => {
  it('renders formal data without partial fixture fallback', async () => {
    const wrapper = mount(App)
    await flushPromises()
    expect(wrapper.find('.comparison-panel .panel-header').exists()).toBe(false)
    expect(wrapper.get('.comparison-panel').attributes('aria-label')).toBe('模型與工具 token 矩形樹狀圖')
    expect(wrapper.text()).toContain('C:/workspace/token-dashboard')
    expect(wrapper.text()).toContain('C:/workspace/token-dashboard')
    expect(wrapper.text()).toContain('成本覆蓋率')
    expect(wrapper.text()).not.toContain('fixture')
    expect(wrapper.text()).not.toContain('部分同步')
  })

  it('renders explicit authorization error without a key', async () => {
    window.sessionStorage.clear()
    const wrapper = mount(App)
    await flushPromises()
    expect(wrapper.text()).toContain('缺少 localhost session key')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('calls API search from the full-text control and opens timeline detail', async () => {
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.get('#full-search').setValue('tool')
    await wrapper.get('#full-search').trigger('input')
    await flushPromises()
    expect(wrapper.text()).toContain('C:/workspace/token-dashboard')
    await wrapper.get('.session-row').trigger('click')
    expect(wrapper.find('.session-drawer').exists()).toBe(true)
    expect(wrapper.text()).toContain('SESSION TIMELINE')
    expect(wrapper.text()).toContain('Ctrl K')
  })

  it('switches cost and token trend views while persisting the chosen granularity', async () => {
    const wrapper = mount(App)
    await flushPromises()
    expect(wrapper.text()).toContain('各時段的已知成本')
    const trendButtons = wrapper.findAll('.trend-controls button')
    await trendButtons.find((button) => button.text() === '每週')!.trigger('click')
    await flushPromises()
    expect(window.localStorage.getItem('token-dashboard.trend-granularity')).toBe('weekly')
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/api/dashboard-snapshot') && String(input).includes('interval=7d'))).toBe(true)
    await trendButtons.find((button) => button.text() === 'Token')!.trigger('click')
    await nextTick()
    expect(window.localStorage.getItem('token-dashboard.trend-metric')).toBe('tokens')
  })

  it('supports interactive heatmap and critical accessible controls', async () => {
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.find('[role="gridcell"]').trigger('click')
    expect((wrapper.get('#full-search').element as HTMLInputElement).value).toBe('2026-07-01')
    expect(wrapper.find('button[aria-label="切換為暗色模式"]').exists()).toBe(true)
    expect(wrapper.find('button[aria-label="刪除資料"]').exists()).toBe(true)
  })

  it('keeps the mobile control rail collapsed until its keyboard-capable toggle is used', async () => {
    const wrapper = mount(App)
    await flushPromises()
    const toggle = wrapper.get('#control-toggle')
    expect(toggle.element.tagName).toBe('BUTTON')
    expect(toggle.attributes('aria-controls')).toBe('control-rail')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    expect(wrapper.get('#control-rail').classes('rail-open')).toBe(false)
    expect(wrapper.find('.kpi-grid').exists()).toBe(true)

    await toggle.trigger('click')
    expect(toggle.attributes('aria-expanded')).toBe('true')
    expect(wrapper.get('#control-rail').classes('rail-open')).toBe(true)
    await toggle.trigger('click')
    expect(toggle.attributes('aria-expanded')).toBe('false')
  })

  it('uses native modal lifecycle for destructive and sensitive-content confirmations', async () => {
    const wrapper = mount(App, { attachTo: document.body })
    await flushPromises()

    const deleteTrigger = wrapper.get('button[aria-label="刪除資料"]')
    await deleteTrigger.trigger('click')
    await flushPromises()
    await nextTick()
    await nextTick()
    const deleteDialog = wrapper.get('dialog[aria-labelledby="delete-heading"]')
    expect((deleteDialog.element as HTMLDialogElement).open).toBe(true)
    expect(document.activeElement).toBe(deleteDialog.get('[data-autofocus]').element)
    await deleteDialog.trigger('cancel')
    expect((deleteDialog.element as HTMLDialogElement).open).toBe(false)
    expect(document.activeElement).toBe(deleteTrigger.element)

    const exportTrigger = wrapper.findAll('button').find((button) => button.text() === 'JSON')
    expect(exportTrigger).toBeDefined()
    await exportTrigger!.trigger('click')
    await flushPromises()
    const exportDialog = wrapper.get('dialog[aria-labelledby="export-heading"]')
    expect((exportDialog.element as HTMLDialogElement).open).toBe(true)
    await exportDialog.get('[data-autofocus]').trigger('click')
    expect((exportDialog.element as HTMLDialogElement).open).toBe(false)
  })

  it('rejects an effective-to date that is not after effective-from without a PUT', async () => {
    const wrapper = mount(App)
    await flushPromises()
    await wrapper.findAll('.topbar-nav button')[1]!.trigger('click')
    await nextTick()
    const pricingDates = wrapper.find('.pricing-editor').findAll('input[type="date"]')
    await pricingDates[0]!.setValue('2026-07-10')
    await pricingDates[1]!.setValue('2026-07-10')
    expect((pricingDates[0]!.element as HTMLInputElement).value).toBe('2026-07-10')
    expect((pricingDates[1]!.element as HTMLInputElement).value).toBe('2026-07-10')
    const before = fetchMock.mock.calls.length
    await wrapper.findAll('button').find((button) => button.text() === '儲存 override')!.trigger('click')
    await nextTick()
    expect(wrapper.text()).toContain('有效日期錯誤：effective to 必須晚於 effective from')
    expect(fetchMock.mock.calls.length).toBe(before)
  })
})
