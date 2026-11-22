import type { SimulationNodeDatum } from 'd3-force';
import type {
  TopicDto,
  TopicGraphDto,
  TopicGraphEdgeDto,
  TopicGraphNodeDto,
  TopicKindDto,
} from '../../core/services/topics.service';

export const MAX_MAP_TOPICS = 150;
export const LOCAL_MAP_DEPTH_MIN = 1;
export const LOCAL_MAP_DEPTH_MAX = 3;

/** Where the map's camera stands, as Sigma reports it. */
export interface MapCamera {
  x: number;
  y: number;
  ratio: number;
  angle: number;
}

/** The topics the map draws: the most referenced, up to the cap. */
export function drawnTopics<T extends TopicDto>(topics: readonly T[]): T[] {
  return [...topics].sort(compareTopics).slice(0, MAX_MAP_TOPICS);
}

/** Result of scoping the topic graph around one root topic. */
export interface ScopedGraph {
  nodes: TopicGraphNodeDto[];
  edges: TopicGraphEdgeDto[];
  /** Shortest hop distance from `rootId` (0 for root, 1..depth for neighbours). */
  hops: Map<string, number>;
  /** Reachable topics within `depth` using edges `>= minShared`, before capping. */
  totalInScope: number;
}

/**
 * Scope a topic graph to `rootId` within `depth` steps along edges with at
 * least `minShared` shared notes. Drops edges below `minShared` before BFS so
 * topics reachable only via dropped edges are omitted, and caps at `limit`
 * keeping closest hops first, then highest usage.
 */
export function scopeGraph(
  graph: TopicGraphDto,
  rootId: string,
  depth: number,
  minShared: number,
  limit: number = MAX_MAP_TOPICS
): ScopedGraph {
  const nodeById = new Map(graph.nodes.map((node) => [node.id, node]));
  if (!rootId || !nodeById.has(rootId)) {
    return { nodes: [], edges: [], hops: new Map(), totalInScope: 0 };
  }

  const maxDepth = Math.max(0, Math.min(LOCAL_MAP_DEPTH_MAX, Math.floor(depth)));
  const minStrength = Math.max(1, Math.floor(minShared));
  const effectiveLimit = Math.max(1, Math.floor(limit));

  const validEdges = graph.edges.filter(
    (edge) =>
      edge.sharedNotes >= minStrength &&
      edge.sourceId !== edge.targetId &&
      nodeById.has(edge.sourceId) &&
      nodeById.has(edge.targetId)
  );

  const adjacency = new Map<string, string[]>();
  for (const edge of validEdges) {
    let from = adjacency.get(edge.sourceId);
    if (!from) {
      from = [];
      adjacency.set(edge.sourceId, from);
    }
    from.push(edge.targetId);

    let to = adjacency.get(edge.targetId);
    if (!to) {
      to = [];
      adjacency.set(edge.targetId, to);
    }
    to.push(edge.sourceId);
  }

  const allHops = new Map<string, number>([[rootId, 0]]);
  const queue: string[] = [rootId];
  let head = 0;
  while (head < queue.length) {
    const current = queue[head++];
    const hop = allHops.get(current)!;
    if (hop >= maxDepth) continue;
    for (const neighbour of adjacency.get(current) ?? []) {
      if (allHops.has(neighbour)) continue;
      allHops.set(neighbour, hop + 1);
      queue.push(neighbour);
    }
  }

  const totalInScope = allHops.size;
  const reachable = [...allHops.keys()].map((id) => nodeById.get(id)!);
  reachable.sort(
    (a, b) => (allHops.get(a.id)! - allHops.get(b.id)!) || compareTopics(a, b)
  );

  const nodes = reachable.slice(0, effectiveLimit);
  const keptIds = new Set(nodes.map((node) => node.id));
  const hops = new Map<string, number>();
  for (const node of nodes) {
    hops.set(node.id, allHops.get(node.id)!);
  }
  const edges = validEdges.filter(
    (edge) => keptIds.has(edge.sourceId) && keptIds.has(edge.targetId)
  );

  return { nodes, edges, hops, totalInScope };
}

/* ── Visual constants ── */
export const NODE_SIZE_MIN = 4;
export const NODE_SIZE_MAX = 16;
export const EDGE_SIZE_MIN = 0.75;
export const EDGE_SIZE_MAX = 3;

/**
 * Label font size, in px, for EVERY label.
 *
 * There is deliberately no per-node range here any more. The component used to
 * compute one (10-16) and store it as a node attribute, but Sigma 3 rendered
 * every label at `settings.labelSize` and ignored the per-node value, so it was
 * dead config, and `fitOccupancy()` was reserving gutter against a number no
 * label was ever drawn at. One named constant now feeds both the label drawer
 * and the gutter calculation, so they cannot disagree.
 */
export const LABEL_DRAW_SIZE = 12;

/**
 * Edge alpha floor. Measured on the real graph field: at the previous floor
 * (0.44-0.54) an isolated edge's strongest pixel differed from the field by
 * only 8-17 of 765 — a contrast ratio of 1.05:1, i.e. invisible. Sweeping alpha
 * against the same field and camera gave max deltas of 115 at 0.6, 266 at 0.8
 * and 365 at 1.0, so the floor is the lever, not the stroke width.
 */
export const EDGE_ALPHA_MIN = 0.62;
export const EDGE_ALPHA_RANGE = 0.38;

/**
 * Dimming applied to nodes that are NOT connected to the active node.
 *
 * Measured: at 0.55 a dimmed node retained only 21% of its contrast in the light
 * theme (1.66:1 against the field) — the "everything else melts into the
 * background" report.
 *
 * At 0.70, measured by compositing the reducer's own output over the field
 * (scripts/map-audit/dim-reducer.mjs), both themes clear the 3:1 non-text
 * minimum while the active neighbourhood still dominates:
 *
 *   light  composite rgb(137,133,127) on rgb(253,248,246) -> 3.48:1 (44% retained)
 *   dark   composite rgb(130,126,120) on rgb(21,24,31)    -> 4.40:1 (50% retained)
 *
 * Prefer that probe over screenshot sampling: a node is a few pixels wide, so a
 * "centre pixel" lands on an anti-aliased edge and understates the ink, and a
 * patch-max probe instead catches whatever dark edge crosses the box.
 */
export const NODE_ALPHA_DIM = 0.7;

/** Edges not touching the active node: quieter than nodes, but still present. */
export const EDGE_ALPHA_DIM = 0.34;

/**
 * Minimum DRAWN radius for a node to get a label. Decollision is left to Sigma's
 * label grid.
 *
 * Sigma tests this against `scaleSize(size)`, the radius it actually draws
 * (`sigma/dist/sigma.esm.js` — `var size = this.scaleSize(data.size); if
 * (!data.forceLabel && size < this.settings.labelRenderedSizeThreshold) continue`),
 * NOT against the stored `size` attribute. So the number that matters is
 * `NODE_SIZE_MIN / sqrt(ratio)` at the fitted zoom, not `NODE_SIZE_MIN`:
 *
 *   fitted ratio ~1.91  ->  smallest drawn radius 4 / sqrt(1.91) = 2.89px
 *
 * The previous value (3.2) was calibrated when radii did not scale with the
 * camera, so the floor was exactly `NODE_SIZE_MIN` = 4.0. Leaving it at 3.2 after
 * switching to `Math.sqrt` silenced 47 of 53 labels, because most topics share
 * the minimum usage count and sit exactly at the 4px floor once scaled.
 *
 * 2.4 clears that 2.89px floor with margin at the fit, so every node is labelled
 * on open, while still letting labels recede as the user zooms out (a larger
 * ratio shrinks drawn radii past the threshold) — which is Obsidian's behaviour,
 * where labels fade with zoom rather than being pinned on forever.
 * `topic-map.component.spec.ts` asserts the relationship so it cannot regress:
 * the threshold must stay below `NODE_SIZE_MIN / sqrt(fitted ratio)`.
 */
export const LABEL_RENDER_MIN_SIZE = 2.4;

/**
 * Edge allowance, as a fraction of stage width, so the outermost nodes' discs are
 * not flush against the canvas.
 *
 * This used to be a label gutter deliberately sized to the widest topic name,
 * because Sigma's label drawer only ever drew to the RIGHT of a node and anything
 * past the canvas edge was silently truncated. That reservation is no longer
 * needed: `drawFlipsAtEdgeNodeLabel` moves a label to the other side of its node
 * when it would overflow, so the text always fits at any stage width.
 *
 * Keeping the reservation was actively harmful. On a 369px phone stage the gutter
 * cap resolved to 55px a side, which made the WIDTH the binding axis of the fit
 * (occupancy.x 0.75 against occupancy.y 0.88) and spent the difference on empty
 * margin, shrinking every node: the graph measured fillX 0.748 with a mean node
 * radius of 2.0px, where a small constant allowance gives the map back its width
 * (fillX 0.84) and draws the nodes larger (mean radius 2.4px). The allowance
 * covers the largest node's disc plus half Sigma's label offset, which is all the
 * framing needs now that the text cannot overflow in the first place.
 */
export const LABEL_EDGE_ALLOWANCE_FRACTION = 0.04;

/** Sigma's default horizontal offset from a node to the start of its label. */
export const LABEL_OFFSET_PX = 10;

/** Fraction of the stage a fitted graph should occupy, with no label inset. */
export const FIT_OCCUPANCY = 0.88;

/* The stage-aspect seed and the per-axis stretch that used to live here are both
 * gone, and their removal is the point of this change rather than a tidy-up.
 *
 * `normalizeGraphPositions` rescaled the settled layout into stage pixels, solving
 * each axis independently and clamping the ratio to 1.4x, so a portrait phone got
 * an isotropically-correct layout sheared into ellipses after the fact. Two things
 * were wrong with it, both measured:
 *
 *  1. It bought nothing. Re-running the fit without any shear on a 369x707 stage
 *     left the graph BETTER framed: fillY 0.426 -> 0.436. The shear only shifted
 *     the extent that `uniform = min(scaleX, scaleY)` is derived from, so it was
 *     filling the long axis by emptying the short one.
 *  2. It corrupted the live physics. Rescaling settled positions by k and then
 *     running the force simulation at constants x k is not the same system: the
 *     collide radius is in sigma units and penetrates, so the graph inflated by
 *     2.03x on a portrait stage (1.40x on desktop) the moment a user dragged
 *     (`scripts/map-audit/live-loop-behaviour.mjs`). Without the rescale the same
 *     loop grows the graph by 1.03x — the order of a normal re-heat settle.
 *
 * Obsidian never rescales either: its worker keeps the physics in one unit system
 * and the renderer's pan/zoom does the framing. Sigma's camera already does that
 * here, so positions stay in graph units and the fit stays uniform.
 */

/** Stable initial coordinates keep captures and sessions reproducible. */
export function hashSeed(value: string): number {
  let hash = 2166136261;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0) / 4294967295;
}

/**
 * Obsidian's `setData` seeding, adapted to a whole-graph build.
 *
 * Obsidian spawns a new node beside the already-placed neighbours it shares edges
 * with, and falls back to a ring for one that has none:
 *
 *   var L = 60 * I * 60;                       // I = number of new nodes
 *   var O = Math.sqrt(L / Math.PI + v * v) - v;
 *   var angle = 2 * Math.PI * Math.random();
 *   f.x = r * Math.cos(angle); f.y = r * Math.sin(angle)
 *   //   r = v + Math.sqrt(Math.random()) * O          (v = collide radius, 60)
 *
 * Two details are load-bearing and easy to get wrong:
 *
 *  * the ring is keyed on the **collide radius** `v`, not on `linkDistance`.
 *    `v` is 60 while `linkDistance` is 250, and the `-v` term cancels the `v +`
 *    out of `O`, so the ring sits at roughly `sqrt(60*N*60/π)` ~ 246 for 53 nodes.
 *    Using `linkDistance` here instead inflates the seed radius by ~270% and the
 *    settled graph then comes out smaller on screen (measured: desktop fillX
 *    0.626 against the shipped 0.701).
 *  * the angle must be drawn per node, not stepped. A golden-angle spiral is a
 *    tempting deterministic substitute but it is strongly structured, and the
 *    layout inherits that structure: measured on the live graph it settled to an
 *    aspect of 1.61 against the ~1.05 this force set produces from Obsidian's
 *    own uniform-random angles. That elongation alone cost the portrait stage its
 *    height fill. `hashSeed` gives the same statistical spread while keeping a
 *    capture and a test run reproducible, which `Math.random` would not.
 */
export function seedPositions(
  nodes: Array<{ id: string; x: number; y: number }>,
  collideRadius: number
): void {
  const count = Math.max(nodes.length, 1);
  // Obsidian's `L`/`O`, with `v` = collide radius.
  const spread = Math.sqrt((SEED_RING_SCALE * count) / Math.PI + collideRadius * collideRadius) - collideRadius;

  nodes.forEach((node) => {
    const angle = hashSeed(`${node.id}:angle`) * 2 * Math.PI;
    const radius = collideRadius + Math.sqrt(hashSeed(`${node.id}:radius`)) * spread;
    node.x = radius * Math.cos(angle);
    node.y = radius * Math.sin(angle);
  });
}

/**
 * The d3 simulation's node shape. Graphology owns the attributes Sigma renders
 * from; the simulation works on these plain objects and the result is written
 * back, so one source of truth (the graph) is preserved.
 *
 * `size` is carried as well as the coordinates because d3's integration step
 * writes `x`/`y` onto these same objects and the tick handler copies the whole
 * record back — so a field that is absent here would silently blank the node's
 * radius on the first frame after a drag.
 */
export interface LayoutNode extends SimulationNodeDatum {
  id: string;
  size: number;
  hop?: number;
}

/**
 * Pointer travel, in CSS pixels, before a press on a node counts as a drag
 * rather than a click. Without a threshold the sub-pixel movement of an
 * ordinary click trips the drag path and the click-to-select that follows is
 * suppressed.
 */
export const DRAG_THRESHOLD_PX = 3;

/* ── Obsidian's graph forces ──
 *
 * The map previously ran graphology's ForceAtlas2 once, synchronously, and then
 * froze the result. It is replaced with the force set Obsidian actually uses,
 * read out of the shipped 1.13.7 bundle (`app/resources/obsidian.asar` →
 * `sim.js`, the "Graph Worker") rather than from documentation. The extraction
 * notes, with the engine internals quoted, are in `docs/obsidian-graph-physics.md`.
 *
 * The five forces and every default below are Obsidian's, verbatim. They are
 * deliberately NOT tuned to Nostos: the point of this change is that the map
 * behaves like the graph view it is being compared against.
 *
 * In Obsidian these same numbers are exposed as the four "Forces" sliders; the
 * two fixed values (collide radius/strength) are not in the UI at all.
 */
export const OBSIDIAN_FORCES = {
  /** `centerStrength` slider, default 0.1. Applied to forceX AND forceY. */
  centerStrength: 0.1,
  /**
   * `repelStrength` slider, default 10. Obsidian cube-maps the slider before
   * posting it (`setForces({repelStrength: e*e*e})`), so the force receives
   * 10³ = 1000 and the worker negates it (`x = -repelStrength`).
   */
  repelStrength: 1000,
  /** `linkDistance` slider, default 250. */
  linkDistance: 250,
  /** forceCollide radius. Fixed in Obsidian, not exposed as a setting. */
  collideRadius: 60,
  /** forceCollide strength. Fixed in Obsidian, not exposed as a setting. */
  collideStrength: 0.5,
  /** forceManyBody distanceMin, fixed in Obsidian. */
  distanceMin: 30,
  /** Integration damping; Obsidian's worker multiplies velocity by 0.6 per tick. */
  velocityDecay: 0.6,
  /** `alphaDecay = 1 - 0.001^(1/300)`, d3's default, at which alphaMin is reached in 300 ticks. */
  alphaDecay: 1 - Math.pow(0.001, 1 / 300),
  /** Below this alpha the simulation halts entirely. */
  alphaMin: 0.001,
} as const;

/** Radial distance added per hop from the pinned root in local-map layout. */
export const LOCAL_RING_STEP = OBSIDIAN_FORCES.linkDistance;
/** Weak radial pull per hop, layered on top of Obsidian's forces without replacing them. */
export const LOCAL_RADIAL_STRENGTH = 0.15;

/**
 * Obsidian's `linkStrength` slider default, applied as a MULTIPLIER on d3's own
 * per-link strength rather than as a replacement for it.
 *
 * Obsidian's worker keeps the function it captured before the override and
 * returns `E * captured(link, i, links)`, so the degree weighting survives a
 * slider move. Passing a plain number to d3 instead replaces that function with
 * a constant (`d3-force/src/link.js:108`). At the default the two happen to agree,
 * which is exactly why the distinction has to be written down rather than
 * discovered later when someone wires the slider up.
 */
export const OBSIDIAN_LINK_STRENGTH = 1;

/**
 * Alpha applied when the graph data or the forces change.
 *
 * Obsidian posts `alpha: .3, run: true` on `setData` and on `setForces`. Alpha is
 * an energy budget, not a force balance: the settle is "run hot, then cool to
 * alphaMin and stop", which is what makes the layout keep relaxing and then halt.
 */
export const SETTLE_ALPHA = 0.3;

/**
 * Alpha target held for the whole of a drag gesture.
 *
 * Obsidian re-posts `alpha: .3, alphaTarget: .3` on EVERY pointermove while a node
 * is dragged, so the simulation runs hot and continuously for the gesture and the
 * neighbours relax in real time. On release it posts `alphaTarget: 0`, and alpha
 * then decays naturally — which is the inertia the map was missing: previously
 * the layout froze the instant the pointer lifted (measured: 0 nodes moved at
 * every sample up to 2s after release).
 */
export const DRAG_ALPHA = 0.3;

/**
 * Obsidian's `forceCollide` radius, as a multiple of `linkDistance`.
 *
 * Used only for seeding: new nodes are spawned on a ring around the graph's
 * centre at roughly one link distance, which is what Obsidian's `setData` does
 * (`r = v + sqrt(rand) * O` where `O = sqrt(60*N*60/π)`), not the uniform random
 * square this component used before.
 */
export const SEED_RING_SCALE = 60 * 60;

/* Nostos theme tokens read at runtime from CSS custom properties. */
export function getCssVar(name: string, fallback: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

export function hexToRgba(hex: string, alpha: number): string {
  const c = hex.replace('#', '');
  const r = parseInt(c.substring(0, 2), 16);
  const g = parseInt(c.substring(2, 4), 16);
  const b = parseInt(c.substring(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

export function mixHex(first: string, second: string, amount: number): string {
  const a = first.replace('#', '');
  const b = second.replace('#', '');
  const channel = (source: string, offset: number) => parseInt(source.slice(offset, offset + 2), 16);
  const mix = (offset: number) => Math.round(channel(a, offset) + (channel(b, offset) - channel(a, offset)) * amount);
  return `#${[0, 2, 4].map((offset) => mix(offset).toString(16).padStart(2, '0')).join('')}`;
}

/**
 * How many clusters get a hue of their own.
 *
 * Three, drawn from the manifesto's accents (pine, clay, slate — see the
 * `--graph-community-*` tokens), not one per cluster. The map is a scatter, so
 * every pair of hues can end up side by side and the palette has to separate
 * on ALL pairs, not just adjacent ones. A restrained fourth (burgundy) collapsed
 * onto pine for red-green colour-blind readers, and a louder fourth would be
 * the "colourful AI aesthetic" the manifesto rules out. Smaller clusters keep
 * the neutral node ink.
 */
export const COMMUNITY_HUES = 3;

/** Light-theme values, used only when the CSS tokens cannot be read. */
const COMMUNITY_FALLBACK = ['#2f6e4e', '#b06a30', '#5560a8'];

/**
 * Light-theme categorical kind colours, matching `--map-kind-1..12` in
 * styles.css. Twelve fixed OKLCH hues 30° apart, all at L 58 so no kind reads
 * as "louder" than another; in the dark themes the same hues sit at L 68.
 * Every value is >= 3.9:1 against the light stage and >= 6.2:1 against the
 * dark one (WCAG 1.4.11 non-text contrast). Deuteranopia collapses neighbouring
 * hues by nature — the legend is what carries identity for those pairs.
 */
export const MAP_KIND_TOKEN_COUNT = 12;
export const UNTYPED_KIND_KEY = 'none';

const KIND_FALLBACK = [
  '#c74a4f',
  '#bd5a02',
  '#9e7207',
  '#7e8008',
  '#3a902b',
  '#0e8f71',
  '#028b92',
  '#0c85b1',
  '#4076d8',
  '#7d64d0',
  '#a455b3',
  '#bd4a86',
];
const KIND_NONE_FALLBACK = '#6f737c';

/** A cluster needs at least this many topics before it earns a hue. */
export const COMMUNITY_MIN_SIZE = 3;

export interface ThemeColors {
  node: string;
  nodeHead: string;
  edge: string;
  edgeActive: string;
  label: string;
  labelActive: string;
  /**
   * Fill for the label box Sigma draws behind the active node's own label.
   *
   * Sigma's built-in `drawDiscNodeHover` hardcodes `#FFF` here. On dark the
   * label ink is `--color-text-muted` (#c4c7d0), so the text landed on a white
   * box at 1.66:1 against a 4.5:1 text minimum — measured in the live app, with
   * Sigma's strongest available ink at no better than 1.16:1. Light mode
   * measured 17.2:1, which is exactly why this only ever showed up on dark.
   */
  labelBox: string;
  /** The stage field, painted as a halo behind every label so edges pass behind words. */
  field: string;
  /** Cluster hues, largest cluster first. See `COMMUNITY_HUES`. */
  communities: string[];
  /** Categorical topic kind colours `--map-kind-1..12`. */
  kinds: string[];
  /** Quiet neutral `--map-kind-none` for untyped topics. */
  kindNone: string;
}

export function readTheme(): ThemeColors {
  return {
    node: getCssVar('--graph-node', '#8b8e99'),
    nodeHead: getCssVar('--graph-node-head', '#4a4d57'),
    edge: getCssVar('--graph-edge', '#8b8e99'),
    edgeActive: getCssVar('--graph-edge-active', '#2b2d33'),
    label: getCssVar('--color-text-muted', '#6b6e78'),
    labelActive: getCssVar('--color-text-main', '#2b2d33'),
    labelBox: getCssVar('--graph-label-box', '#ffffff'),
    field: getCssVar('--bg-body', '#fbfbfc'),
    communities: COMMUNITY_FALLBACK.map((fallback, index) =>
      getCssVar(`--graph-community-${index + 1}`, fallback)
    ),
    kinds: KIND_FALLBACK.map((fallback, index) =>
      getCssVar(`--map-kind-${index + 1}`, fallback)
    ),
    kindNone: getCssVar('--map-kind-none', KIND_NONE_FALLBACK),
  };
}

/** A node drawn on its hover plate: viewport px, and the ink to draw it in. */
export interface PlateNode {
  x: number;
  y: number;
  /** Drawn node radius, viewport px. */
  size: number;
  label: string | null;
  /** The node disc's fill, as the renderer draws it. */
  nodeColor: string;
  labelColor: string;
  /** The node's own weight (hubs and the active node are 600). */
  labelWeight: string;
}

/**
 * A hovered or highlighted node on a themed label plate.
 *
 * Sigma 3 drew this on its 2D hover layer with `drawDiscNodeHover`, whose box
 * was a hardcoded `#FFF`: invisible-in-light but actively wrong on dark, where
 * it painted a white plate under `--color-text-muted` ink. Sigma 4 renders
 * everything in WebGL and has no 2D hover drawer, so the component draws the
 * plate itself on its own label canvas. The geometry is reproduced from
 * `sigma@3.0.3` (`drawDiscNodeHover`) with `boxFill` parameterised, so light
 * mode is unchanged apart from the token (which is `#ffffff` there) and dark
 * mode does not draw a white block.
 *
 * The label canvas sits ABOVE the WebGL stage, so the plate would cover the
 * node's own disc. Sigma 3 drew the hovered node again on top of its plate;
 * this does the same by repainting the disc after the plate.
 *
 * The flip matches `placeLabels`: without it the plate and the text
 * would part company on a node near the right edge.
 */
export function drawThemeNodeHover(
  context: CanvasRenderingContext2D,
  data: PlateNode,
  labelSize: number,
  labelFont: string,
  boxFill: string
): void {
  // Measured in the node's own weight, so the plate fits the text drawn on it.
  context.font = `${data.labelWeight} ${labelSize}px ${labelFont}`;

  context.fillStyle = boxFill;
  context.shadowOffsetX = 0;
  context.shadowOffsetY = 0;
  context.shadowBlur = 8;
  context.shadowColor = 'rgba(0, 0, 0, 0.35)';

  const PADDING = 2;
  if (typeof data.label === 'string') {
    const textWidth = context.measureText(data.label).width;
    const boxHeight = Math.round(labelSize + 2 * PADDING);
    const radius = Math.max(data.size, labelSize / 2) + PADDING;
    // The text starts `size + LABEL_OFFSET_PX` from the centre, not at the
    // plate's disc `radius`, so the plate must reach past the text's end.
    const boxWidth = Math.round(data.size + LABEL_OFFSET_PX - radius + textWidth + 6);
    const angleRadian = Math.asin(boxHeight / 2 / radius);
    const xDeltaCoord = Math.sqrt(Math.abs(radius ** 2 - (boxHeight / 2) ** 2));

    const overflowsRight = data.x + radius + boxWidth > logicalCanvasWidth(context.canvas);
    const flips = overflowsRight && data.x - radius - boxWidth >= 0;
    const direction = flips ? -1 : 1;

    context.beginPath();
    context.moveTo(data.x + direction * xDeltaCoord, data.y + boxHeight / 2);
    context.lineTo(data.x + direction * (radius + boxWidth), data.y + boxHeight / 2);
    context.lineTo(data.x + direction * (radius + boxWidth), data.y - boxHeight / 2);
    context.lineTo(data.x + direction * xDeltaCoord, data.y - boxHeight / 2);
    if (flips) {
      context.arc(data.x, data.y, radius, Math.PI - angleRadian, Math.PI + angleRadian);
    } else {
      context.arc(data.x, data.y, radius, angleRadian, -angleRadian);
    }
    context.closePath();
    context.fill();
  } else {
    context.beginPath();
    context.arc(data.x, data.y, data.size + PADDING, 0, Math.PI * 2);
    context.closePath();
    context.fill();
  }

  context.shadowBlur = 0;
  context.shadowColor = 'transparent';

  // The node itself, back on top of its plate.
  context.fillStyle = data.nodeColor;
  context.beginPath();
  context.arc(data.x, data.y, data.size, 0, Math.PI * 2);
  context.closePath();
  context.fill();

  context.fillStyle = data.labelColor;
  if (typeof data.label === 'string') {
    const textWidth = context.measureText(data.label).width;
    const overflowsRight =
      data.x + data.size + LABEL_OFFSET_PX + textWidth > logicalCanvasWidth(context.canvas);
    const flips = overflowsRight && data.x - data.size - LABEL_OFFSET_PX - textWidth >= 0;
    if (flips) {
      context.textAlign = 'right';
      context.fillText(data.label, data.x - data.size - LABEL_OFFSET_PX, data.y + labelSize / 3);
      context.textAlign = 'left';
    } else {
      context.fillText(data.label, data.x + data.size + LABEL_OFFSET_PX, data.y + labelSize / 3);
    }
  }
}

export function compareTopics(a: TopicDto, b: TopicDto): number {
  return (
    b.usageCount - a.usageCount ||
    (a.name < b.name ? -1 : a.name > b.name ? 1 : 0) ||
    (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
  );
}


/* ── Label layout ──
 *
 * Sigma's label grid decides which labels are *candidates* (one region of the
 * screen at a time), but it never checks whether two chosen labels overlap, and
 * its drawer paints straight onto edges. On a 54-node graph that produced pairs
 * like "Seneca/Death" printed on top of each other, and every edge under a word
 * cut through it.
 *
 * So the component does not let Sigma draw labels (`renderLabels: false`).
 * After each frame `selectLabelCandidates` picks candidates the way Sigma's grid
 * did, `placeLabels` lays them out in priority order — the active neighbourhood
 * first, then hub topics, then the rest by size — skipping any label that would
 * collide with one already placed, and `drawPlacedLabels` paints each one over
 * a halo in the field colour, so edges pass behind words instead of through
 * them. All of it lands on a 2D canvas the component layers over Sigma's stage.
 */

/** A node as the label pass sees it: viewport px, drawn radius. */
export interface LabelSource {
  key: string;
  x: number;
  y: number;
  /** Drawn node radius, viewport px (`sigma.scaleSize(size)`). */
  size: number;
  label: string | null;
  /** Always a candidate, whatever its size or its cell's density. */
  forced: boolean;
}

/** Sigma's label-culling settings, plus the stage they apply to. */
export interface LabelSelectionOptions {
  width: number;
  height: number;
  /** `labelGridCellSize`: side of one grid cell, px. */
  cellSize: number;
  /** `labelDensity`: candidates kept per cell. */
  density: number;
  /** `labelRenderedSizeThreshold`: smallest drawn radius that gets a label. */
  threshold: number;
}

/** How far past the stage a node may sit and still offer a label (Sigma's margins). */
const LABEL_SELECT_MARGIN_X = 150;
const LABEL_SELECT_MARGIN_Y = 50;

/**
 * The nodes whose labels are candidates this frame, in Sigma's own terms.
 *
 * Sigma used to do this before calling the label drawer; with its labels off
 * the component does it here. The rules are the ones in Sigma's
 * `computeDisplayedNodeLabels`: a grid of `cellSize` cells keeps its `density`
 * largest nodes (ties broken by key), forced nodes always qualify, and a node
 * needs a label, a drawn radius of at least `threshold` unless forced, and a
 * position on (or just off) the stage. Sigma's grid lives in un-zoomed graph
 * space with a per-cell quota that grows as `1 / ratio²`; on screen that is
 * `density` labels per `cellSize` square at any zoom, which is what this keeps.
 */
export function selectLabelCandidates<T extends LabelSource>(
  nodes: readonly T[],
  options: LabelSelectionOptions
): T[] {
  const { width, height, cellSize, density, threshold } = options;
  const quota = Math.ceil(density);
  const onStage = (node: T) =>
    node.x >= -LABEL_SELECT_MARGIN_X - node.size &&
    node.x <= width + LABEL_SELECT_MARGIN_X + node.size &&
    node.y >= -LABEL_SELECT_MARGIN_Y - node.size &&
    node.y <= height + LABEL_SELECT_MARGIN_Y + node.size;

  const cells = new Map<string, T[]>();
  const chosen: T[] = [];
  for (const node of nodes) {
    if (!node.label || !onStage(node)) continue;
    if (node.forced) {
      chosen.push(node);
      continue;
    }
    if (node.size < threshold) continue;
    const cell = `${Math.floor(node.x / cellSize)}:${Math.floor(node.y / cellSize)}`;
    const members = cells.get(cell);
    if (members) members.push(node);
    else cells.set(cell, [node]);
  }
  for (const members of cells.values()) {
    members.sort((a, b) => b.size - a.size || (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));
    chosen.push(...members.slice(0, quota));
  }
  return chosen;
}

/** Label priority tiers. Higher wins a collision. */
export const LABEL_PRIORITY = {
  /** Not part of the active neighbourhood while something is active. */
  dimmed: 0,
  normal: 1,
  /** The most-referenced topics: always candidates, drawn bolder. */
  hub: 2,
  /** The hovered/selected node and its neighbours. Never culled. */
  active: 3,
} as const;

/** Fraction of nodes, by usage, that count as hubs. */
export const HUB_FRACTION = 0.15;
/** Hub count bounds, so a tiny graph still has one and a big one stays selective. */
export const HUB_MIN = 1;
export const HUB_MAX = 12;

/** Halo stroke width in px. `strokeText` paints half of it outside the glyph. */
export const LABEL_HALO_WIDTH = 4;

/** Breathing room kept between two placed labels, in px. */
const LABEL_COLLISION_PAD = 2;

/**
 * The canvas width in the units labels are drawn in.
 *
 * The label canvas's backing store is `width × pixelRatio` and its context is
 * scaled to match, so node coordinates handed to a drawer are CSS pixels
 * while `canvas.width` is DEVICE pixels. Testing `x + textWidth > canvas.width`
 * therefore never fired on a 2x phone, the edge flip never ran, and right-edge
 * labels were cut off ("Epictetu", "Marcu") — while desktop at 1x looked fine.
 */
export function logicalCanvasWidth(canvas: HTMLCanvasElement): number {
  const css = canvas.clientWidth;
  if (css > 0) return css;
  const ratio =
    typeof window !== 'undefined' && window.devicePixelRatio > 0 ? window.devicePixelRatio : 1;
  return canvas.width / ratio;
}

export interface LabelCandidate {
  key: string;
  /** Node centre, viewport px. */
  x: number;
  y: number;
  /** Drawn node radius, viewport px. */
  size: number;
  label: string;
  color: string;
  weight: string;
  priority: number;
  /**
   * Drawn elsewhere — the hovered/selected node's label goes on its own plate
   * (`drawThemeNodeHover`), painted after the others — so this pass only
   * RESERVES its box, keeping other labels off it, and does not paint it twice.
   */
  reserveOnly?: boolean;
}

/** A node disc, viewport px, that labels should not cover. */
export interface DiscObstacle {
  key: string;
  x: number;
  y: number;
  size: number;
}

export interface PlacedLabel extends LabelCandidate {
  /** Where the text starts (left edge), viewport px. */
  textX: number;
  /** Text baseline, viewport px. */
  textY: number;
  align: 'left' | 'right';
}

interface Box {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

function intersects(a: Box, b: Box): boolean {
  return a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
}

/**
 * Lay labels out without overlaps.
 *
 * Each label prefers the right of its node and flips left when the right side
 * would leave the stage, cover another node, or hit a label already placed.
 * Labels never overlap each other: one that fits on neither side is dropped,
 * whatever its tier, because overprinted text is unreadable anyway — the
 * selection card lists the active node's neighbours by name. Covering another
 * node's disc is only tolerated for hub and active labels, which must stay.
 */
export function placeLabels(
  candidates: LabelCandidate[],
  measure: (label: string, weight: string) => number,
  stageWidth: number,
  labelSize: number,
  discs: DiscObstacle[] = []
): PlacedLabel[] {
  const ordered = [...candidates].sort(
    (a, b) =>
      b.priority - a.priority ||
      b.size - a.size ||
      (a.key < b.key ? -1 : a.key > b.key ? 1 : 0)
  );

  const placed: PlacedLabel[] = [];
  const boxes: Box[] = [];
  const seen = new Set<string>();
  const half = labelSize * 0.6;

  for (const candidate of ordered) {
    if (seen.has(candidate.key) || !candidate.label) continue;
    seen.add(candidate.key);

    const width = measure(candidate.label, candidate.weight);
    const top = candidate.y - half - LABEL_COLLISION_PAD;
    const bottom = candidate.y + half + LABEL_COLLISION_PAD;
    const rightStart = candidate.x + candidate.size + LABEL_OFFSET_PX;
    const leftEnd = candidate.x - candidate.size - LABEL_OFFSET_PX;

    const right: Box = { left: rightStart - LABEL_COLLISION_PAD, right: rightStart + width + LABEL_COLLISION_PAD, top, bottom };
    const left: Box = { left: leftEnd - width - LABEL_COLLISION_PAD, right: leftEnd + LABEL_COLLISION_PAD, top, bottom };

    const rightFits = rightStart + width <= stageWidth;
    const leftFits = leftEnd - width >= 0;
    const clearOfLabels = (box: Box) => !boxes.some((other) => intersects(box, other));
    const clearOfDiscs = (box: Box) =>
      !discs.some(
        (d) =>
          d.key !== candidate.key &&
          intersects(box, { left: d.x - d.size, right: d.x + d.size, top: d.y - d.size, bottom: d.y + d.size })
      );

    let side: 'left' | 'right' | null = null;
    if (candidate.reserveOnly) {
      side = rightFits || !leftFits ? 'right' : 'left';
    } else if (rightFits && clearOfLabels(right) && clearOfDiscs(right)) side = 'right';
    else if (leftFits && clearOfLabels(left) && clearOfDiscs(left)) side = 'left';
    else if (candidate.priority >= LABEL_PRIORITY.hub) {
      if (rightFits && clearOfLabels(right)) side = 'right';
      else if (leftFits && clearOfLabels(left)) side = 'left';
    }
    if (!side) continue;

    boxes.push(side === 'right' ? right : left);
    if (candidate.reserveOnly) continue;
    placed.push({
      ...candidate,
      align: side === 'right' ? 'left' : 'right',
      textX: side === 'right' ? rightStart : leftEnd,
      textY: candidate.y + labelSize / 3,
    });
  }

  return placed;
}

/** Paint placed labels: a halo in the field colour first, then the ink. */
export function drawPlacedLabels(
  context: CanvasRenderingContext2D,
  labels: PlacedLabel[],
  labelSize: number,
  labelFont: string,
  halo: string
): void {
  context.save();
  context.lineJoin = 'round';
  context.lineWidth = LABEL_HALO_WIDTH;
  context.strokeStyle = halo;
  for (const label of labels) {
    context.font = `${label.weight} ${labelSize}px ${labelFont}`;
    context.textAlign = label.align;
    context.strokeText(label.label, label.textX, label.textY);
    context.fillStyle = label.color;
    context.fillText(label.label, label.textX, label.textY);
  }
  context.restore();
}

/** Ids of the most-referenced topics, `HUB_FRACTION` of the graph. */
export function pickHubs(nodes: Array<{ id: string; usageCount: number }>): Set<string> {
  const count = Math.min(HUB_MAX, Math.max(HUB_MIN, Math.round(nodes.length * HUB_FRACTION)));
  return new Set(
    [...nodes]
      .sort((a, b) => b.usageCount - a.usageCount || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
      .slice(0, count)
      .map((n) => n.id)
  );
}

/* ── Clusters ── */

/**
 * Weighted label propagation: each topic repeatedly joins the cluster its
 * neighbours share the most notes with, until nothing changes.
 *
 * Deterministic — nodes are visited in `hashSeed` order and ties go to the
 * smaller cluster id — so a reload paints the same clusters in the same hues.
 * Returns each node's cluster RANK (0 = largest), which is what picks its hue:
 * colour follows the cluster's size order, not the order clusters were found.
 */
export function detectCommunities(
  ids: string[],
  edges: Array<{ sourceId: string; targetId: string; sharedNotes: number }>
): Map<string, number> {
  const neighbours = new Map<string, Array<{ id: string; weight: number }>>();
  for (const id of ids) neighbours.set(id, []);
  for (const edge of edges) {
    const a = neighbours.get(edge.sourceId);
    const b = neighbours.get(edge.targetId);
    if (!a || !b || edge.sourceId === edge.targetId) continue;
    const weight = Math.max(1, edge.sharedNotes);
    a.push({ id: edge.targetId, weight });
    b.push({ id: edge.sourceId, weight });
  }

  const label = new Map(ids.map((id) => [id, id]));
  const order = [...ids].sort((a, b) => hashSeed(a) - hashSeed(b));

  for (let round = 0; round < 30; round += 1) {
    let changed = false;
    for (const id of order) {
      const tally = new Map<string, number>();
      for (const n of neighbours.get(id)!) {
        const l = label.get(n.id)!;
        tally.set(l, (tally.get(l) ?? 0) + n.weight);
      }
      if (tally.size === 0) continue;
      let best = label.get(id)!;
      let bestWeight = tally.get(best) ?? -1;
      for (const [l, w] of tally) {
        if (w > bestWeight || (w === bestWeight && l < best)) {
          best = l;
          bestWeight = w;
        }
      }
      if (best !== label.get(id)) {
        label.set(id, best);
        changed = true;
      }
    }
    if (!changed) break;
  }

  const sizes = new Map<string, number>();
  for (const l of label.values()) sizes.set(l, (sizes.get(l) ?? 0) + 1);
  const ranked = [...sizes.entries()]
    .sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1))
    .map(([l]) => l);
  const rank = new Map(ranked.map((l, index) => [l, index]));

  return new Map(ids.map((id) => [id, rank.get(label.get(id)!)!]));
}

/* ── Framing ── */

/**
 * Centre-force strengths for a stage of the given shape.
 *
 * Obsidian's forces settle into a roughly round cloud, so on a 1310x650 desktop
 * stage the graph filled 88% of the height but only 51% of the width, and on a
 * portrait phone the reverse. Stretching the result afterwards was tried and
 * removed (see the note above `hashSeed`): it shears the layout and corrupts the
 * live physics. Weakening the centre pull along the stage's LONG axis instead
 * lets the simulation itself settle into the stage's shape, in one unit system,
 * with nothing rescaled afterwards.
 */
export function centerStrengths(width: number, height: number): { x: number; y: number } {
  const base = OBSIDIAN_FORCES.centerStrength;
  if (!(width > 0) || !(height > 0)) return { x: base, y: base };
  const aspect = Math.min(2.2, Math.max(1 / 2.2, width / height));
  return aspect >= 1 ? { x: base / aspect, y: base } : { x: base, y: base * aspect };
}

/**
 * Positions for topics with no connections: a tidy shelf under the graph.
 *
 * Left to the physics, an unconnected topic is pushed away by everything and
 * held only by the weak centre force, so it drifts to a corner — measured two
 * isolates settling well outside the main body, where the fit then had to frame
 * them and shrank everything else. Shelved in rows the width of the graph, they
 * stay visible and findable without deciding the zoom.
 *
 * Sigma's graph y axis points UP, so "under" is `minY - gap`.
 */
export function shelfPositions(
  count: number,
  extent: { minX: number; maxX: number; minY: number; maxY: number } | null,
  spacing: number
): Array<{ x: number; y: number }> {
  if (count <= 0) return [];
  const minX = extent?.minX ?? 0;
  const maxX = extent?.maxX ?? 0;
  const minY = extent?.minY ?? 0;
  const span = Math.max(spacing, maxX - minX);
  const perRow = Math.max(1, Math.floor(span / spacing) + 1);
  const centre = (minX + maxX) / 2;
  const positions: Array<{ x: number; y: number }> = [];
  for (let index = 0; index < count; index += 1) {
    const row = Math.floor(index / perRow);
    const inRow = Math.min(perRow, count - row * perRow);
    const column = index % perRow;
    positions.push({
      x: centre + (column - (inRow - 1) / 2) * spacing,
      y: minY - spacing * (extent ? 1 : 0) - row * spacing * 0.6,
    });
  }
  return positions;
}

/**
 * The rotation, in radians, that lays a settled layout's long axis along the
 * stage's long axis.
 *
 * A force layout has no preferred orientation, so its long axis lands wherever
 * the seed sent it: measured on a portrait phone, a graph lying sideways filled
 * 84% of the width and 47% of the height. Rotating it is a RIGID transform —
 * every distance the forces settled on is preserved — so unlike the per-axis
 * stretch removed earlier, it cannot distort the layout or the live physics.
 *
 * The long axis is the principal component of the node positions.
 */
export function alignmentRotation(
  points: Array<{ x: number; y: number }>,
  stageWidth: number,
  stageHeight: number
): number {
  if (points.length < 3 || !(stageWidth > 0) || !(stageHeight > 0)) return 0;
  const n = points.length;
  const mx = points.reduce((sum, p) => sum + p.x, 0) / n;
  const my = points.reduce((sum, p) => sum + p.y, 0) / n;
  let sxx = 0;
  let syy = 0;
  let sxy = 0;
  for (const p of points) {
    const dx = p.x - mx;
    const dy = p.y - my;
    sxx += dx * dx;
    syy += dy * dy;
    sxy += dx * dy;
  }
  // A near-round cloud has no long axis worth turning.
  const spread = Math.sqrt((sxx - syy) ** 2 + 4 * sxy * sxy);
  if (spread < 0.08 * (sxx + syy)) return 0;
  const principal = 0.5 * Math.atan2(2 * sxy, sxx - syy);
  const target = stageWidth >= stageHeight ? 0 : Math.PI / 2;
  return target - principal;
}

/* ── Topic kinds & filtering ── */

export interface KindLegendEntry {
  /** Kind id, or `UNTYPED_KIND_KEY` (`'none'`) for untyped topics. */
  key: string;
  /** The kind's name, or `"No kind"`. */
  label: string;
  /** CSS variable reference for DOM swatches so theme switches live-update. */
  color: string;
  count: number;
  visible: boolean;
  isUntyped: boolean;
}

const KIND_KEY_RE = /^(?:map-)?kind-(1[0-2]|[1-9])$/;

/** Zero-based index (0..11) for a palette colorKey (`kind-1`..`kind-12`), or null. */
export function kindIndexFromColorKey(colorKey: string | null | undefined): number | null {
  if (!colorKey) return null;
  const match = KIND_KEY_RE.exec(colorKey.trim());
  if (!match) return null;
  return Number.parseInt(match[1], 10) - 1;
}

/** Resolved hex colour for WebGL/canvas from `ThemeColors`. */
export function resolveKindColor(
  colorKey: string | null | undefined,
  theme: Pick<ThemeColors, 'kinds' | 'kindNone'>
): string {
  const index = kindIndexFromColorKey(colorKey);
  return index !== null && theme.kinds[index] ? theme.kinds[index] : theme.kindNone;
}

/** CSS custom property reference for DOM swatches and pills. */
export function kindCssVar(colorKey: string | null | undefined): string {
  const index = kindIndexFromColorKey(colorKey);
  return index !== null ? `var(--map-kind-${index + 1})` : 'var(--map-kind-none)';
}

/** Filter key for a topic (`kindId` or `'none'` when untyped). */
export function kindFilterKeyOf(kindId: string | null | undefined): string {
  const trimmed = kindId?.trim();
  return trimmed || UNTYPED_KIND_KEY;
}

/**
 * Parses `?kinds=<id>,<id>` from the URL. Returns `null` when the param is
 * absent (meaning all kinds are shown), or a Set of visible kind keys when
 * present (`''` yields an empty Set meaning all hidden).
 */
export function parseKindsParam(raw: string | null): Set<string> | null {
  if (raw === null) return null;
  const parts = raw
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part.length > 0);
  return new Set(parts);
}

/**
 * Serializes visible kind keys for the `kinds` URL query param. Returns `null`
 * (clearing the param) when `keys` is null or includes every key present in the
 * current scope.
 */
export function serializeKindsParam(
  keys: ReadonlySet<string> | null,
  presentKeys: readonly string[]
): string | null {
  if (!keys) return null;
  if (presentKeys.length > 0 && presentKeys.every((key) => keys.has(key))) {
    return null;
  }
  return [...keys].join(',');
}

/** Toggles one kind key on or off and returns the next `kinds` URL param value. */
export function toggleKindFilter(
  current: ReadonlySet<string> | null,
  toggledKey: string,
  presentKeys: readonly string[]
): string | null {
  const next = current ? new Set(current) : new Set(presentKeys);
  if (next.has(toggledKey)) {
    next.delete(toggledKey);
  } else {
    next.add(toggledKey);
  }
  return serializeKindsParam(next, presentKeys);
}

/** Filter topics by visible kind keys BEFORE the 150-topic cap. */
export function filterTopicsByKind<T extends { kindId?: string | null }>(
  topics: readonly T[],
  visibleKindKeys: ReadonlySet<string> | null
): T[] {
  if (!visibleKindKeys) return [...topics];
  return topics.filter((topic) => visibleKindKeys.has(kindFilterKeyOf(topic.kindId)));
}

/** A scoped graph with the kind filter applied and `hiddenByKind` counted. */
export interface KindFilteredScope extends ScopedGraph {
  /** Reachable topics the filter removed. The root is never one of them. */
  hiddenByKind: number;
}

/**
 * Apply the kind filter to an ALREADY-SCOPED graph, then cap.
 *
 * Scoping runs on the full graph first so hiding a kind never cuts the paths
 * that define "within N steps": `around=Education&depth=2` still reaches a
 * Person two steps away when Person is the only visible kind. The root is kept
 * even when its own kind is hidden, so the local map never vanishes under the
 * reader. `limit` applies after the filter, which is what frees room for
 * lower-ranked topics when a kind is hidden.
 */
export function filterScopedGraphByKind(
  scoped: ScopedGraph,
  visibleKindKeys: ReadonlySet<string> | null,
  rootId: string | null = null,
  limit: number = MAX_MAP_TOPICS
): KindFilteredScope {
  const visible = visibleKindKeys
    ? scoped.nodes.filter(
        (node) => node.id === rootId || visibleKindKeys.has(kindFilterKeyOf(node.kindId))
      )
    : scoped.nodes;
  const capped = visible.slice(0, Math.max(1, Math.floor(limit)));
  const kept = new Set(capped.map((node) => node.id));
  const hops = new Map([...scoped.hops].filter(([id]) => kept.has(id)));
  const edges = scoped.edges.filter((edge) => kept.has(edge.sourceId) && kept.has(edge.targetId));
  return {
    nodes: capped,
    edges,
    hops,
    totalInScope: visible.length,
    hiddenByKind: visibleKindKeys ? Math.max(0, scoped.totalInScope - visible.length) : 0,
  };
}

/** Options for the legend counts. */
export interface KindLegendOptions {
  /**
   * The topics the map actually drew (after the cap). A visible kind shows this
   * count; a hidden kind shows its in-scope count instead, so the reader knows
   * how many topics come back when they show it.
   */
  drawnItems?: ReadonlyArray<{ kindId?: string | null }>;
  /**
   * Whether the kind list itself is available. A topic with an unknown kind id
   * is untyped only when the list loaded and does not contain that id; before
   * then it is simply not counted as "No kind".
   */
  kindsLoaded?: boolean;
}

/**
 * The legend key for one item: a known kind id, `'none'`, or null when the kind
 * list is unavailable and the id cannot be resolved.
 */
function legendKeyOf(
  item: { kindId?: string | null },
  kindById: ReadonlyMap<string, TopicKindDto>,
  kindsLoaded: boolean
): string | null {
  const id = item.kindId?.trim();
  if (!id) return UNTYPED_KIND_KEY;
  if (kindById.has(id)) return id;
  return kindsLoaded ? UNTYPED_KIND_KEY : null;
}

function countLegendKeys(
  items: ReadonlyArray<{ kindId?: string | null }>,
  kindById: ReadonlyMap<string, TopicKindDto>,
  kindsLoaded: boolean
): Map<string, number> {
  const counts = new Map<string, number>();
  for (const item of items) {
    const key = legendKeyOf(item, kindById, kindsLoaded);
    if (key === null) continue;
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return counts;
}

/**
 * Build legend rows for the kinds present in the current scope, in `sortOrder`,
 * followed by `"No kind"` when untyped topics are present.
 *
 * Counts differ by state: a VISIBLE kind reports how many of its topics are
 * drawn on the map (after the cap), while a HIDDEN kind reports its in-scope
 * count, so the reader knows how many topics return when it is shown again.
 */
export function buildKindLegendEntries(
  items: ReadonlyArray<{ kindId?: string | null }>,
  kinds: readonly TopicKindDto[],
  visibleKindKeys: ReadonlySet<string> | null,
  options: KindLegendOptions = {}
): KindLegendEntry[] {
  const kindById = new Map(kinds.map((kind) => [kind.id, kind]));
  const kindsLoaded = options.kindsLoaded ?? true;
  const drawnItems = options.drawnItems ?? items;
  const inScopeCounts = countLegendKeys(items, kindById, kindsLoaded);
  const drawnCounts = countLegendKeys(drawnItems, kindById, kindsLoaded);

  const isVisible = (key: string) => (visibleKindKeys ? visibleKindKeys.has(key) : true);
  const countFor = (key: string) => (isVisible(key) ? drawnCounts.get(key) ?? 0 : inScopeCounts.get(key) ?? 0);

  const orderedKinds = [...kinds]
    .filter((kind) => (inScopeCounts.get(kind.id) ?? 0) > 0)
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));

  const entries: KindLegendEntry[] = orderedKinds.map((kind) => ({
    key: kind.id,
    label: kind.name,
    color: kindCssVar(kind.colorKey),
    count: countFor(kind.id),
    visible: isVisible(kind.id),
    isUntyped: false,
  }));

  if ((inScopeCounts.get(UNTYPED_KIND_KEY) ?? 0) > 0) {
    entries.push({
      key: UNTYPED_KIND_KEY,
      label: 'No kind',
      color: kindCssVar(null),
      count: countFor(UNTYPED_KIND_KEY),
      visible: isVisible(UNTYPED_KIND_KEY),
      isUntyped: true,
    });
  }

  return entries;
}
