import React, { useCallback, useEffect, useMemo, useState } from 'react'
import {
  PALETTE_AREA,
  ROUTES_AREA,
  SIDEBAR_NAV_AREA,
  STATUSBAR_AREAS,
  compactNumber,
  host,
  useQuery
} from '@hermes/plugin-sdk'

const e = React.createElement
let pluginContext = null

const usd = new Intl.NumberFormat(undefined, {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 4
})
const integer = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 })
const dateTime = new Intl.DateTimeFormat(undefined, {
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit'
})

const colors = {
  border: 'var(--ui-stroke-secondary)',
  muted: 'var(--ui-text-tertiary)',
  subtle: 'var(--ui-text-quaternary)',
  accent: 'var(--ui-accent)'
}

const styles = {
  page: {
    height: '100%',
    minHeight: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: 12,
    padding: 14,
    overflow: 'hidden'
  },
  toolbar: { display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center' },
  group: { display: 'flex', flexWrap: 'wrap', gap: 4, alignItems: 'center' },
  label: { color: colors.muted, fontSize: 11, textTransform: 'uppercase', letterSpacing: '0.06em' },
  card: { border: `1px solid ${colors.border}`, borderRadius: 7, padding: 10, minWidth: 0 },
  metricValue: { fontSize: 20, fontWeight: 650, fontVariantNumeric: 'tabular-nums' },
  metricNote: { color: colors.muted, fontSize: 11, marginTop: 3 },
  body: { minHeight: 0, overflow: 'auto', display: 'flex', flexDirection: 'column', gap: 8 },
  tableWrap: { overflowX: 'auto', borderTop: `1px solid ${colors.border}`, marginTop: 8 },
  table: { width: '100%', borderCollapse: 'collapse', fontSize: 12, fontVariantNumeric: 'tabular-nums' },
  th: { padding: '7px 8px', color: colors.muted, fontWeight: 500, textAlign: 'right', whiteSpace: 'nowrap' },
  td: { padding: '7px 8px', borderTop: `1px solid ${colors.border}`, textAlign: 'right', whiteSpace: 'nowrap' },
  left: { textAlign: 'left' },
  input: {
    height: 30,
    minWidth: 190,
    border: `1px solid ${colors.border}`,
    borderRadius: 6,
    padding: '0 9px',
    background: 'transparent',
    color: 'inherit',
    outline: 'none'
  },
  select: {
    height: 30,
    border: `1px solid ${colors.border}`,
    borderRadius: 6,
    padding: '0 8px',
    background: 'var(--ui-editor-background)',
    color: 'inherit'
  }
}

function buttonStyle(active = false) {
  return {
    height: 28,
    border: `1px solid ${active ? colors.accent : colors.border}`,
    borderRadius: 6,
    padding: '0 8px',
    background: active ? 'color-mix(in srgb, var(--ui-accent) 18%, transparent)' : 'transparent',
    color: 'inherit',
    cursor: 'pointer',
    fontSize: 12
  }
}

function Pill({ active, children, onClick, title, disabled }) {
  return e('button', { type: 'button', style: buttonStyle(active), onClick, title, disabled }, children)
}

function MetricCard({ label, value, note }) {
  return e('div', { style: styles.card },
    e('div', { style: styles.label }, label),
    e('div', { style: styles.metricValue }, value),
    e('div', { style: styles.metricNote }, note)
  )
}

function Coverage({ result }) {
  const priced = result.apiEquivalentPricedTokens || 0
  const total = priced + (result.apiEquivalentUnpricedTokens || 0)
  return total ? `${(priced / total * 100).toFixed(1)}% token coverage` : 'no priced tokens'
}

function UsageTable({ lines, costBasis }) {
  if (!lines || !lines.length) {
    return e('div', { style: { ...styles.metricNote, paddingTop: 8 } }, 'No model usage lines recorded.')
  }
  const headers = ['Task', 'Model / provider', 'Calls', 'Input', 'Cache', 'Output', 'Reasoning', 'Tokens', costBasis === 'api-equivalent' ? 'API equivalent' : 'Recorded']
  return e('div', { style: styles.tableWrap },
    e('table', { style: styles.table },
      e('thead', null, e('tr', null, headers.map((header, index) =>
        e('th', { key: header, style: { ...styles.th, ...(index < 2 ? styles.left : {}) } }, header)
      ))),
      e('tbody', null, lines.map((line, index) => {
        const displayedCost = costBasis === 'api-equivalent' ? line.apiEquivalentCostUsd : line.estimatedCostUsd
        return e('tr', { key: `${line.model}:${line.task}:${index}` },
          e('td', { style: { ...styles.td, ...styles.left } }, line.task),
          e('td', { style: { ...styles.td, ...styles.left } },
            e('div', { style: { fontWeight: 550 } }, line.model),
            e('div', { style: { color: colors.muted, fontSize: 10 } }, `${line.provider || 'unattributed'}${line.billingMode ? ` · ${line.billingMode}` : ''}`)
          ),
          e('td', { style: styles.td }, integer.format(line.apiCalls || 0)),
          e('td', { style: styles.td }, integer.format(line.inputTokens || 0)),
          e('td', { style: styles.td }, integer.format((line.cacheReadTokens || 0) + (line.cacheWriteTokens || 0))),
          e('td', { style: styles.td }, integer.format(line.outputTokens || 0)),
          e('td', { style: styles.td }, integer.format(line.reasoningTokens || 0)),
          e('td', { style: { ...styles.td, fontWeight: 600 } }, integer.format(line.accountedTokens || 0)),
          e('td', { style: styles.td, title: line.apiEquivalentPricingProvider || '' }, displayedCost == null ? '—' : usd.format(displayedCost))
        )
      }))
    )
  )
}

function SessionBlock({ session, costBasis }) {
  const copyId = async () => {
    const copied = await pluginContext.os.writeClipboard(session.id)
    host.notify({ kind: copied ? 'success' : 'error', message: copied ? 'Session ID copied' : 'Clipboard unavailable' })
  }
  return e('div', { style: { borderTop: `1px solid ${colors.border}`, paddingTop: 8, marginTop: 8 } },
    e('div', { style: { display: 'flex', gap: 8, alignItems: 'baseline', flexWrap: 'wrap' } },
      e('strong', null, session.title || session.id),
      e('span', { style: styles.metricNote }, session.isChild ? 'child process' : 'root process'),
      e('span', { style: styles.metricNote }, `${dateTime.format(new Date(session.startedAt))} · ${session.status}`),
      e('button', { type: 'button', onClick: copyId, style: { ...buttonStyle(false), height: 22, fontSize: 10 } }, 'copy ID')
    ),
    e(UsageTable, { lines: session.usageLines, costBasis })
  )
}

function FamilyCard({ family, costBasis, expanded, onToggle }) {
  const lead = family.sessions[0]
  const title = lead?.title || family.rootSessionId
  const displayedCost = costBasis === 'api-equivalent' ? family.apiEquivalentCostUsd : family.estimatedCostUsd
  return e('section', { style: styles.card },
    e('div', { style: { display: 'flex', justifyContent: 'space-between', gap: 12, alignItems: 'flex-start', flexWrap: 'wrap' } },
      e('div', { style: { minWidth: 0 } },
        e('div', { style: { display: 'flex', gap: 7, alignItems: 'center', flexWrap: 'wrap' } },
          e('strong', { style: { overflowWrap: 'anywhere' } }, title),
          e('span', { style: { ...styles.label, textTransform: 'none' } }, family.profile),
          family.sessions.length > 1 ? e('span', { style: styles.label }, `${family.sessions.length} processes`) : null,
          !family.rootIncluded ? e('span', { style: styles.label }, 'root outside result') : null
        ),
        e('div', { style: styles.metricNote }, `${dateTime.format(new Date(family.startedAt))} · ${compactNumber(family.accountedTokens)} tokens · ${compactNumber(family.apiCalls)} calls · ${usd.format(displayedCost || 0)}`)
      ),
      family.sessions.length > 1
        ? e(Pill, { active: expanded, onClick: onToggle }, expanded ? 'Collapse processes' : 'Show processes')
        : null
    ),
    expanded
      ? family.sessions.map(session => e(SessionBlock, { key: session.id, session, costBasis }))
      : e(UsageTable, { lines: family.usageLines, costBasis })
  )
}

function Summary({ result, costBasis }) {
  const apiEquivalent = costBasis === 'api-equivalent'
  const cost = apiEquivalent ? result.apiEquivalentCostUsd : result.estimatedCostUsd
  return e('div', { style: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(145px, 1fr))', gap: 8 } },
    e(MetricCard, { label: 'Sessions', value: integer.format(result.filteredSessions), note: `${integer.format(result.totalSessions)} stored` }),
    e(MetricCard, { label: 'Accounted tokens', value: compactNumber(result.accountedTokens), note: 'input + cache + output' }),
    e(MetricCard, { label: 'Reasoning', value: compactNumber(result.reasoningTokens), note: 'reported separately' }),
    e(MetricCard, { label: 'API calls', value: compactNumber(result.apiCalls), note: `${result.filteredFamilies} families` }),
    e(MetricCard, { label: apiEquivalent ? 'API equivalent' : 'Recorded estimate', value: usd.format(cost || 0), note: apiEquivalent ? e(Coverage, { result }) : `${usd.format(result.actualCostUsd || 0)} actual` })
  )
}

function ErrorPanel({ error, retry }) {
  const message = error?.message || String(error || 'Unknown error')
  return e('div', { style: { ...styles.card, borderColor: 'var(--ui-error, var(--ui-accent))' } },
    e('strong', null, 'Session usage backend unavailable'),
    e('div', { style: { ...styles.metricNote, margin: '5px 0 8px' } }, message),
    e(Pill, { onClick: retry }, 'Retry')
  )
}

function SessionUsagePage() {
  const saved = pluginContext.storage.get('preferences', {}) || {}
  const [selected, setSelected] = useState(() => Array.isArray(saved.profiles) ? saved.profiles : [])
  const [windowName, setWindowName] = useState(saved.window || '24h')
  const [costBasis, setCostBasis] = useState(saved.costBasis || 'api-equivalent')
  const [sort, setSort] = useState(saved.sort || 'tokens')
  const [searchDraft, setSearchDraft] = useState('')
  const [search, setSearch] = useState('')
  const [offset, setOffset] = useState(0)
  const [expanded, setExpanded] = useState(() => new Set())
  const limit = 50

  const profilesQuery = useQuery({
    queryKey: ['session-usage', 'profiles'],
    queryFn: () => pluginContext.rest('/profiles'),
    staleTime: 30000,
    refetchInterval: 60000
  })

  useEffect(() => {
    const profiles = profilesQuery.data || []
    if (!profiles.length) return
    const available = new Set(profiles.map(profile => profile.name))
    if (!selected.length || (!selected.includes('all') && !selected.some(name => available.has(name)))) {
      setSelected([profiles[0].name])
    }
  }, [profilesQuery.data, selected])

  useEffect(() => {
    const handle = window.setTimeout(() => {
      setSearch(searchDraft.trim())
      setOffset(0)
    }, 300)
    return () => window.clearTimeout(handle)
  }, [searchDraft])

  useEffect(() => {
    pluginContext.storage.set('preferences', { profiles: selected, window: windowName, costBasis, sort })
  }, [selected, windowName, costBasis, sort])

  const request = useMemo(() => ({
    profiles: selected,
    window: windowName,
    search,
    sort,
    costBasis,
    descending: true,
    limit,
    offset
  }), [selected, windowName, search, sort, costBasis, offset])

  const metricsQuery = useQuery({
    queryKey: ['session-usage', 'metrics', selected.join(','), windowName, search, sort, costBasis, offset],
    queryFn: () => pluginContext.rest('/metrics', { method: 'POST', body: request, timeoutMs: 30000 }),
    enabled: selected.length > 0,
    refetchInterval: 15000
  })

  const toggleProfile = useCallback((name) => {
    setOffset(0)
    setSelected(current => {
      if (name === 'all') return ['all']
      if (current.includes('all')) return [name]
      if (current.includes(name)) return current.length > 1 ? current.filter(item => item !== name) : current
      return [...current, name]
    })
  }, [])

  const result = metricsQuery.data
  const profiles = profilesQuery.data || []
  const first = result?.filteredFamilies ? offset + 1 : 0
  const last = result ? Math.min(offset + result.families.length, result.filteredFamilies) : 0

  return e('main', { style: styles.page },
    e('header', { style: { display: 'flex', flexDirection: 'column', gap: 9 } },
      e('div', { style: { display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 12 } },
        e('div', null,
          e('h1', { style: { fontSize: 16, fontWeight: 650, margin: 0 } }, 'Session usage'),
          e('div', { style: styles.metricNote }, 'Read-only local token and API-equivalent pricing telemetry')
        ),
        e('div', { style: styles.metricNote }, metricsQuery.isFetching ? 'Refreshing…' : (result ? `Live · ${result.queryElapsedMilliseconds} ms` : 'Connecting…'))
      ),
      e('div', { style: styles.toolbar },
        e('div', { style: styles.group },
          e('span', { style: styles.label }, 'Profiles'),
          e(Pill, { active: selected.includes('all'), onClick: () => toggleProfile('all') }, 'All'),
          profiles.map(profile => e(Pill, {
            key: profile.name,
            active: selected.includes(profile.name),
            onClick: () => toggleProfile(profile.name),
            title: `${(profile.sizeBytes / 1e6).toFixed(1)} MB`
          }, profile.name))
        ),
        e('div', { style: styles.group },
          ['24h', '7d', '30d', 'all'].map(value => e(Pill, {
            key: value,
            active: windowName === value,
            onClick: () => { setWindowName(value); setOffset(0) }
          }, value === 'all' ? 'All time' : value))
        ),
        e('div', { style: styles.group },
          e(Pill, { active: costBasis === 'api-equivalent', onClick: () => { setCostBasis('api-equivalent'); setOffset(0) } }, 'API equivalent'),
          e(Pill, { active: costBasis === 'recorded', onClick: () => { setCostBasis('recorded'); setOffset(0) } }, 'Recorded')
        ),
        e('input', {
          style: styles.input,
          value: searchDraft,
          onChange: event => setSearchDraft(event.target.value),
          placeholder: 'Search sessions, models, tasks…',
          'aria-label': 'Search session usage'
        }),
        e('select', {
          style: styles.select,
          value: sort,
          onChange: event => { setSort(event.target.value); setOffset(0) },
          'aria-label': 'Sort session families'
        },
          e('option', { value: 'tokens' }, 'Sort: tokens'),
          e('option', { value: 'cost' }, 'Sort: cost'),
          e('option', { value: 'calls' }, 'Sort: calls'),
          e('option', { value: 'started' }, 'Sort: started')
        ),
        e(Pill, { onClick: () => { profilesQuery.refetch(); metricsQuery.refetch() } }, 'Refresh')
      )
    ),
    profilesQuery.error ? e(ErrorPanel, { error: profilesQuery.error, retry: profilesQuery.refetch }) : null,
    metricsQuery.error ? e(ErrorPanel, { error: metricsQuery.error, retry: metricsQuery.refetch }) : null,
    result ? e(Summary, { result, costBasis }) : null,
    e('div', { style: styles.body },
      !result && metricsQuery.isFetching ? e('div', { style: styles.metricNote }, 'Loading session metrics…') : null,
      result && !result.families.length ? e('div', { style: { ...styles.card, color: colors.muted } }, 'No session families match this window and filter.') : null,
      result ? result.families.map(family => {
        const key = `${family.profile}:${family.rootSessionId}`
        return e(FamilyCard, {
          key,
          family,
          costBasis,
          expanded: expanded.has(key),
          onToggle: () => setExpanded(current => {
            const next = new Set(current)
            if (next.has(key)) next.delete(key)
            else next.add(key)
            return next
          })
        })
      }) : null
    ),
    result ? e('footer', { style: { ...styles.toolbar, justifyContent: 'space-between' } },
      e('div', { style: styles.metricNote }, `${integer.format(first)}–${integer.format(last)} of ${integer.format(result.filteredFamilies)} families · snapshot ${dateTime.format(new Date(result.generatedAt))}`),
      e('div', { style: styles.group },
        e(Pill, { disabled: offset === 0, onClick: () => setOffset(Math.max(0, offset - limit)) }, 'Previous'),
        e(Pill, { disabled: offset + result.families.length >= result.filteredFamilies, onClick: () => setOffset(offset + limit) }, 'Next')
      )
    ) : null
  )
}

function StatusChip() {
  const query = useQuery({
    queryKey: ['session-usage', 'status'],
    queryFn: () => pluginContext.rest('/metrics', {
      method: 'POST',
      body: { profiles: 'all', window: '24h', limit: 1, offset: 0 },
      timeoutMs: 30000
    }),
    refetchInterval: 60000,
    retry: false
  })
  const label = query.data ? `${compactNumber(query.data.accountedTokens)} tokens · ${usd.format(query.data.apiEquivalentCostUsd || 0)}` : 'session usage'
  return e('button', {
    type: 'button',
    style: { border: 0, background: 'transparent', color: colors.muted, fontSize: 11, cursor: 'pointer', padding: '0 6px' },
    onClick: () => host.navigate('/session-usage'),
    title: 'Open session usage'
  }, label)
}

export default {
  id: 'session-usage',
  name: 'Session Usage',
  defaultEnabled: true,
  register(ctx) {
    pluginContext = ctx
    ctx.registerMany([
      {
        id: 'page',
        area: ROUTES_AREA,
        data: { path: '/session-usage' },
        render: () => e(SessionUsagePage)
      },
      {
        id: 'nav',
        area: SIDEBAR_NAV_AREA,
        order: 75,
        data: { path: '/session-usage', label: 'Session usage', codicon: 'pulse' }
      },
      {
        id: 'open',
        area: PALETTE_AREA,
        data: {
          id: 'session-usage.open',
          label: 'Open Session Usage',
          keywords: ['tokens', 'cost', 'sessions', 'metrics'],
          run: () => host.navigate('/session-usage')
        }
      },
      {
        id: 'status',
        area: STATUSBAR_AREAS.right,
        order: 115,
        render: () => e(StatusChip)
      }
    ])
  }
}
