export type DatePreset = '30' | '7' | '90' | 'month' | 'last-month' | 'custom'

export interface DateRange {
  startDate: string
  endDate: string
}

function isoDate(date: Date): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function resolveDateRange(preset: DatePreset, now = new Date()): DateRange {
  const end = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  if (preset === 'month') {
    return { startDate: isoDate(new Date(end.getFullYear(), end.getMonth(), 1)), endDate: isoDate(end) }
  }
  if (preset === 'last-month') {
    return {
      startDate: isoDate(new Date(end.getFullYear(), end.getMonth() - 1, 1)),
      endDate: isoDate(new Date(end.getFullYear(), end.getMonth(), 0))
    }
  }
  const days = preset === '7' ? 6 : preset === '90' ? 89 : 29
  const start = new Date(end)
  start.setDate(start.getDate() - days)
  return { startDate: isoDate(start), endDate: isoDate(end) }
}

export function isValidDateRange(range: DateRange): boolean {
  return range.startDate.length === 10 && range.endDate.length === 10 && range.startDate <= range.endDate
}
