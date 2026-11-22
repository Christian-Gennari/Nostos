import {
  LABEL_PRIORITY,
  MAP_KIND_TOKEN_COUNT,
  MAX_MAP_TOPICS,
  UNTYPED_KIND_KEY,
  alignmentRotation,
  buildKindLegendEntries,
  centerStrengths,
  detectCommunities,
  drawThemeNodeHover,
  filterScopedGraphByKind,
  filterTopicsByKind,
  kindIndexFromColorKey,
  logicalCanvasWidth,
  parseKindsParam,
  pickHubs,
  placeLabels,
  readTheme,
  resolveKindColor,
  scopeGraph,
  selectLabelCandidates,
  serializeKindsParam,
  shelfPositions,
  toggleKindFilter,
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

describe('topic kind map helpers', () => {
  afterEach(() => {
    for (let i = 1; i <= MAP_KIND_TOKEN_COUNT; i += 1) {
      document.documentElement.style.removeProperty(`--map-kind-${i}`);
    }
    document.documentElement.style.removeProperty('--map-kind-none');
  });

  it('reads all 12 --map-kind-* tokens and --map-kind-none from theme styles', () => {
    for (let i = 1; i <= MAP_KIND_TOKEN_COUNT; i += 1) {
      document.documentElement.style.setProperty(`--map-kind-${i}`, `#0000${i.toString(16).padStart(2, '0')}`);
    }
    document.documentElement.style.setProperty('--map-kind-none', '#807972');
    const theme = readTheme();
    expect(theme.kinds).toHaveLength(MAP_KIND_TOKEN_COUNT);
    expect(theme.kinds[0]).toBe('#000001');
    expect(theme.kinds[11]).toBe('#00000c');
    expect(theme.kindNone).toBe('#807972');
  });

  it('resolves kind colour tokens from the colorKey slots the API stores', () => {
    const theme = { kinds: Array.from({ length: 12 }, (_, i) => `token-${i + 1}`), kindNone: 'token-none' };
    expect(kindIndexFromColorKey('kind-1')).toBe(0);
    expect(kindIndexFromColorKey('kind-12')).toBe(11);
    expect(kindIndexFromColorKey('map-kind-5')).toBe(4);
    expect(kindIndexFromColorKey('kind-13')).toBeNull();
    expect(kindIndexFromColorKey(null)).toBeNull();
    expect(resolveKindColor('kind-3', theme)).toBe('token-3');
    expect(resolveKindColor(null, theme)).toBe('token-none');
  });

  it('parses, serializes, and toggles ?kinds= URL filter parameters', () => {
    expect(parseKindsParam(null)).toBeNull();
    expect(parseKindsParam('')?.size).toBe(0);
    expect([...(parseKindsParam('k1, none ,k1') ?? [])]).toEqual(['k1', UNTYPED_KIND_KEY]);
    expect(serializeKindsParam(new Set(['none', 'k1']), ['k1', 'none'])).toBeNull();
    expect(serializeKindsParam(new Set(['k1']), ['k1', 'none'])).toBe('k1');
    expect(serializeKindsParam(null, ['k1'])).toBeNull();

    // Everything visible (no param) -> hiding k1 keeps k2 + none.
    expect(toggleKindFilter(null, 'k1', ['k1', 'k2', 'none'])).toBe('k2,none');
    // Hiding the last remaining key writes '' (all hidden).
    expect(toggleKindFilter(new Set(['k1']), 'k1', ['k1', 'k2'])).toBe('');
    // Re-enabling all keys clears the parameter back to null.
    expect(toggleKindFilter(new Set(['k1']), 'k2', ['k1', 'k2'])).toBeNull();
    // From all hidden, clicking k1 shows only k1.
    expect(toggleKindFilter(new Set(), 'k1', ['k1', 'k2'])).toBe('k1');
  });

  it('filters topics and graphs by visible kind keys, untyped included', () => {
    const topics = [
      { id: 't1', name: 'One', usageCount: 10, kindId: 'k1' },
      { id: 't2', name: 'Two', usageCount: 8, kindId: 'k2' },
      { id: 't3', name: 'Three', usageCount: 5, kindId: null },
    ];
    expect(filterTopicsByKind(topics, null)).toHaveLength(3);
    expect(filterTopicsByKind(topics, new Set(['k2', 'none'])).map((t) => t.id)).toEqual(['t2', 't3']);
    expect(filterTopicsByKind(topics, new Set())).toEqual([]);

    const graph = {
      nodes: topics,
      edges: [
        { sourceId: 't1', targetId: 't2', sharedNotes: 2 },
        { sourceId: 't2', targetId: 't3', sharedNotes: 1 },
      ],
    };
    const scoped = scopeGraph(graph, 't2', 1, 1, Number.MAX_SAFE_INTEGER);
    const filtered = filterScopedGraphByKind(scoped, new Set(['k2', 'none']), 't2');
    expect(filtered.nodes.map((n) => n.id).sort()).toEqual(['t2', 't3']);
  });

  it('scopes first, then filters, then caps, so hidden kinds free cap room', () => {
    const nodes = [
      { id: 'root', name: 'Root', usageCount: 100, kindId: 'k1' },
      ...Array.from({ length: MAX_MAP_TOPICS + 5 }, (_, i) => ({
        id: `busy-${i}`,
        name: `Busy ${i}`,
        usageCount: 1000 + i,
        kindId: 'k1',
      })),
      { id: 'quiet-1', name: 'Quiet 1', usageCount: 2, kindId: 'k2' },
      { id: 'quiet-2', name: 'Quiet 2', usageCount: 1, kindId: 'k2' },
    ];
    const edges = [
      ...Array.from({ length: MAX_MAP_TOPICS + 5 }, (_, i) => ({
        sourceId: 'root',
        targetId: `busy-${i}`,
        sharedNotes: 3,
      })),
      { sourceId: 'quiet-1', targetId: 'quiet-2', sharedNotes: 2 },
      { sourceId: 'root', targetId: 'quiet-1', sharedNotes: 2 },
    ];
    // Depth 2 so quiet-2 (two steps away through quiet-1) is in scope too.
    const full = scopeGraph({ nodes, edges }, 'root', 2, 1, Number.MAX_SAFE_INTEGER);
    expect(full.totalInScope).toBe(nodes.length);

    const filtered = filterScopedGraphByKind(full, new Set(['k2']), 'root');
    expect(filtered.nodes.map((n) => n.id).sort()).toEqual(['quiet-1', 'quiet-2', 'root']);
    // Capped after filtering: the two quiet topics survive although every
    // busy topic outranks them by usageCount.
    expect(filtered.totalInScope).toBe(3);
    expect(filtered.hiddenByKind).toBe(MAX_MAP_TOPICS + 5);
    expect(filtered.edges.every((e) => e.sourceId !== 'busy-0')).toBe(true);

    // Without a filter the helper still applies the cap and hides nothing.
    const unfiltered = filterScopedGraphByKind(full, null, 'root');
    expect(unfiltered.nodes).toHaveLength(MAX_MAP_TOPICS);
    expect(unfiltered.hiddenByKind).toBe(0);
  });

  it('keeps the root even when its own kind is hidden', () => {
    const nodes = [
      { id: 'root', name: 'Root', usageCount: 10, kindId: 'k1' },
      { id: 'a', name: 'A', usageCount: 5, kindId: 'k2' },
    ];
    const full = scopeGraph({ nodes, edges: [{ sourceId: 'root', targetId: 'a', sharedNotes: 2 }] }, 'root', 1, 1);
    const filtered = filterScopedGraphByKind(full, new Set(['k2']), 'root');
    expect(filtered.nodes.map((n) => n.id)).toEqual(['root', 'a']);
    expect(filtered.hiddenByKind).toBe(0);
  });

  it('builds sorted kind legend entries with counts and No kind (n) at the end', () => {
    const kinds = [
      { id: 'k1', name: 'Books', colorKey: 'kind-1', sortOrder: 1, createdAt: '', topicCount: 1 },
      { id: 'k2', name: 'Concepts', colorKey: 'kind-2', sortOrder: 2, createdAt: '', topicCount: 2 },
      { id: 'k3', name: 'Unused', colorKey: 'kind-3', sortOrder: 3, createdAt: '', topicCount: 0 },
    ];
    const items = [{ kindId: 'k2' }, { kindId: 'k2' }, { kindId: 'k1' }, { kindId: null }];
    const entries = buildKindLegendEntries(items, kinds, new Set(['k2', 'none']));
    expect(entries).toEqual([
      {
        key: 'k1',
        label: 'Books',
        color: 'var(--map-kind-1)',
        count: 1,
        visible: false,
        isUntyped: false,
      },
      {
        key: 'k2',
        label: 'Concepts',
        color: 'var(--map-kind-2)',
        count: 2,
        visible: true,
        isUntyped: false,
      },
      {
        key: 'none',
        label: 'No kind',
        color: 'var(--map-kind-none)',
        count: 1,
        visible: true,
        isUntyped: true,
      },
    ]);

    // A topic whose kind row is missing counts as untyped, so it cannot vanish.
    const unknown = buildKindLegendEntries([{ kindId: 'gone' }], kinds, null);
    expect(unknown.map((entry) => [entry.key, entry.count])).toEqual([['none', 1]]);
  });

  it('counts drawn topics for a visible kind and in-scope topics for a hidden one', () => {
    const kinds = [
      { id: 'k1', name: 'Books', colorKey: 'kind-1', sortOrder: 1, createdAt: '', topicCount: 3 },
      { id: 'k2', name: 'Concepts', colorKey: 'kind-2', sortOrder: 2, createdAt: '', topicCount: 2 },
    ];
    const drawn = [{ kindId: 'k1' }, { kindId: 'k1' }];
    const scope = [...drawn, { kindId: 'k1' }, { kindId: 'k2' }, { kindId: 'k2' }];

    // Visible: Books reports the 2 actually drawn, not the 3 in scope.
    const allVisible = buildKindLegendEntries(scope, kinds, null, { drawnItems: drawn });
    expect(allVisible.map((entry) => [entry.key, entry.count])).toEqual([
      ['k1', 2],
      ['k2', 0],
    ]);

    // Hidden: Concepts reports the 2 that come back when it is shown again.
    const booksOnly = buildKindLegendEntries(scope, kinds, new Set(['k1']), {
      drawnItems: drawn,
    });
    expect(booksOnly.map((entry) => [entry.key, entry.count, entry.visible])).toEqual([
      ['k1', 2, true],
      ['k2', 2, false],
    ]);
  });

  it('does not count an unknown kind id as untyped before the kind list has loaded', () => {
    const kinds = [
      { id: 'k1', name: 'Books', colorKey: 'kind-1', sortOrder: 1, createdAt: '', topicCount: 1 },
    ];
    const items = [{ kindId: 'k1' }, { kindId: null }];

    // Unavailable: k1 is typed but unnamed; only the truly untyped row appears.
    const unavailable = buildKindLegendEntries(items, [], null, { kindsLoaded: false });
    expect(unavailable.map((entry) => [entry.key, entry.count])).toEqual([['none', 1]]);

    // Loaded, but the id is gone from the list: it is untyped, not invisible.
    const loadedWithoutK1 = buildKindLegendEntries(items, [], null, { kindsLoaded: true });
    expect(loadedWithoutK1.map((entry) => [entry.key, entry.count])).toEqual([['none', 2]]);

    const loaded = buildKindLegendEntries(items, kinds, null, { kindsLoaded: true });
    expect(loaded.map((entry) => [entry.key, entry.count])).toEqual([
      ['k1', 1],
      ['none', 1],
    ]);
  });
});
