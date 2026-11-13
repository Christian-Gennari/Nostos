import { PdfReadAloudBridge } from './pdf-read-aloud-bridge';
import { PdfReaderTimers } from './pdf-reader-timers';

describe('PdfReadAloudBridge', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function setup() {
    const host = document.createElement('div');
    const container = document.createElement('div');
    container.id = 'viewerContainer';
    Object.defineProperty(container, 'getBoundingClientRect', {
      value: () => ({ top: 0, bottom: 500, left: 0, right: 400, width: 400, height: 500 }),
    });
    const page = document.createElement('div');
    page.className = 'page';
    page.setAttribute('data-page-number', '2');
    const layer = document.createElement('div');
    layer.className = 'textLayer';
    page.appendChild(layer);
    container.appendChild(page);
    host.appendChild(container);

    const timers = new PdfReaderTimers();
    const goToPage = vi.fn();
    const highlightService = {
      paintReadAloudMark: vi.fn((textLayer: HTMLElement, rects: unknown[]) => {
        textLayer.innerHTML = '';
        if (rects.length > 0) {
          const box = document.createElement('div');
          box.className = 'read-aloud-mark-box';
          box.getBoundingClientRect = () =>
            ({ top: 700, bottom: 720, left: 10, right: 200, width: 190, height: 20, x: 10, y: 700, toJSON() {} }) as DOMRect;
          box.scrollIntoView = vi.fn();
          textLayer.appendChild(box);
        }
      }),
    };

    const bridge = new PdfReadAloudBridge({
      language: () => 'en',
      pageCount: () => 5,
      currentPage: () => 1,
      getPdfDoc: () => null,
      goToPage,
      hostElement: () => host,
      highlightService: highlightService as any,
      timers,
    });

    return { host, layer, timers, goToPage, highlightService, bridge };
  }

  it('tracks move suppression timers and cancels them cleanly on reset()', () => {
    const { timers, goToPage, bridge } = setup();

    (bridge as any).goToPage(2);
    expect(goToPage).toHaveBeenCalledWith(2);
    expect(bridge.isMoveInFlight()).toBe(true);
    expect(timers.hasPending()).toBe(true);

    bridge.reset();
    timers.cancelAll();

    expect(bridge.isMoveInFlight()).toBe(false);
    expect(timers.hasPending()).toBe(false);
  });

  it('scopes mark painting and scroll-into-view to the component host', () => {
    const { host, layer, bridge, highlightService } = setup();

    const decoyPage = document.createElement('div');
    decoyPage.className = 'page';
    decoyPage.setAttribute('data-page-number', '2');
    const decoyLayer = document.createElement('div');
    decoyLayer.className = 'textLayer';
    decoyPage.appendChild(decoyLayer);
    document.body.appendChild(decoyPage);
    document.body.appendChild(host);

    try {
      (bridge as any).showMark(2, [{ left: 0.1, top: 0.2, width: 0.4, height: 0.05 }]);

      expect(highlightService.paintReadAloudMark).toHaveBeenCalledWith(layer, [
        { left: 0.1, top: 0.2, width: 0.4, height: 0.05 },
      ]);
      expect(highlightService.paintReadAloudMark).not.toHaveBeenCalledWith(
        decoyLayer,
        expect.any(Array),
      );
      const box = layer.querySelector('.read-aloud-mark-box') as HTMLElement & {
        scrollIntoView: ReturnType<typeof vi.fn>;
      };
      expect(box.scrollIntoView).toHaveBeenCalledWith({ block: 'center', inline: 'nearest' });
      expect(bridge.isMoveInFlight()).toBe(true);
    } finally {
      decoyPage.remove();
      host.remove();
    }
  });
});
