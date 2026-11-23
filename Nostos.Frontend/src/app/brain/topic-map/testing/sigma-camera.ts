/**
 * The coordinate maths a Sigma instance applies to a camera state, ported from
 * sigma@4's `matrixFromCamera` / `framedGraphToViewport` / `viewportToFramedGraph`.
 *
 * Sigma cannot be imported outside WebGL, so a unit spec cannot ask a real
 * instance; copying its matrix code is what makes the assertions about where a
 * node lands true of the camera the app actually ships, rather than of a mock
 * whose conversions ignored the camera state. `graphDimensions` is the graph's
 * square extent (correctionRatio 1), padding is 0 and the angle is 0 — the
 * component runs with `autoRescale: false` around the origin.
 */
export interface CameraState {
  x: number;
  y: number;
  angle: number;
  ratio: number;
}

function identity(): number[] {
  return [1, 0, 0, 0, 1, 0, 0, 0, 1];
}

function scale(m: number[], x: number, y: number): number[] {
  m[0] = x;
  m[4] = y;
  return m;
}

function rotate(m: number[], r: number): number[] {
  const s = Math.sin(r);
  const c = Math.cos(r);
  m[0] = c;
  m[1] = s;
  m[3] = -s;
  m[4] = c;
  return m;
}

function translate(m: number[], x: number, y: number): number[] {
  m[6] = x;
  m[7] = y;
  return m;
}

function multiply(a: number[], b: number[]): number[] {
  const [a00, a01, a02, a10, a11, a12, a20, a21, a22] = a;
  const [b00, b01, b02, b10, b11, b12, b20, b21, b22] = b;
  a[0] = b00 * a00 + b01 * a10 + b02 * a20;
  a[1] = b00 * a01 + b01 * a11 + b02 * a21;
  a[2] = b00 * a02 + b01 * a12 + b02 * a22;
  a[3] = b10 * a00 + b11 * a10 + b12 * a20;
  a[4] = b10 * a01 + b11 * a11 + b12 * a21;
  a[5] = b10 * a02 + b11 * a12 + b12 * a22;
  a[6] = b20 * a00 + b21 * a10 + b22 * a20;
  a[7] = b20 * a01 + b21 * a11 + b22 * a21;
  a[8] = b20 * a02 + b21 * a12 + b22 * a22;
  return a;
}

function multiplyVec2(a: number[], b: { x: number; y: number }): { x: number; y: number } {
  return {
    x: b.x * a[0] + b.y * a[3] + a[6],
    y: b.x * a[1] + b.y * a[4] + a[7],
  };
}

export function matrixFromCamera(
  state: Partial<CameraState>,
  dimensions: { width: number; height: number },
  inverse: boolean
): number[] {
  // A camera target the map animates to carries only x, y and ratio; Sigma
  // keeps the current angle and merges the rest.
  const { angle = 0, ratio = 1, x = 0, y = 0 } = state;
  const { width, height } = dimensions;
  const matrix = identity();
  // A square graph extent makes the correction ratio 1 whatever the stage is.
  const smallestDimension = Math.min(width, height);
  if (!inverse) {
    multiply(matrix, scale(identity(), (2 * smallestDimension) / width, (2 * smallestDimension) / height));
    multiply(matrix, rotate(identity(), -angle));
    multiply(matrix, scale(identity(), 1 / ratio, 1 / ratio));
    multiply(matrix, translate(identity(), -x, -y));
  } else {
    multiply(matrix, translate(identity(), x, y));
    multiply(matrix, scale(identity(), ratio, ratio));
    multiply(matrix, rotate(identity(), angle));
    multiply(matrix, scale(identity(), width / smallestDimension / 2, height / smallestDimension / 2));
  }
  return matrix;
}

export function framedGraphToViewport(
  point: { x: number; y: number },
  state: Partial<CameraState>,
  dimensions: { width: number; height: number }
): { x: number; y: number } {
  const matrix = matrixFromCamera(state, dimensions, false);
  const viewport = multiplyVec2(matrix, point);
  return {
    x: ((1 + viewport.x) * dimensions.width) / 2,
    y: ((1 - viewport.y) * dimensions.height) / 2,
  };
}

export function viewportToFramedGraph(
  point: { x: number; y: number },
  state: Partial<CameraState>,
  dimensions: { width: number; height: number }
): { x: number; y: number } {
  const matrix = matrixFromCamera(state, dimensions, true);
  return multiplyVec2(matrix, {
    x: (point.x / dimensions.width) * 2 - 1,
    y: 1 - (point.y / dimensions.height) * 2,
  });
}
