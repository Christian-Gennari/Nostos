import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router, type ParamMap } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';

import { NotesService } from '../core/services/notes.service';
import { TopicsService, type TopicDto, type TopicGraphDto } from '../core/services/topics.service';
import { BrainMapPage } from './brain-map.page';
import { BrainStateService, type BrainMapView } from './brain-state.service';
import { MAX_MAP_TOPICS } from './topic-map/topic-map.helpers';

vi.mock('sigma', () => {
  const camera = {
    x: 0.5,
    y: 0.5,
    angle: 0,
    ratio: 1,
    enabled: true,
    zoomIn: vi.fn(() => Promise.resolve()),
    zoomOut: vi.fn(() => Promise.resolve()),
    animate: vi.fn(() => Promise.resolve()),
    setState: vi.fn(),
    getState: vi.fn(() => ({ x: camera.x, y: camera.y, angle: camera.angle, ratio: camera.ratio })),
  };
  const noopCaptor = { on: vi.fn(() => noopCaptor) };
  class MockSigma {
    constructor() {}
    on() {
      return this;
    }
    setSetting() {
      return this;
    }
    getSetting() {
      return undefined;
    }
    createCanvas() {
      return document.createElement('canvas');
    }
    scaleSize(size: number) {
      return size;
    }
    refresh() {}
    kill() {}
    getCamera() {
      return camera;
    }
    getMouseCaptor() {
      return noopCaptor;
    }
    getTouchCaptor() {
      return noopCaptor;
    }
    getDimensions() {
      return { width: 800, height: 600 };
    }
    viewportToGraph(c: { x: number; y: number }) {
      return c;
    }
    graphToViewport(p: { x: number; y: number }) {
      return { x: 400 + p.x, y: 300 - p.y };
    }
    viewportToFramedGraph(p: { x: number; y: number }) {
      return { x: (p.x - 400) / 800, y: (300 - p.y) / 600 };
    }
  }
  return {
    default: MockSigma,
    DEFAULT_STYLES: { nodes: {}, edges: {} },
  };
});

describe('BrainMapPage', () => {
  let fixture: ComponentFixture<BrainMapPage>;
  let router: Router;
  let query: BehaviorSubject<ParamMap>;
  let mapView: ReturnType<typeof signal<BrainMapView | null>>;
  let topicsSignal: ReturnType<typeof signal<TopicDto[]>>;
  let graphData: TopicGraphDto;

  const baseTopics: TopicDto[] = [
    ...Array.from({ length: MAX_MAP_TOPICS }, (_, i) => ({
      id: `top-${i}`,
      name: `Top ${i}`,
      usageCount: 200 + i,
    })),
    { id: 'edu', name: 'Education', usageCount: 4 },
    { id: 'ped', name: 'Pedagogy', usageCount: 2 },
    { id: 'log', name: 'Logic', usageCount: 1 },
  ];

  const baseGraph: TopicGraphDto = {
    nodes: baseTopics.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount })),
    edges: [
      { sourceId: 'edu', targetId: 'ped', sharedNotes: 3 },
      { sourceId: 'ped', targetId: 'log', sharedNotes: 1 },
    ],
  };

  async function mountPage(
    initialParams: Record<string, string>,
    options: { back?: boolean; savedView?: BrainMapView | null } = {}
  ): Promise<void> {
    query = new BehaviorSubject<ParamMap>(convertToParamMap(initialParams));
    mapView = signal<BrainMapView | null>(options.savedView ?? null);
    topicsSignal = signal<TopicDto[]>(baseTopics);
    graphData = baseGraph;

    await TestBed.configureTestingModule({
      imports: [BrainMapPage],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: query.asObservable(),
            snapshot: { queryParamMap: query.value },
          },
        },
        {
          provide: NotesService,
          useValue: { browse: vi.fn(() => of({ items: [], totalCount: 0, offset: 0, limit: 1 })) },
        },
        {
          provide: TopicsService,
          useValue: { getGraph: vi.fn(() => of(graphData)) },
        },
        {
          provide: BrainStateService,
          useValue: {
            ensureLoaded: vi.fn(),
            topics: topicsSignal,
            topicsLoaded: signal(true),
            sidebarTopics: topicsSignal,
            drawerOpen: signal(false),
            mapView,
            topicById: (id: string | null) =>
              id ? topicsSignal().find((t) => t.id === id) ?? null : null,
          },
        },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    if (options.back) {
      vi.spyOn(router, 'currentNavigation').mockReturnValue({
        trigger: 'popstate',
      } as ReturnType<Router['currentNavigation']>);
    }

    fixture = TestBed.createComponent(BrainMapPage);
    fixture.detectChanges();
    fixture.detectChanges();
  }

  afterEach(() => {
    fixture?.nativeElement.remove();
    TestBed.resetTestingModule();
  });

  function el(): HTMLElement {
    return fixture.nativeElement;
  }

  it('drives local-map scope from URL params, exposes accessible slider labels, and finds local topics', async () => {
    await mountPage({ around: 'edu', depth: '2', min: '1' });

    const chip = el().querySelector('.map-controls .map-scope-chip');
    expect(chip?.textContent).toContain('Around Education');

    const depthSlider = el().querySelector('.map-controls input[min="1"][max="3"]') as HTMLInputElement;
    expect(depthSlider.value).toBe('2');
    expect(depthSlider.getAttribute('aria-label')).toBe('Steps from Education: 2');

    const strengthSlider = el().querySelector(
      '.map-controls input[aria-label^="Minimum shared notes:"]'
    ) as HTMLInputElement;
    expect(strengthSlider.value).toBe('1');
    expect(strengthSlider.max).toBe('3');
    expect(strengthSlider.getAttribute('aria-label')).toBe('Minimum shared notes: 1');

    // Local map draws Logic (usage 1) even though top-150 global cap omits it; finder must find it.
    fixture.componentInstance.onFinderInput('Logic');
    fixture.detectChanges();
    expect(fixture.componentInstance.finds().map((f) => f.id)).toContain('log');
  });

  it('restores depth, strength, and camera on Back (popstate)', async () => {
    const keptCamera = { x: 0.31, y: 0.64, ratio: 0.45, angle: 0 };
    await mountPage(
      { around: 'edu' },
      {
        back: true,
        savedView: {
          camera: keptCamera,
          focus: null,
          around: 'edu',
          depth: 3,
          min: 2,
        },
      }
    );

    expect(fixture.componentInstance.startCamera).toEqual(keptCamera);
    expect(fixture.componentInstance.depth()).toBe(3);
    expect(fixture.componentInstance.min()).toBe(2);
    expect(fixture.componentInstance.effectiveMin()).toBe(2);
  });

  it('clears around, depth, and min when the scope chip close button is clicked', async () => {
    await mountPage({ around: 'edu', depth: '2', min: '2' });
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const removeBtn = el().querySelector('.map-controls .topic-chip-remove') as HTMLButtonElement;
    expect(removeBtn).toBeTruthy();
    removeBtn.click();

    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { around: null, depth: null, min: null, focus: null },
        queryParamsHandling: 'merge',
      })
    );
  });

  it('updates depth and strength URL params with queryParamsHandling merge and replaceUrl', async () => {
    await mountPage({ around: 'edu', depth: '1', min: '1' });
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    fixture.componentInstance.onDepthInput('3');
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { depth: '3' },
        queryParamsHandling: 'merge',
        replaceUrl: true,
      })
    );

    fixture.componentInstance.onMinInput('2');
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { min: '2' },
        queryParamsHandling: 'merge',
        replaceUrl: true,
      })
    );
  });
});
