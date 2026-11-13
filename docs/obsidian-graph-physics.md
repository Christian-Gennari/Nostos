# Obsidian 1.13.7 graph physics — extracted from the real app bundle

Source: snap `1.13.7` → `app/resources/obsidian.asar` → `sim.js` (the "Graph Worker")
and `app.js` (renderer + settings). This is not documentation or a blog post; it is
the shipped code, so these values are authoritative.

## Engine

d3-force (velocity Verlet), compiled to **WebAssembly** (`simulate`, `complete`,
`visitCharge`, `visitCollide`, `manyBody`) with a **pure-JS d3-fallback** in the same
file that mirrors the WASM semantics. Runs in a Worker (`/sim.js`, name "Graph Worker").
Rendering is PIXI/WebGL; the worker only computes positions and posts a Float32Array.

## The worker's module-level constants (sim.js)

```js
var Q = 1,                            // alpha
    B = 1 - Math.pow(.001, 1/300),    // alphaDecay ≈ 0.02276
    y = 0,                            // alphaTarget
    c = .1,                           // centerStrength
    E = 1,                            // linkStrength (multiplier)
    v = 250,                          // linkDistance
    x = -1e3;                         // charge strength (= -repelStrength)
```

`velocityDecay` is `0.6` (in the tick: `t.x += t.vx *= .6`; the WASM path passes
`.6` to `complete()`).

## The force set (sim.js: `b = [_, N, q, R, k]`)

| force | built as | final value |
| --- | --- | --- |
| forceX | `.strength(.1)` | strength = **centerStrength** |
| forceY | `.strength(.1)` | strength = **centerStrength** |
| forceLink | `.id(d => d.id).distance(250)` | distance = **linkDistance**; strength = **linkStrength × 1/min(deg(src), deg(tgt))** |
| forceManyBody | `.strength(() => -1e3).distanceMin(30)` | strength = **−repelStrength**, distanceMin **30**, theta default 0.81 |
| forceCollide | `.radius(60).strength(.5)` | radius **60**, strength **0.5** |

Note there is **no `forceCenter`** and no `distanceMax` — centering is done by
forceX/forceY at strength `centerStrength`, i.e. a soft pull per-axis, not a hard
centroid snap.

## The step (sim.js)

```js
X = 1e3 / 60;                     // one frame = 16.67ms, via setTimeout not rAF

function W() {                    // the step
  O = null;
  if (Q > .001) {                 // alphaMin
    P();                          // schedule the NEXT frame
    if (M.length === 0) return;   // no nodes -> stop
    Q += (y - Q) * B;             // alpha decay
    K();                          // one tick: run all 5 forces with alpha Q, then integrate
    Y();                          // post positions back to the renderer
  }
}
function P() { O || (O = setTimeout(W, X)); }
```

Integration (per node, per tick):

```js
if (fx == null) { x += vx *= .6; vx = 0 } else { x = fx, vx = 0 }
if (fy == null) { y += vy *= .6; vy = 0 } else { y = fy, vy = 0 }
```

So: **velocity is persistent and damped by 0.6 each frame**; a pinned node is snapped
to `fx/fy` and its velocity zeroed. The loop stops entirely once alpha ≤ 0.001.

## Interaction → alpha (app.js renderer)

- **Set data** (`setData`, only when nodes/links actually changed):
  `postMessage({nodes, links, alpha: .3, run: true})`
- **Change a force** (`setForces`): `postMessage({forces, alpha: .3, run: true})`
- **Drag move** (renderer pointermove over a node):
  ```js
  node.fx = local.x; node.fy = local.y;
  worker.postMessage({ alpha: .3, alphaTarget: .3, run: true,
                       forceNode: { id, x: local.x, y: local.y } });
  ```
  i.e. pin the node hard at the pointer AND hold `alphaTarget = 0.3` so the
  simulation runs **hot and continuously** for the whole gesture. Neighbours relax in
  real time; the graph never freezes mid-drag.
- **Drag end** (pointerup / pointerupoutside):
  ```js
  node.fx = null; node.fy = null;
  worker.postMessage({ alphaTarget: 0, forceNode: { id, x: null, y: null } });
  ```
  Un-pin and drop `alphaTarget` back to 0 so alpha decays naturally and the layout
  **settles smoothly with inertia** — no snap-back, no freeze.
- **Click vs drag**: a click is only emitted if pointer travel² ≤ 25 (5px threshold).
- Panning/zooming is camera-only; it never touches node coordinates.

## Seeding of NEW nodes (app.js `setData`)

```js
var I = newNodes.length, L = 60*I*60,
    O = Math.sqrt(L/Math.PI + v*v) - v,   // v = sqrt(max r² over existing nodes)
    F = Math.sqrt(L);
for each new node f:
  gather already-positioned related nodes -> centroid (B/H, V/H), count H
  if H > 0:  f.x = centroidX + (rand-.5)*F ;  f.y = centroidY + (rand-.5)*F
  else:      angle = 2π*rand; r = v + sqrt(rand)*O;  f.x = r cos ; f.y = r sin
```

New nodes spawn **next to the neighbours they are already placed with** (jittered by
`sqrt(60*I*60)`), or on a ring of radius ≈ `v` for isolated ones. That is the
"springy big bang" people describe — NOT a uniform random square.

## Settings slider → parameter mapping (app.js)

Defaults (`g0`):

```js
{ centerStrength: u0(.1,.01), repelStrength: 10, linkStrength: u0(1,.01), linkDistance: 250 }
```

| UI slider | limits | posted value |
| --- | --- | --- |
| Center force | 0 – 1, any, step .01 | `centerStrength` directly (default **0.1**) |
| Repel force | 0 – 20, any, step .01 | `repelStrength: e*e*e` (default 10 → **1000**) |
| Link force | 0 – 1, any, step .01 | `linkStrength` directly (default **1**) |
| Link distance | 30 – 500, step 1 | `linkDistance` directly (default **250**) |

In the worker, `x = -repelStrength` and `Math.abs(repelStrength) < 1 && (x = -1)`.

Collide radius/strength is **not** exposed in the UI — it is fixed at 60 / 0.5.

## The user's own vault (for reference)

`~/obsidian/scriptorium/.obsidian/graph.json`:
`centerStrength 0.5187`, `repelStrength 10`, `linkStrength 1`, `linkDistance 250`,
`nodeSizeMultiplier 0.945`, `lineSizeMultiplier 1`. So Christian runs the stock
repel/link values with a much stronger center force and stock 250 link distance.

## How the Nostos topic map implements this

The topic map runs this model in the browser with **d3-force** itself.
`Nostos.Frontend/src/app/brain/topic-map/topic-map-simulation.ts` owns the
simulation and the drag lifecycle; `topic-map.helpers.ts` holds `OBSIDIAN_FORCES`,
every default above, verbatim, in one place. It replaced
`graphology-layout-forceatlas2`, which ran 400 synchronous iterations, froze the
result and rescaled it into stage pixels.

What is Obsidian's, unchanged:

- the five forces: `forceX`/`forceY` at `centerStrength`, `forceLink` at
  `linkDistance` with degree-weighted strength, `forceManyBody` at
  `-repelStrength` with `distanceMin` 30, `forceCollide` at radius 60 / strength
  0.5 — and no `forceCenter`;
- d3's own defaults for the rest of the engine, which is why the constants can be
  named and left alone: `velocityDecay` 0.6, `alphaMin` 0.001 and
  `alphaDecay = 1 - 0.001^(1/300)` — 300 ticks from alpha 1 down to `alphaMin`, so
  a settle that starts at `SETTLE_ALPHA` 0.3 halts after roughly 250 (about four
  seconds at 60 fps);
- alpha as an energy budget, not a force balance: the layout starts at
  `SETTLE_ALPHA` 0.3, and a drag holds `DRAG_ALPHA` 0.3 as the target for the whole
  gesture, dropping it to 0 on release so the graph cools with inertia;
- seeding: nodes start on a ring around the centre (`seedPositions`, an angle and
  radius drawn from a hash of the node id, so a capture and a test run stay
  reproducible), not in a uniform random square;
- dragging: the dragged node is pinned with `fx`/`fy` at the pointer, the camera is
  disabled for the gesture, and a press only becomes a drag after
  `DRAG_THRESHOLD_PX` (3 CSS pixels) of travel.

Where Nostos intentionally differs, and why:

- **No worker.** The simulation ticks on the main thread, one
  `requestAnimationFrame` tick per frame, and only while alpha is above
  `alphaMin`. The result is written back into the graphology graph Sigma renders
  from, so the graph stays the single source of truth; there is no Float32Array
  handoff.
- **A synchronous pre-settle.** `initLayout` ticks the simulation synchronously
  (at most 300 ticks) before Sigma mounts the graph, so the map opens as a settled
  layout rather than as its seed ring. Obsidian lets its worker post frames as
  they are computed.
- **Per-axis centre strength.** `centerStrengths` weakens the pull along the
  stage's long axis (capped at 2.2:1) so a phone or a wide desktop stage settles
  into its own shape. The per-axis stretch that used to do this to the finished
  layout is gone: it sheared the physics, and the live loop inflated the graph by
  2.03× on a portrait stage once it ran at the stretched constants
  (`scripts/map-audit/live-loop-behaviour.mjs`).
- **A rigid long-axis rotation.** `alignmentRotation` turns the settled cloud so
  its principal axis lies along the stage's. A rotation preserves every distance
  the forces produced, so unlike the removed stretch it cannot distort them.
- **Unconnected topics are shelved, not simulated.** Topics with no edge are held
  out of the simulation and laid out in rows under the graph (`shelfPositions`),
  where they stay findable instead of deciding the camera fit.
- **No force sliders.** The four Obsidian sliders are constants: the map has one
  behaviour rather than a settings panel.
- **Framing is the camera's.** Positions stay in graph units and Sigma's camera
  fits them (`FIT_OCCUPANCY` 0.88, uniform on both axes), as Obsidian's pan/zoom
  does; nothing is rescaled into stage pixels.

The five drawbacks listed for the old implementation are therefore all gone: the
layout keeps relaxing after a drag, nodes carry velocity, centring is
forceX/forceY, collisions are a hard radius, and there is no anisotropic stretch
left to shear the geometry.
