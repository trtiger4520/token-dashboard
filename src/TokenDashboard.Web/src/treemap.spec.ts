import { describe, expect, it } from 'vitest'
import type { ComparisonTreeNode } from './types'
import { layoutTreemap } from './treemap'

function node(name: string, tokens: number, children: ComparisonTreeNode[] = []): ComparisonTreeNode {
  return {
    kind: children.length ? 'model' : 'tool',
    name,
    tokens,
    eventCount: 1,
    uniqueSessionCount: 1,
    turnCount: 1,
    costUsd: 0,
    partialCostUsd: 0,
    cacheHitRate: null,
    children
  }
}

describe('layoutTreemap', () => {
  it('normalizes token weights into the available rectangle', () => {
    const rectangles = layoutTreemap([
      node('large', 17_000_000, [node('read', 11_000_000), node('none', 6_000_000)]),
      node('small', 4_000_000, [node('exec', 4_000_000)])
    ], 920, 360)

    expect(rectangles.length).toBe(5)
    for (const rectangle of rectangles) {
      expect(rectangle.x).toBeGreaterThanOrEqual(0)
      expect(rectangle.y).toBeGreaterThanOrEqual(0)
      expect(rectangle.x + rectangle.width).toBeLessThanOrEqual(920)
      expect(rectangle.y + rectangle.height).toBeLessThanOrEqual(360)
    }
  })

  it('reflows to the measured aspect ratio', () => {
    const data = [node('model', 100, [node('tool', 100)])]
    const wide = layoutTreemap(data, 800, 240)[0]
    const tall = layoutTreemap(data, 240, 800)[0]

    expect(wide?.width).toBeGreaterThan(wide?.height ?? 0)
    expect(tall?.height).toBeGreaterThan(tall?.width ?? 0)
  })
})
