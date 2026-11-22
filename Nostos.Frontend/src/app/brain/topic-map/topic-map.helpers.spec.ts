import {
  LABEL_PRIORITY,
  MAX_MAP_TOPICS,
  alignmentRotation,
  centerStrengths,
  detectCommunities,
  drawThemeNodeHover,
  logicalCanvasWidth,
  pickHubs,
  placeLabels,
  scopeGraph,
  selectLabelCandidates,
  shelfPositions,
  type LabelCandidate,
  type LabelSource,
  type PlacedLabel,
} from './topic-map.helpers';

/** 7px per character: a stand-in for `measureText` at the 12px label size. */
const measure = (label: string): number => label.length * 7;

function candidate(partial: Partial<LabelCandidate> & { key: string; x: number; y: number }): LabelCandidate {
  return {
    size: 4,
    label: partial.key,
    color: '#000',
    weight: '400',
    priority: LABEL_PRIORITY.normal,
    ...partial,
  };
}

function box(label: PlacedLabel): { left: number; right: number; top: number; bottom: number } {
  const width = measure(label.label);
  const left = label.align === 'left' ? label.textX : label.textX - width;
  return { left, right: left + width, top: label.y - 7, bottom: label.y + 7 };
}

function overlaps(a: PlacedLabel, b: PlacedLabel): boolean {
  const p = box(a);
  const q = box(b);
  return p.left < q.right && q.left < p.right && p.top < q.bottom && q.top < p.bottom;
}

describe('topic map label layout', () => {
  it('never places two labels on top of each other', () => {
    // A tight cluster, like Seneca/Death or Narrative/Practice/Revision in the
    // captured graph, where Sigma's own drawer overprinted the names.
    const candidates = [
      candidate({ key: 'Seneca', x: 100, y: 100 }),
      candidate({ key: 'Death', x: 130, y: 102 }),
      candidate({ key: 'Narrative', x: 110, y: 106 }),
      candidate({ key: 'Practice', x: 140, y: 98 }),
      candidate({ key: 'Revision', x: 160, y: 104 }),
    ];
    const placed = placeLabels(candidates, measure, 800, 12);

    for (let i = 0; i < placed.length; i += 1) {
      for (let j = i + 1; j < placed.length; j += 1) {
        expect(overlaps(placed[i], placed[j]), `${placed[i].label} / ${placed[j].label}`).toBe(false);
      }
    }
    expect(placed.length).toBeGreaterThan(0);
    expect(placed.length).toBeLessThan(candidates.length);
  });

  it('lets the active neighbourhood win a collision over ordinary labels', () => {
    const placed = placeLabels(
      [
        candidate({ key: 'Ordinary', x: 100, y: 100, size: 12 }),
        candidate({ key: 'Active', x: 102, y: 101, priority: LABEL_PRIORITY.active }),
      ],
      measure,
      800,
      12
    );
    expect(placed.map((p) => p.label)).toContain('Active');
  });

  it('flips a label to the left of its node at the right edge of the stage', () => {
    const [placed] = placeLabels([candidate({ key: 'Epictetus', x: 360, y: 50 })], measure, 390, 12);
    expect(placed.align).toBe('right');
    expect(placed.textX).toBeLessThan(360);
  });

  it('reserves room for a label it does not draw', () => {
    // The hovered/selected label is painted by Sigma on its plate; nothing else
    // may sit on that box, and it must not be painted twice.
    const placed = placeLabels(
      [
        candidate({ key: 'Selected', x: 100, y: 100, priority: LABEL_PRIORITY.active, reserveOnly: true }),
        candidate({ key: 'Neighbour', x: 104, y: 101, priority: LABEL_PRIORITY.normal }),
      ],
      measure,
      800,
      12
    );
    expect(placed.map((p) => p.label)).not.toContain('Selected');
    // The plate box is to the right of x=100, so the neighbour takes its left.
    const neighbour = placed.find((p) => p.label === 'Neighbour')!;
    expect(neighbour.textX).toBeLessThan(100);
  });

  it('steers ordinary labels off other nodes', () => {
    const [placed] = placeLabels(
      [candidate({ key: 'Label', x: 100, y: 100 })],
      measure,
      800,
      12,
      [{ key: 'other', x: 130, y: 100, size: 6 }]
    );
    // The right side is blocked by a node, so the label goes to the left.
    expect(placed.textX).toBeLessThan(100);
  });
});

describe('logicalCanvasWidth', () => {
  it('measures in CSS px, not the device-pixel backing store', () => {
    // Sigma sizes canvases at width × devicePixelRatio. Comparing a CSS-px x
    // against that is why right-edge labels were clipped on 2x phones.
    const canvas = document.createElement('canvas');
    canvas.width = 780;
    Object.defineProperty(canvas, 'clientWidth', { value: 390 });
    expect(logicalCanvasWidth(canvas)).toBe(390);
  });
});

describe('pickHubs', () => {
  it('takes the most-referenced fraction, with a floor of one', () => {
    const nodes = Array.from({ length: 20 }, (_, i) => ({ id: `n${i}`, usageCount: i }));
    expect([...pickHubs(nodes)]).toEqual(['n19', 'n18', 'n17']);
    expect([...pickHubs([{ id: 'only', usageCount: 1 }])]).toEqual(['only']);
  });
});

describe('detectCommunities', () => {
  const ids = ['a1', 'a2', 'a3', 'b1', 'b2', 'b3', 'lonely'];
  const edges = [
    { sourceId: 'a1', targetId: 'a2', sharedNotes: 3 },
    { sourceId: 'a2', targetId: 'a3', sharedNotes: 3 },
    { sourceId: 'a1', targetId: 'a3', sharedNotes: 3 },
    { sourceId: 'b1', targetId: 'b2', sharedNotes: 2 },
    { sourceId: 'b2', targetId: 'b3', sharedNotes: 2 },
    { sourceId: 'b1', targetId: 'b3', sharedNotes: 2 },
    // A weak bridge must not merge the two clusters.
    { sourceId: 'a3', targetId: 'b1', sharedNotes: 1 },
  ];

  it('separates densely connected groups across a weak bridge', () => {
    const rank = detectCommunities(ids, edges);
    expect(rank.get('a1')).toBe(rank.get('a2'));
    expect(rank.get('a2')).toBe(rank.get('a3'));
    expect(rank.get('b1')).toBe(rank.get('b2'));
    expect(rank.get('b2')).toBe(rank.get('b3'));
    expect(rank.get('a1')).not.toBe(rank.get('b1'));
    expect(rank.get('lonely')).not.toBe(rank.get('a1'));
  });

  it('is deterministic, so a reload paints the same hues', () => {
    const first = Object.fromEntries(detectCommunities(ids, edges));
    const again = Object.fromEntries(detectCommunities([...ids].reverse(), edges));
    expect(again).toEqual(first);
  });
});

describe('alignmentRotation', () => {
  const horizontal = Array.from({ length: 10 }, (_, i) => ({ x: i * 10, y: (i % 2) * 2 }));

  it('turns a sideways graph upright on a portrait stage', () => {
    const turn = alignmentRotation(horizontal, 390, 720);
    expect(Math.abs(Math.abs(turn) - Math.PI / 2)).toBeLessThan(0.1);
  });

  it('leaves a graph alone when it already follows the stage', () => {
    expect(Math.abs(alignmentRotation(horizontal, 1340, 680))).toBeLessThan(0.1);
  });

  it('does not turn a round cloud', () => {
    const ring = Array.from({ length: 12 }, (_, i) => ({
      x: Math.cos((i / 12) * 2 * Math.PI),
      y: Math.sin((i / 12) * 2 * Math.PI),
    }));
    expect(alignmentRotation(ring, 390, 720)).toBe(0);
  });
});

describe('centerStrengths', () => {
  it('pulls less along the long axis of the stage', () => {
    const wide = centerStrengths(1340, 680);
    expect(wide.x).toBeLessThan(wide.y);
    const tall = centerStrengths(390, 720);
    expect(tall.y).toBeLessThan(tall.x);
    expect(centerStrengths(0, 0)).toEqual({ x: 0.1, y: 0.1 });
  });
});

describe('shelfPositions', () => {
  it('lines unconnected topics up under the graph, centred', () => {
    const shelf = shelfPositions(3, { minX: -100, maxX: 100, minY: -50, maxY: 50 }, 100);
    expect(shelf).toHaveLength(3);
    for (const p of shelf) expect(p.y).toBeLessThan(-50);
    expect(shelf.reduce((sum, p) => sum + p.x, 0) / 3).toBeCloseTo(0, 5);
  });

  it('wraps to a new row once the graph width is used', () => {
    const shelf = shelfPositions(5, { minX: 0, maxX: 200, minY: 0, maxY: 100 }, 100);
    const rows = new Set(shelf.map((p) => p.y));
    expect(rows.size).toBe(2);
  });
});

describe('selectLabelCandidates', () => {
  const stage = { width: 400, height: 300, cellSize: 100, density: 2, threshold: 2.4 };
  const node = (partial: Partial<LabelSource> & { key: string }): LabelSource => ({
    x: 50,
    y: 50,
    size: 4,
    label: partial.key,
    forced: false,
    ...partial,
  });
  const keys = (nodes: LabelSource[]) => nodes.map((n) => n.key).sort();

  it('keeps the largest nodes of each grid cell, up to the density', () => {
    const chosen = selectLabelCandidates(
      [
        node({ key: 'small', size: 3 }),
        node({ key: 'big', size: 9 }),
        node({ key: 'mid', size: 6 }),
        node({ key: 'elsewhere', x: 250, size: 3 }),
      ],
      stage
    );
    expect(keys(chosen)).toEqual(['big', 'elsewhere', 'mid']);
  });

  it('always offers forced labels, whatever their size or cell', () => {
    const chosen = selectLabelCandidates(
      [
        node({ key: 'a', size: 9 }),
        node({ key: 'b', size: 8 }),
        node({ key: 'hub', size: 1, forced: true }),
      ],
      stage
    );
    expect(keys(chosen)).toEqual(['a', 'b', 'hub']);
  });

  it('drops nodes drawn under the threshold, without a label, or far off stage', () => {
    const chosen = selectLabelCandidates(
      [
        node({ key: 'tiny', size: 2 }),
        node({ key: 'blank', label: null }),
        node({ key: 'gone', x: 900 }),
        node({ key: 'edge', x: 420 }),
      ],
      stage
    );
    expect(keys(chosen)).toEqual(['edge']);
  });
});

describe('drawThemeNodeHover', () => {
  /** A 2D context stand-in that records each fill and the style it used. */
  function recordingContext(width: number) {
    const fills: string[] = [];
    const texts: Array<{ text: string; style: string }> = [];
    const context = {
      canvas: { clientWidth: width, width },
      font: '',
      fillStyle: '',
      textAlign: 'left',
      shadowOffsetX: 0,
      shadowOffsetY: 0,
      shadowBlur: 0,
      shadowColor: '',
      measureText: (text: string) => ({ width: text.length * 7 }),
      beginPath: () => {},
      moveTo: () => {},
      lineTo: () => {},
      arc: () => {},
      closePath: () => {},
      fill() {
        fills.push(String(this.fillStyle));
      },
      fillText(text: string) {
        texts.push({ text, style: String(this.fillStyle) });
      },
    };
    return { context: context as unknown as CanvasRenderingContext2D, fills, texts };
  }

  it('paints the plate in the theme fill, the node over it, then the label', () => {
    const { context, fills, texts } = recordingContext(800);
    drawThemeNodeHover(
      context,
      {
        x: 100,
        y: 100,
        size: 6,
        label: 'Seneca',
        nodeColor: '#4a4d57',
        labelColor: '#e8e9ec',
        labelWeight: '600',
      },
      12,
      'sans-serif',
      '#1f2127'
    );
    // Never Sigma 3's hardcoded white box.
    expect(fills).toEqual(['#1f2127', '#4a4d57']);
    expect(texts).toEqual([{ text: 'Seneca', style: '#e8e9ec' }]);
    expect(context.font).toContain('600');
  });
});

describe('scopeGraph', () => {
  const sample = {
    nodes: [
      { id: 'root', name: 'Education', usageCount: 2 },
      { id: 'h1a', name: 'Pedagogy', usageCount: 10 },
      { id: 'h1b', name: 'Rhetoric', usageCount: 3 },
      { id: 'h2a', name: 'Logic', usageCount: 8 },
      { id: 'h2b', name: 'Grammar', usageCount: 4 },
      { id: 'h3a', name: 'Poetry', usageCount: 20 },
      { id: 'far', name: 'Astronomy', usageCount: 50 },
      { id: 'bridge-only', name: 'Optics', usageCount: 15 },
    ],
    edges: [
      { sourceId: 'root', targetId: 'h1a', sharedNotes: 3 },
      { sourceId: 'root', targetId: 'h1b', sharedNotes: 1 },
      { sourceId: 'h1a', targetId: 'h2a', sharedNotes: 2 },
      { sourceId: 'h1a', targetId: 'h2b', sharedNotes: 1 },
      { sourceId: 'h2a', targetId: 'h3a', sharedNotes: 2 },
      { sourceId: 'h3a', targetId: 'far', sharedNotes: 4 },
      // Reachable from root only via the weak edge root -> h1b (1) or h1b -> bridge-only (5)
      { sourceId: 'h1b', targetId: 'bridge-only', sharedNotes: 5 },
      // Cross-edge at hop 2
      { sourceId: 'h2a', targetId: 'h2b', sharedNotes: 2 },
    ],
  };

  it('respects depth limits 1, 2, and 3', () => {
    const d1 = scopeGraph(sample, 'root', 1, 1);
    expect(d1.nodes.map((n) => n.id)).toEqual(['root', 'h1a', 'h1b']);
    expect(d1.hops.get('root')).toBe(0);
    expect(d1.hops.get('h1a')).toBe(1);

    const d2 = scopeGraph(sample, 'root', 2, 1);
    expect(d2.nodes.map((n) => n.id)).toEqual(['root', 'h1a', 'h1b', 'bridge-only', 'h2a', 'h2b']);
    expect(d2.hops.get('h2a')).toBe(2);

    const d3 = scopeGraph(sample, 'root', 3, 1);
    expect(d3.nodes.map((n) => n.id)).toContain('h3a');
    expect(d3.nodes.map((n) => n.id)).not.toContain('far');
  });

  it('drops edges below minShared and omits topics reachable only through dropped edges', () => {
    const depth2 = scopeGraph(sample, 'root', 2, 2);
    expect(depth2.nodes.map((n) => n.id)).toEqual(['root', 'h1a', 'h2a']);

    const depth3 = scopeGraph(sample, 'root', 3, 2);
    const ids = depth3.nodes.map((n) => n.id);
    // h2b is unreachable through h1a->h2b (weight 1), so it shifts to hop 3 via h2a->h2b (weight 2)
    expect(ids).toEqual(['root', 'h1a', 'h2a', 'h3a', 'h2b']);
    expect(depth3.hops.get('h2b')).toBe(3);
    // h1b (via sharedNotes: 1) and bridge-only (behind h1b even though edge=5) must not appear
    expect(ids).not.toContain('h1b');
    expect(ids).not.toContain('bridge-only');
    expect(depth3.edges.every((e) => e.sharedNotes >= 2)).toBe(true);
  });

  it('caps at limit keeping closest hops first, then highest usage, and reports totalInScope', () => {
    const capped = scopeGraph(sample, 'root', 2, 1, 4);
    expect(capped.totalInScope).toBe(6);
    expect(capped.nodes).toHaveLength(4);
    // Hop 0: root (usage 2); Hop 1: h1a (10), h1b (3); Hop 2 best usage: bridge-only (15) beats h2a (8) and h2b (4)
    expect(capped.nodes.map((n) => n.id)).toEqual(['root', 'h1a', 'h1b', 'bridge-only']);
    expect(capped.nodes[0].id).toBe('root');
  });

  it('always keeps the root even with MAX_MAP_TOPICS limit and low root usage', () => {
    const dense = {
      nodes: [
        { id: 'root', name: 'Tiny Root', usageCount: 1 },
        ...Array.from({ length: MAX_MAP_TOPICS + 25 }, (_, i) => ({
          id: `n-${i}`,
          name: `Topic ${i}`,
          usageCount: 100 + i,
        })),
      ],
      edges: Array.from({ length: MAX_MAP_TOPICS + 25 }, (_, i) => ({
        sourceId: 'root',
        targetId: `n-${i}`,
        sharedNotes: 2,
      })),
    };
    const scoped = scopeGraph(dense, 'root', 1, 1);
    expect(scoped.totalInScope).toBe(MAX_MAP_TOPICS + 26);
    expect(scoped.nodes).toHaveLength(MAX_MAP_TOPICS);
    expect(scoped.nodes[0].id).toBe('root');
  });

  it('keeps an isolated root with zero links', () => {
    const lonely = {
      nodes: [
        { id: 'root', name: 'Alone', usageCount: 1 },
        { id: 'other', name: 'Other', usageCount: 5 },
      ],
      edges: [],
    };
    const scoped = scopeGraph(lonely, 'root', 3, 1);
    expect(scoped.nodes.map((n) => n.id)).toEqual(['root']);
    expect(scoped.edges).toEqual([]);
    expect(scoped.totalInScope).toBe(1);
    expect(scoped.hops.get('root')).toBe(0);
  });

  it('returns an empty scope when rootId is missing from the graph', () => {
    const scoped = scopeGraph(sample, 'does-not-exist', 2, 1);
    expect(scoped.nodes).toEqual([]);
    expect(scoped.edges).toEqual([]);
    expect(scoped.totalInScope).toBe(0);
    expect(scoped.hops.size).toBe(0);
  });
});
