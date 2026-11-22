import { signal } from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router, type ParamMap } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';

import { NotesService } from '../core/services/notes.service';
import { TopicsService, type TopicDto, type TopicGraphDto, type TopicKindDto } from '../core/services/topics.service';
import { type MapColorMode } from './brain.helpers';
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
  let topicKindsSignal: ReturnType<typeof signal<TopicKindDto[]>>;
  let kindsLoadedSignal: ReturnType<typeof signal<boolean>>;
  let mapColorModeSignal: ReturnType<typeof signal<MapColorMode>>;
  let setMapColorModeSpy: ReturnType<typeof vi.fn>;
  let graphData: TopicGraphDto;

  const baseTopics: TopicDto[] = [
    ...Array.from({ length: MAX_MAP_TOPICS }, (_, i) => ({
      id: `top-${i}`,
      name: `Top ${i}`,
      usageCount: 200 + i,
      kindId: i % 2 === 0 ? 'k1' : 'k2',
    })),
    { id: 'edu', name: 'Education', usageCount: 4, kindId: 'k1' },
    { id: 'ped', name: 'Pedagogy', usageCount: 2, kindId: 'k2' },
    { id: 'log', name: 'Logic', usageCount: 1, kindId: null },
  ];

  const baseKinds: TopicKindDto[] = [
    { id: 'k1', name: 'Concepts', colorKey: 'kind-1', sortOrder: 1, createdAt: '', topicCount: 151 },
    { id: 'k2', name: 'Methods', colorKey: 'kind-2', sortOrder: 2, createdAt: '', topicCount: 1 },
  ];

  const baseGraph: TopicGraphDto = {
    nodes: baseTopics.map((t) => ({ id: t.id, name: t.name, usageCount: t.usageCount, kindId: t.kindId ?? null })),
    edges: [
      { sourceId: 'edu', targetId: 'ped', sharedNotes: 3 },
      { sourceId: 'ped', targetId: 'log', sharedNotes: 1 },
    ],
  };

  async function mountPage(
    initialParams: Record<string, string>,
    options: {
      back?: boolean;
      savedView?: BrainMapView | null;
      colorMode?: MapColorMode;
      kindsLoaded?: boolean;
    } = {}
  ): Promise<void> {
    query = new BehaviorSubject<ParamMap>(convertToParamMap(initialParams));
    mapView = signal<BrainMapView | null>(options.savedView ?? null);
    topicsSignal = signal<TopicDto[]>(baseTopics);
    topicKindsSignal = signal<TopicKindDto[]>(baseKinds);
    kindsLoadedSignal = signal<boolean>(options.kindsLoaded ?? true);
    mapColorModeSignal = signal<MapColorMode>(options.colorMode ?? 'kind');
    setMapColorModeSpy = vi.fn((mode: MapColorMode) => mapColorModeSignal.set(mode));
    graphData = baseGraph;

    await TestBed.configureTestingModule({
      imports: [BrainMapPage],
      providers: [
        provideRouter([]),
        {
          provide: BreakpointObserver,
          useValue: {
            isMatched: () => false,
            observe: () => of({ matches: false }),
          },
        },
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
          useValue: {
            getGraph: vi.fn(() => of(graphData)),
            listKinds: vi.fn(() => of(baseKinds)),
          },
        },
        {
          provide: BrainStateService,
          useValue: {
            ensureLoaded: vi.fn(),
            ensureKindsLoaded: vi.fn(),
            topics: topicsSignal,
            topicKinds: topicKindsSignal,
            kindsLoaded: kindsLoadedSignal,
            topicsLoaded: signal(true),
            sidebarTopics: topicsSignal,
            drawerOpen: signal(false),
            mapView,
            mapColorMode: mapColorModeSignal,
            setMapColorMode: setMapColorModeSpy,
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
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
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
      { around: 'edu', kinds: 'k1,k2' },
      {
        back: true,
        savedView: {
          camera: keptCamera,
          focus: null,
          around: 'edu',
          depth: 3,
          min: 2,
          kinds: 'k1,k2',
        },
      }
    );

    expect(fixture.componentInstance.startCamera).toEqual(keptCamera);
    expect(fixture.componentInstance.depth()).toBe(3);
    expect(fixture.componentInstance.min()).toBe(2);
    expect(fixture.componentInstance.effectiveMin()).toBe(2);
    expect(fixture.componentInstance.kinds()).toBe('k1,k2');
    expect([...(fixture.componentInstance.visibleKindKeys() ?? [])]).toEqual(['k1', 'k2']);
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

  it('switches colour mode via the segmented control and toggles ?kinds= in URL without replaceUrl', async () => {
    await mountPage({}, { colorMode: 'kind' });
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const modeButtons = Array.from(
      el().querySelectorAll('.map-controls nostos-segmented button[role="radio"]')
    ) as HTMLButtonElement[];
    expect(modeButtons.map((b) => b.textContent?.trim())).toEqual(['Kind', 'Cluster', 'None']);
    modeButtons[1].click();
    expect(setMapColorModeSpy).toHaveBeenCalledWith('cluster');

    // Toggle the first legend pill (Concepts) while in kind mode: the visible
    // set is written without it, and because the click is a real navigation
    // (not replaceUrl) Back returns to the unfiltered URL.
    const kindButtons = Array.from(el().querySelectorAll('.map-legend-kind')) as HTMLButtonElement[];
    expect(kindButtons.length).toBe(3);
    kindButtons[0].click();

    const call = navigate.mock.calls.at(-1)!;
    expect(call[1]).toEqual(
      expect.objectContaining({
        queryParams: { kinds: 'k2,none' },
        queryParamsHandling: 'merge',
      })
    );
    expect((call[1] as { replaceUrl?: boolean }).replaceUrl).toBeUndefined();

    // Clearing the filter clears ?kinds=
    query.next(convertToParamMap({ kinds: 'k2' }));
    fixture.detectChanges();
    fixture.componentInstance.onClearKindFilter();
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: { kinds: null },
        queryParamsHandling: 'merge',
      })
    );
  });

  it('falls back to Cluster while the kind list is unavailable', async () => {
    await mountPage({}, { colorMode: 'kind', kindsLoaded: false });

    expect(fixture.componentInstance.effectiveColorMode()).toBe('cluster');
    expect(el().querySelector('.map-legend-kinds')).toBeNull();
    expect(el().textContent).not.toContain('No kind (');

    // The Colour control has to say the mode the map actually paints: it read
    // the reader's stored choice and said "Kind" over a Cluster map.
    const modes = Array.from(
      el().querySelectorAll('.map-controls nostos-segmented button[role="radio"]')
    ) as HTMLButtonElement[];
    expect(modes.map((button) => button.textContent?.trim())).toEqual(['Kind', 'Cluster', 'None']);
    expect(modes.map((button) => button.getAttribute('aria-checked'))).toEqual(['false', 'true', 'false']);
  });

  it('puts the same kind legend in the Controls sheet, with the map a11y contract', async () => {
    await mountPage({}, { colorMode: 'kind' });
    fixture.componentInstance.controlsOpen.set(true);
    fixture.detectChanges();
    await Promise.resolve();

    const sheet = document.querySelector('.map-controls-sheet') as HTMLElement;
    expect(sheet).toBeTruthy();
    const legend = sheet.querySelector('app-map-kind-legend') as HTMLElement;
    expect(legend).toBeTruthy();

    const chips = Array.from(legend.querySelectorAll('.map-legend-kind')) as HTMLButtonElement[];
    expect(chips.map((chip) => chip.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'Concepts (75)',
      'Methods (75)',
      'No kind (0)',
    ]);
    expect(chips.every((chip) => chip.getAttribute('aria-pressed') === 'true')).toBe(true);
    expect(chips[0].getAttribute('aria-label')).toContain('drawn on the map');

    const sortLink = legend.querySelector('.map-legend-sort') as HTMLAnchorElement;
    expect(sortLink.textContent?.trim()).toBe('Sort them');

    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    chips[0].click();
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({ queryParams: { kinds: 'k2,none' }, queryParamsHandling: 'merge' })
    );

    // The escape hatch is in the sheet too, next to the rows it relates to.
    fixture.componentInstance.scope.set({
      totalInScope: 20,
      shownCount: 20,
      maxShared: 1,
      effectiveMin: 1,
      capped: false,
      hiddenByKindCount: 7,
    });
    fixture.detectChanges();
    expect(legend.querySelector('.map-legend-clear')?.textContent?.trim()).toBe('Show all kinds');
  });

  it('states one in-scope total and the hidden-by-kind count separately', async () => {
    await mountPage({ kinds: 'k1' });

    fixture.componentInstance.scope.set({
      totalInScope: 150,
      shownCount: 150,
      maxShared: 1,
      effectiveMin: 1,
      capped: true,
      hiddenByKindCount: 7,
    });
    fixture.detectChanges();

    expect(el().querySelector('.map-controls-count')?.textContent?.trim()).toBe(
      'Showing 150 of 150 topics (7 hidden by kind)'
    );
  });

  it('opens the kind picker from a map-card pill, anchored to that pill', async () => {
    await mountPage({});
    const page = fixture.componentInstance;
    const pill = document.createElement('button');
    el().appendChild(pill);

    page.openKindPicker({ topicId: 'edu', kindId: 'k1', origin: pill });
    expect(page.kindPickerTopic()?.id).toBe('edu');
    expect(page.kindPickerOrigin()).toBe(pill);

    // A second press on the same topic's pill is the reader closing the panel.
    page.openKindPicker({ topicId: 'edu', kindId: 'k1', origin: pill });
    expect(page.kindPickerTopic()).toBeNull();
    expect(page.kindPickerOrigin()).toBeNull();
  });

  it('never hangs the kind picker from the page body', async () => {
    await mountPage({});
    const page = fixture.componentInstance;

    // Safari's document.activeElement is <body> after a touch.
    page.openKindPicker({ topicId: 'edu', kindId: 'k1', origin: document.body });

    expect(page.kindPickerTopic()?.id).toBe('edu');
    expect(page.kindPickerOrigin()).not.toBe(document.body);
  });

  it('ignores a picker request for a topic the page no longer holds', async () => {
    await mountPage({});
    const page = fixture.componentInstance;

    page.openKindPicker({ topicId: 'gone', kindId: null });

    expect(page.kindPickerTopic()).toBeNull();
  });
});

