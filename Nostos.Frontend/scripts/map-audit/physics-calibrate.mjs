/**
 * Physics calibration for the topic map.
 *
 * Runs Obsidian's force set (see docs/obsidian-graph-physics.md) over the real
 * topic graph, sweeping the scale-dependent constants, and reports settled
 * extent / overlap / nearest-neighbour distance for each candidate so the
 * chosen numbers come from a dose-response curve rather than a guess.
 *
 * Usage: node scripts/map-audit/physics-calibrate.mjs /tmp/gp-graph.json 990 730
 */
import { readFileSync } from 'node:fs';
import {
  forceSimulation, forceX, forceY, forceLink, forceManyBody, forceCollide,
} from 'd3-force';

const file = process.argv[2] ?? '/tmp/gp-graph.json';
const STAGE_W = Number(process.argv[3] ?? 990);
const STAGE_H = Number(process.argv[4] ?? 730);

const data = JSON.parse(readFileSync(file, 'utf8'));
const NODE_SIZE_MIN = 4;
const NODE_SIZE_MAX = 16;
const FIT_OCCUPANCY = 0.88;

const usages = data.nodes.map((n) => Math.max(0, n.usageCount));
const minUsage = Math.min(...usages);
const maxUsage = Math.max(...usages);
const usageRange = Math.max(1, maxUsage - minUsage);

function hashSeed(value) {
  let hash = 2166136261;
  for (let i = 0; i < value.length; i += 1) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0) / 4294967295;
}

function build() {
  const aspect = STAGE_W / STAGE_H;
  const seedW = aspect >= 1 ? aspect : 1;
  const seedH = aspect >= 1 ? 1 : 1 / aspect;
  const nodes = data.nodes.map((n) => {
    const ratio = (Math.max(0, n.usageCount) - minUsage) / usageRange;
    return {
      id: n.id,
      size: NODE_SIZE_MIN + (NODE_SIZE_MAX - NODE_SIZE_MIN) * Math.sqrt(ratio),
      x: (hashSeed(`${n.id}:x`) * 2 - 1) * seedW,
      y: (hashSeed(`${n.id}:y`) * 2 - 1) * seedH,
    };
  });
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const links = data.edges
    .filter((e) => byId.has(e.sourceId) && byId.has(e.targetId))
    .map((e) => ({ source: e.sourceId, target: e.targetId }));
  return { nodes, links };
}

/** Run to convergence the way the worker's loop does, and report geometry. */
function run({ linkDistance, collideRadius, repelStrength, centerStrength, velocityDecay, linkStrength }) {
  const { nodes, links } = build();
  const sim = forceSimulation(nodes)
    .velocityDecay(velocityDecay)
    .alphaDecay(1 - Math.pow(0.001, 1 / 300))
    .alphaMin(0.001)
    .alphaTarget(0)
    .force('x', forceX(0).strength(centerStrength))
    .force('y', forceY(0).strength(centerStrength))
    .force('link', forceLink(links).id((d) => d.id).distance(linkDistance).strength(linkStrength))
    .force('charge', forceManyBody().strength(-repelStrength).distanceMin(30))
    .force('collide', forceCollide().radius(collideRadius).strength(0.5));

  sim.alpha(0.3);
  let ticks = 0;
  while (sim.alpha() > sim.alphaMin() && ticks < 5000) { sim.tick(); ticks += 1; }
  sim.stop();

  const xs = nodes.map((n) => n.x);
  const ys = nodes.map((n) => n.y);
  const spanX = Math.max(...xs) - Math.min(...xs);
  const spanY = Math.max(...ys) - Math.min(...ys);

  // Camera fit: uniform scale to reach FIT_OCCUPANCY of the tighter axis.
  const scale = Math.min((STAGE_W * FIT_OCCUPANCY) / spanX, (STAGE_H * FIT_OCCUPANCY) / spanY);

  const pts = nodes.map((n) => ({ id: n.id, x: n.x * scale, y: n.y * scale, r: n.size }));
  let overlaps = 0;
  const nn = [];
  for (let i = 0; i < pts.length; i += 1) {
    let best = Infinity;
    for (let j = 0; j < pts.length; j += 1) {
      if (i === j) continue;
      const d = Math.hypot(pts[i].x - pts[j].x, pts[i].y - pts[j].y);
      if (d < best) best = d;
      if (j > i && d < pts[i].r + pts[j].r - 0.001) overlaps += 1;
    }
    nn.push(best);
  }
  nn.sort((a, b) => a - b);

  const screenSpanX = spanX * scale;
  const screenSpanY = spanY * scale;

  return {
    ticks,
    spanX: +spanX.toFixed(0),
    spanY: +spanY.toFixed(0),
    aspect: +(spanX / spanY).toFixed(2),
    scale: +scale.toFixed(4),
    fillX: +(screenSpanX / STAGE_W).toFixed(3),
    fillY: +(screenSpanY / STAGE_H).toFixed(3),
    minNN: +nn[0].toFixed(1),
    medianNN: +nn[Math.floor(nn.length / 2)].toFixed(1),
    overlaps,
  };
}

const base = { linkDistance: 250, collideRadius: 60, repelStrength: 1000, centerStrength: 0.1, velocityDecay: 0.6, linkStrength: 1 };
console.log('graph:', data.nodes.length, 'nodes,', data.edges.length, 'edges; stage', STAGE_W, 'x', STAGE_H);
console.log('\n--- Obsidian stock values, unmodified (control) ---');
console.log(JSON.stringify(run(base)));

console.log('\n--- sweep linkDistance (collide 60, repel 1000, center 0.1) ---');
for (const linkDistance of [60, 90, 120, 160, 200, 250]) {
  console.log(linkDistance, JSON.stringify(run({ ...base, linkDistance })));
}

console.log('\n--- sweep repelStrength at linkDistance 120 ---');
for (const repelStrength of [400, 700, 1000, 1500, 2500, 4000]) {
  console.log(repelStrength, JSON.stringify(run({ ...base, linkDistance: 120, repelStrength })));
}

console.log('\n--- sweep collideRadius at linkDistance 120 / repel 1000 ---');
for (const collideRadius of [20, 26, 32, 40, 50, 60]) {
  console.log(collideRadius, JSON.stringify(run({ ...base, linkDistance: 120, collideRadius })));
}

console.log('\n--- sweep centerStrength at linkDistance 120 / repel 1000 / collide 32 ---');
for (const centerStrength of [0.02, 0.05, 0.1, 0.2, 0.35, 0.5]) {
  console.log(centerStrength, JSON.stringify(run({ ...base, linkDistance: 120, collideRadius: 32, centerStrength })));
}
