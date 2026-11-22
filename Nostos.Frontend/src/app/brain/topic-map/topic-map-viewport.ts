import Graph from 'graphology';
import Sigma from 'sigma';

import {
  FIT_OCCUPANCY,
  LABEL_EDGE_ALLOWANCE_FRACTION,
  type MapCamera,
} from './topic-map.helpers';
import { isPhoneViewport } from '../../ui/breakpoints';

export interface GraphExtent {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
}

export interface OverlayElements {
  legend?: HTMLElement;
  hud?: HTMLElement;
}

export interface FrameLoadOptions {
  sigma: Sigma | null;
  graph: Graph | null;
  selectedId: string | null;
  cameraInput: MapCamera | null;
  centerOnLoad: boolean;
  containerEl?: HTMLElement;
  overlays?: OverlayElements;
}

export interface StageObserveOptions {
  container: HTMLElement;
  getSigma: () => Sigma | null;
  getGraph: () => Graph | null;
  getSelectedId: () => string | null;
  getCameraInput: () => MapCamera | null;
  getCenterOnLoad: () => boolean;
  getOverlayElements: () => OverlayElements;
}

export interface RevealSelectedOptions {
  getSigma: () => Sigma | null;
  getGraph: () => Graph | null;
  getSelectedId: () => string | null;
  getContainerEl: () => HTMLElement | undefined;
  getHudEl: () => HTMLElement | undefined;
}

/**
 * Compute the graph's extent in graph coordinates.
 */
export function graphExtent(graph: Graph | null, only?: Set<string>): GraphExtent | null {
  if (!graph || graph.order === 0) return null;
  let minX = Infinity;
  let maxX = -Infinity;
  let minY = Infinity;
  let maxY = -Infinity;
  graph.forEachNode((node, attrs) => {
    if (only && !only.has(node)) return;
    const x = Number(attrs['x']) || 0;
    const y = Number(attrs['y']) || 0;
    if (x < minX) minX = x;
    if (x > maxX) maxX = x;
    if (y < minY) minY = y;
    if (y > maxY) maxY = y;
  });
  if (!Number.isFinite(minX)) return null;
  return { minX, maxX, minY, maxY };
}

/**
 * Coordinates Sigma camera framing, overlay inset calculations, ResizeObserver
 * updates, and Focus mode (Fullscreen API + CSS fallback) for TopicMapComponent.
 */
export class TopicMapViewport {
  /** The host's start view (`camera` / `centerOnLoad`) applies to the first build only. */
  private framed = false;
  /**
   * The camera of the graph a rebuild replaced, when the host tracks the
   * camera: a refreshed topic set keeps the reader's view rather than jumping.
   */
  private carriedCamera: MapCamera | null = null;
  /** The last camera this viewport framed itself, to tell a settled relayout from an idle one. */
  private lastFrameTarget: { x: number; y: number; ratio: number } | null = null;
  /** A reader interaction since the mount means the settle re-frame must keep its hands off. */
  private userMovedCamera = false;
  private settleFrameId: number | null = null;
  private detachUserCameraListeners: (() => void) | null = null;
  /** True while Focus mode is the CSS fallback rather than the Fullscreen API. */
  private pseudoFullscreen = false;
  private resizeObserver: ResizeObserver | null = null;
  private initialFrameId: number | null = null;
  private revealFrameId: number | null = null;
  private readonly pendingTimeouts = new Set<number>();

  private stageEl: HTMLElement | null = null;
  private fullscreenListenerAttached = false;
  private onFullscreenChangeCallback: ((fullscreen: boolean) => void) | null = null;
  private onRefreshCallback: (() => void) | null = null;
  private disposed = false;

  private readonly handleFullscreenChange = (): void => {
    this.onFullscreenChangeCallback?.(document.fullscreenElement === this.stageEl);
    this.scheduleRefresh(0);
  };

  /** Attach document fullscreenchange listener for the stage lifecycle. */
  attachFullscreen(
    stageEl: HTMLElement,
    onFullscreenChange: (fullscreen: boolean) => void,
    onRefresh: () => void
  ): void {
    this.stageEl = stageEl;
    this.onFullscreenChangeCallback = onFullscreenChange;
    this.onRefreshCallback = onRefresh;
    if (!this.fullscreenListenerAttached) {
      document.addEventListener('fullscreenchange', this.handleFullscreenChange);
      this.fullscreenListenerAttached = true;
    }
  }

  async toggleFullscreen(isCurrentlyFullscreen: boolean): Promise<void> {
    if (isCurrentlyFullscreen) {
      await this.exitFullscreen();
      return;
    }
    const stage = this.stageEl;
    if (!stage) return;
    if (document.fullscreenEnabled && stage.requestFullscreen) {
      await stage.requestFullscreen();
      return;
    }
    // iPhone Safari has no Fullscreen API for anything but video, so Focus mode
    // used to do nothing there — on the one device that needs the room most.
    // Fall back to pinning the stage over the page (`.is-fullscreen` in CSS).
    this.pseudoFullscreen = true;
    this.onFullscreenChangeCallback?.(true);
    this.scheduleRefresh(0);
  }

  async exitFullscreen(): Promise<void> {
    if (this.pseudoFullscreen) {
      this.pseudoFullscreen = false;
      this.onFullscreenChangeCallback?.(false);
      this.scheduleRefresh(0);
      return;
    }
    if (document.fullscreenElement && document.exitFullscreen) {
      await document.exitFullscreen();
    }
  }

  /**
   * Capture the current camera before a rebuild if the host preserves view state.
   *
   * An in-flight animation is deliberately not captured: its state is a frame
   * part-way to the target, and carrying it forward froze the map at the
   * default camera or half-zoomed while the mount animation was still running.
   */
  captureCarriedCamera(sigma: Sigma | null, keepsView: boolean): void {
    const camera = sigma?.getCamera();
    const animated = camera && typeof camera.isAnimating === 'function' && camera.isAnimating();
    this.carriedCamera = sigma && camera && keepsView && !animated ? { ...camera.getState() } : null;
  }

  /**
   * Frame the newly mounted Sigma instance, schedule a post-layout re-fit, and
   * attach a ResizeObserver to keep Sigma in sync with container dimensions.
   */
  observeStage(options: StageObserveOptions): void {
    this.disposeRebuildObservers();
    const firstBuild = !this.framed;
    this.userMovedCamera = false;
    this.lastFrameTarget = null;
    this.attachUserCameraListeners(options.container);

    const { container } = options;
    this.initialFrameId = window.requestAnimationFrame(() => {
      this.initialFrameId = null;
      if (this.disposed) return;
      const sigma = options.getSigma();
      if (!sigma) return;
      sigma.refresh();
      this.frameOnLoad({
        sigma,
        graph: options.getGraph(),
        selectedId: options.getSelectedId(),
        cameraInput: options.getCameraInput(),
        centerOnLoad: options.getCenterOnLoad(),
        containerEl: container,
        overlays: options.getOverlayElements(),
      });
      this.framed = true;
      this.carriedCamera = null;
    });

    // The force layout can finish after the first painted frame. Re-frame once
    // from the settled extent, so `?focus=` is not framed against seed
    // positions — but never after the reader has taken control of the camera,
    // and never on a rebuild, where the kept camera is the reader's view.
    if (firstBuild) {
      this.settleFrameId = window.requestAnimationFrame(() => {
        this.settleFrameId = null;
        if (this.disposed || !this.framed || this.userMovedCamera) return;
        const sigma = options.getSigma();
        const graph = options.getGraph();
        const selectedId = options.getSelectedId();
        if (!sigma || !graph || !selectedId || !options.getCenterOnLoad()) return;
        const state = this.focusedCameraState(
          sigma,
          graph,
          selectedId,
          this.fitOccupancy(),
          this.overlayInsets(container, options.getOverlayElements())
        );
        if (!state) return;
        if (this.lastFrameTarget && sameFrame(state, this.lastFrameTarget)) return;
        this.lastFrameTarget = state;
        sigma.getCamera().animate(state, { duration: 350 });
      });
    }

    if (typeof ResizeObserver !== 'undefined') {
      let lastWidth = container.clientWidth;
      let lastHeight = container.clientHeight;
      this.resizeObserver = new ResizeObserver(() => {
        const sigma = options.getSigma();
        if (!sigma || this.disposed) return;
        const w = container.clientWidth;
        const h = container.clientHeight;
        sigma.refresh();
        // A genuine size change invalidates the framing, so re-fit — but only
        // when the size actually moved, so a sub-pixel observer tick does not
        // yank the camera back while the user is exploring.
        if (Math.abs(w - lastWidth) > 1 || Math.abs(h - lastHeight) > 1) {
          lastWidth = w;
          lastHeight = h;
          this.fitGraph(
            sigma,
            options.getGraph(),
            container,
            options.getOverlayElements()
          );
        }
      });
      this.resizeObserver.observe(container);
    }

    // Frame the graph on first paint.
    this.frameOnLoad({
      sigma: options.getSigma(),
      graph: options.getGraph(),
      selectedId: options.getSelectedId(),
      cameraInput: options.getCameraInput(),
      centerOnLoad: options.getCenterOnLoad(),
      containerEl: container,
      overlays: options.getOverlayElements(),
    });
  }

  /** The first view of a built graph: where the host left it, its selection, or all of it. */
  frameOnLoad(options: FrameLoadOptions): void {
    const { sigma, graph, selectedId, cameraInput, centerOnLoad, containerEl, overlays } = options;
    const kept = this.carriedCamera ?? (this.framed ? null : cameraInput);
    if (kept) {
      sigma?.getCamera().setState(kept);
    } else if (!this.framed && centerOnLoad && selectedId && graph?.hasNode(selectedId)) {
      this.frameFocused(
        sigma,
        graph,
        selectedId,
        this.fitOccupancy(),
        this.overlayInsets(containerEl, overlays)
      );
    } else {
      this.fitGraph(sigma, graph, containerEl, overlays);
    }
  }

  /**
   * The fraction of each stage dimension the graph may occupy.
   */
  fitOccupancy(): { x: number; y: number } {
    return {
      x: FIT_OCCUPANCY - LABEL_EDGE_ALLOWANCE_FRACTION,
      y: FIT_OCCUPANCY,
    };
  }

  /**
   * Insets occupied by the overlays. The graph fills the whole canvas, while
   * its initial and fit framing keeps nodes in the open band between the legend
   * and the HUD.
   */
  overlayInsets(containerEl?: HTMLElement, overlays?: OverlayElements): { top: number; bottom: number } {
    const stage = containerEl?.getBoundingClientRect();
    if (!stage) return { top: 0, bottom: 0 };
    const legend = overlays?.legend?.getBoundingClientRect();
    const hud = overlays?.hud?.getBoundingClientRect();
    return {
      top: legend && legend.height ? Math.max(0, legend.bottom - stage.top) : 0,
      bottom: hud && hud.height ? Math.max(0, stage.bottom - hud.top) : 0,
    };
  }

  /**
   * Convert a point in graph coordinates to the camera's framed coordinates,
   * using Sigma's own conversion so the result is exact for any viewport aspect.
   */
  graphPointToFramed(sigma: Sigma | null, x: number, y: number): { x: number; y: number } | null {
    if (!sigma) return null;

    const { width, height } = sigma.getDimensions();
    if (!width || !height) return null;

    const probe = { x: 0.5, y: 0.5, angle: 0, ratio: 1 };
    const viewportPoint = sigma.graphToViewport({ x, y }, { cameraState: probe });
    const framed = sigma.viewportToFramedGraph(viewportPoint, { cameraState: probe });
    if (!Number.isFinite(framed.x) || !Number.isFinite(framed.y)) return null;
    return { x: framed.x, y: framed.y };
  }

  /**
   * The camera state that frames the whole graph, derived from Sigma's own
   * coordinate conversion rather than from hand-rolled maths.
   */
  fitCameraState(
    sigma: Sigma | null,
    graph: Graph | null,
    occupancy: { x: number; y: number } = { x: FIT_OCCUPANCY, y: FIT_OCCUPANCY },
    insets: { top: number; bottom: number } = { top: 0, bottom: 0 }
  ): { x: number; y: number; ratio: number } | null {
    const extent = graphExtent(graph);
    if (!extent || !sigma) return null;

    const { width, height } = sigma.getDimensions();
    if (!width || !height) return null;

    const probe = { x: 0.5, y: 0.5, angle: 0, ratio: 1 };
    const cornerA = sigma.graphToViewport({ x: extent.minX, y: extent.minY }, { cameraState: probe });
    const cornerB = sigma.graphToViewport({ x: extent.maxX, y: extent.maxY }, { cameraState: probe });
    const spanX = Math.abs(cornerB.x - cornerA.x);
    const spanY = Math.abs(cornerB.y - cornerA.y);

    const effectiveX = spanX > 0 ? spanX : Math.max(extent.maxX - extent.minX, 1e-6);
    const effectiveY = spanY > 0 ? spanY : Math.max(extent.maxY - extent.minY, 1e-6);

    const freeHeight = Math.max(1, height - insets.top - insets.bottom);
    const ratio = Math.max(
      effectiveX / (width * occupancy.x),
      effectiveY / (freeHeight * occupancy.y),
      1e-6
    );

    const centre =
      this.graphPointToFramed(sigma, (extent.minX + extent.maxX) / 2, (extent.minY + extent.maxY) / 2) ??
      { x: 0.5, y: 0.5 };
    const dy = (insets.top - insets.bottom) / 2;
    if (dy === 0) return { x: centre.x, y: centre.y, ratio };
    const framed = sigma.viewportToFramedGraph(
      { x: width / 2, y: height / 2 - dy },
      { cameraState: { x: centre.x, y: centre.y, angle: 0, ratio } }
    );
    if (!Number.isFinite(framed.x) || !Number.isFinite(framed.y)) return { x: centre.x, y: centre.y, ratio };
    return { x: framed.x, y: framed.y, ratio };
  }

  /** Frame the whole graph. */
  fitGraph(
    sigma: Sigma | null,
    graph: Graph | null,
    containerEl?: HTMLElement,
    overlays?: OverlayElements
  ): void {
    const state = this.fitCameraState(sigma, graph, this.fitOccupancy(), this.overlayInsets(containerEl, overlays));
    if (!state || !sigma) return;
    sigma.getCamera().animate(state, { duration: 350 });
  }

  /**
   * The camera state for a focused topic, derived from the graph's CURRENT
   * extent. At load that is the settled layout, so the zoom is the intended
   * fraction of a real fit rather than the Sigma default camera's ratio.
   *
   * On a phone the visible map is the band between the legend and the HUD
   * (whose top edge is the real selection card, measured), not the whole
   * canvas: centring on the middle of the stage put the focused topic under the
   * controls rail, just above the card. The node is centred in that band
   * instead. A wide screen keeps the stage centre.
   */
  focusedCameraState(
    sigma: Sigma | null,
    graph: Graph | null,
    selectedId: string | null,
    occupancy: { x: number; y: number } = this.fitOccupancy(),
    insets: { top: number; bottom: number } = { top: 0, bottom: 0 },
    phone = isPhoneViewport()
  ): { x: number; y: number; ratio: number } | null {
    if (!selectedId || !graph || !sigma || !graph.hasNode(selectedId)) return null;

    const attributes = graph.getNodeAttributes(selectedId);
    const framed = this.graphPointToFramed(
      sigma,
      Number(attributes['x']) || 0,
      Number(attributes['y']) || 0
    );
    if (!framed) return null;

    const fit = this.fitCameraState(sigma, graph, occupancy, insets);
    const ratio = fit
      ? Math.max(fit.ratio * 0.6, 0.3)
      : sigma.getCamera().ratio;
    if (!phone) return { x: framed.x, y: framed.y, ratio };

    const { width, height } = sigma.getDimensions();
    if (!width || !height) return { x: framed.x, y: framed.y, ratio };
    const dy = (insets.top - insets.bottom) / 2;
    if (dy === 0) return { x: framed.x, y: framed.y, ratio };
    // Ask Sigma for the framed coordinate that sits at the band's centre under
    // a camera already centred on the node; using that as the camera's centre
    // moves the node there without hand-rolled maths.
    const centred = sigma.viewportToFramedGraph(
      { x: width / 2, y: height / 2 - dy },
      { cameraState: { x: framed.x, y: framed.y, angle: 0, ratio } }
    );
    if (!Number.isFinite(centred.x) || !Number.isFinite(centred.y)) {
      return { x: framed.x, y: framed.y, ratio };
    }
    return { x: centred.x, y: centred.y, ratio };
  }

  /**
   * Frame a focused topic at load. Unlike the toolbar's "Center on selection",
   * this does not clamp the target to the camera's current ratio: on mount that
   * ratio is Sigma's default 1, which is what left `?focus=` over-zoomed.
   */
  frameFocused(
    sigma: Sigma | null,
    graph: Graph | null,
    selectedId: string | null,
    occupancy: { x: number; y: number } = this.fitOccupancy(),
    insets: { top: number; bottom: number } = { top: 0, bottom: 0 }
  ): void {
    const state = this.focusedCameraState(sigma, graph, selectedId, occupancy, insets);
    if (!state || !sigma) return;
    this.lastFrameTarget = state;
    sigma.getCamera().animate(state, { duration: 350 });
  }

  /**
   * Bring the selected node to the centre of the stage (or, on a phone, of the
   * band above the selection card and the controls rail).
   */
  centerSelected(
    sigma: Sigma | null,
    graph: Graph | null,
    selectedId: string | null,
    insets: { top: number; bottom: number } = { top: 0, bottom: 0 }
  ): void {
    const target = this.focusedCameraState(sigma, graph, selectedId, this.fitOccupancy(), insets);
    if (!target || !sigma) return;
    const camera = sigma.getCamera();
    camera.animate({ ...target, ratio: Math.min(camera.ratio, target.ratio) }, { duration: 350 });
  }

  /** Keep the selected node out from under the selection card and controls rail. */
  revealSelected(options: RevealSelectedOptions): void {
    if (this.revealFrameId !== null) {
      window.cancelAnimationFrame(this.revealFrameId);
    }
    this.revealFrameId = window.requestAnimationFrame(() => {
      this.revealFrameId = null;
      if (this.disposed) return;
      const sigma = options.getSigma();
      const graph = options.getGraph();
      const id = options.getSelectedId();
      const hud = options.getHudEl()?.getBoundingClientRect();
      const card = options.getHudEl()?.querySelector('.map-card')?.getBoundingClientRect();
      const stage = options.getContainerEl()?.getBoundingClientRect();
      if (!sigma || !id || !hud || !card || !stage || !graph?.hasNode(id) || !card.height || !hud.height) {
        return;
      }
      const attrs = graph.getNodeAttributes(id);
      const point = sigma.graphToViewport({ x: Number(attrs['x']) || 0, y: Number(attrs['y']) || 0 });
      const margin = 32;
      const left = card.left - stage.left - margin;
      const right = card.right - stage.left + margin;
      // The HUD's top edge is the top of the rail on a phone and of the card on
      // a wide screen; either way it is measured, not a fixed inset.
      const limit = hud.top - stage.top - margin;
      if (point.x < left || point.x > right || point.y <= limit) return;
      const camera = sigma.getCamera();
      const state = camera.getState();
      const shift = sigma.viewportToFramedGraph({ x: point.x, y: point.y }, { cameraState: state });
      const target = sigma.viewportToFramedGraph({ x: point.x, y: limit }, { cameraState: state });
      camera.animate(
        { x: state.x + (shift.x - target.x), y: state.y + (shift.y - target.y) },
        { duration: 300 }
      );
    });
  }

  zoomIn(sigma: Sigma | null): void {
    const camera = sigma?.getCamera();
    if (camera) {
      void camera.zoomIn({ duration: 200 });
    }
  }

  zoomOut(sigma: Sigma | null): void {
    const camera = sigma?.getCamera();
    if (camera) {
      void camera.zoomOut({ duration: 200 });
    }
  }

  /** Cancel per-build animation frames and ResizeObserver. */
  disposeRebuildObservers(): void {
    if (this.initialFrameId !== null) {
      window.cancelAnimationFrame(this.initialFrameId);
      this.initialFrameId = null;
    }
    if (this.settleFrameId !== null) {
      window.cancelAnimationFrame(this.settleFrameId);
      this.settleFrameId = null;
    }
    if (this.revealFrameId !== null) {
      window.cancelAnimationFrame(this.revealFrameId);
      this.revealFrameId = null;
    }
    this.detachUserCameraListeners?.();
    this.detachUserCameraListeners = null;
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
  }

  /**
   * Note reader interactions that move or may move the camera. A press alone
   * counts: it is the start of a pan or a drag, and the settle re-frame must
   * not fight a gesture already under way.
   */
  private attachUserCameraListeners(container: HTMLElement): void {
    const markMoved = (): void => {
      this.userMovedCamera = true;
    };
    container.addEventListener('wheel', markMoved, { passive: true });
    container.addEventListener('pointerdown', markMoved, { passive: true });
    container.addEventListener('touchmove', markMoved, { passive: true });
    container.addEventListener('keydown', markMoved);
    this.detachUserCameraListeners = () => {
      container.removeEventListener('wheel', markMoved);
      container.removeEventListener('pointerdown', markMoved);
      container.removeEventListener('touchmove', markMoved);
      container.removeEventListener('keydown', markMoved);
    };
  }

  /** Tear down all listeners, frames, and pending refresh timers on destroy. */
  dispose(): void {
    this.disposed = true;
    this.disposeRebuildObservers();
    for (const id of this.pendingTimeouts) {
      window.clearTimeout(id);
    }
    this.pendingTimeouts.clear();
    if (this.fullscreenListenerAttached) {
      document.removeEventListener('fullscreenchange', this.handleFullscreenChange);
      this.fullscreenListenerAttached = false;
    }
    this.stageEl = null;
    this.onFullscreenChangeCallback = null;
    this.onRefreshCallback = null;
  }

  private scheduleRefresh(delayMs: number): void {
    const id = window.setTimeout(() => {
      this.pendingTimeouts.delete(id);
      if (!this.disposed) {
        this.onRefreshCallback?.();
      }
    }, delayMs);
    this.pendingTimeouts.add(id);
  }
}

function sameFrame(
  a: { x: number; y: number; ratio: number },
  b: { x: number; y: number; ratio: number }
): boolean {
  return (
    Math.abs(a.x - b.x) < 1e-4 &&
    Math.abs(a.y - b.y) < 1e-4 &&
    Math.abs(a.ratio - b.ratio) < 1e-4
  );
}
