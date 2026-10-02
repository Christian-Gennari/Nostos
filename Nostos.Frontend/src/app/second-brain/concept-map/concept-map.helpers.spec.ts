import {
  LABEL_PRIORITY,
  alignmentRotation,
  centerStrengths,
  detectCommunities,
  logicalCanvasWidth,
  pickHubs,
  placeLabels,
  shelfPositions,
  type LabelCandidate,
  type PlacedLabel,
} from './concept-map.helpers';

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

describe('concept map label layout', () => {
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
  it('lines unconnected concepts up under the graph, centred', () => {
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
