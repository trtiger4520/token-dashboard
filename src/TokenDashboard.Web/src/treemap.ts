import type { ComparisonTreeNode } from './types'

export interface TreemapRect {
  x: number
  y: number
  width: number
  height: number
  depth: number
  node: ComparisonTreeNode
}

interface WeightedNode {
  node: ComparisonTreeNode
  value: number
}

function worst(row: WeightedNode[], side: number): number {
  if (!row.length) return Number.POSITIVE_INFINITY
  const sum = row.reduce((total, item) => total + item.value, 0)
  const max = Math.max(...row.map((item) => item.value))
  const min = Math.min(...row.map((item) => item.value))
  return Math.max((side * side * max) / (sum * sum), (sum * sum) / (side * side * Math.max(min, 1)))
}

function placeRow(row: WeightedNode[], x: number, y: number, width: number, height: number): { rects: Array<{ item: WeightedNode; x: number; y: number; width: number; height: number }>; x: number; y: number; width: number; height: number } {
  const total = row.reduce((sum, item) => sum + item.value, 0)
  const rects: Array<{ item: WeightedNode; x: number; y: number; width: number; height: number }> = []
  if (width >= height) {
    const rowHeight = total / Math.max(width, 1)
    let cursor = x
    for (const item of row) {
      const itemWidth = item.value / Math.max(rowHeight, 1)
      rects.push({ item, x: cursor, y, width: itemWidth, height: rowHeight })
      cursor += itemWidth
    }
    return { rects, x, y: y + rowHeight, width, height: Math.max(0, height - rowHeight) }
  }

  const rowWidth = total / Math.max(height, 1)
  let cursor = y
  for (const item of row) {
    const itemHeight = item.value / Math.max(rowWidth, 1)
    rects.push({ item, x, y: cursor, width: rowWidth, height: itemHeight })
    cursor += itemHeight
  }
  return { rects, x: x + rowWidth, y, width: Math.max(0, width - rowWidth), height }
}

function layoutNodes(nodes: ComparisonTreeNode[], x: number, y: number, width: number, height: number, depth: number): TreemapRect[] {
  const visibleNodes = nodes.filter((node) => node.tokens > 0)
  const totalTokens = visibleNodes.reduce((total, node) => total + node.tokens, 0)
  if (totalTokens <= 0 || width <= 0 || height <= 0) return []

  const availableArea = width * height
  const weighted = visibleNodes
    .map((node) => ({ node, value: (node.tokens / totalTokens) * availableArea }))
    .sort((a, b) => b.value - a.value)
  const output: TreemapRect[] = []
  let remaining = { x, y, width, height }
  let row: WeightedNode[] = []
  while (weighted.length) {
    const next = weighted[0]
    const side = Math.min(remaining.width, remaining.height)
    if (!row.length || worst(row, side) >= worst([...row, next], side)) {
      row.push(weighted.shift()!)
      continue
    }

    const placed = placeRow(row, remaining.x, remaining.y, remaining.width, remaining.height)
    for (const item of placed.rects) {
      const inset = depth === 0 ? 1 : 3
      const rect: TreemapRect = {
        x: item.x + inset,
        y: item.y + inset,
        width: Math.max(0, item.width - inset * 2),
        height: Math.max(0, item.height - inset * 2),
        depth,
        node: item.item.node
      }
      output.push(rect)
      if (item.item.node.children.length && rect.width > 8 && rect.height > 8) {
        output.push(...layoutNodes(item.item.node.children, rect.x + 2, rect.y + 18, Math.max(0, rect.width - 4), Math.max(0, rect.height - 20), depth + 1))
      }
    }
    remaining = { x: placed.x, y: placed.y, width: placed.width, height: placed.height }
    row = []
  }

  if (row.length) {
    const placed = placeRow(row, remaining.x, remaining.y, remaining.width, remaining.height)
    for (const item of placed.rects) {
      const inset = depth === 0 ? 1 : 3
      const rect: TreemapRect = {
        x: item.x + inset,
        y: item.y + inset,
        width: Math.max(0, item.width - inset * 2),
        height: Math.max(0, item.height - inset * 2),
        depth,
        node: item.item.node
      }
      output.push(rect)
      if (item.item.node.children.length && rect.width > 8 && rect.height > 8) {
        output.push(...layoutNodes(item.item.node.children, rect.x + 2, rect.y + 18, Math.max(0, rect.width - 4), Math.max(0, rect.height - 20), depth + 1))
      }
    }
  }
  return output
}

export function layoutTreemap(nodes: ComparisonTreeNode[], width: number, height: number): TreemapRect[] {
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) return []
  return layoutNodes(nodes, 0, 0, width, height, 0)
}
