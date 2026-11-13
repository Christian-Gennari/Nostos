import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ToastService } from '../../core/services/toast.service';
import { CitationService } from './citation.service';
import { citationStyle } from './citation-styles';
import { CitationRefreshCoordinator } from './citation-refresh.coordinator';

@Component({
  selector: 'app-citation-refresh-harness',
  standalone: true,
  template: '',
  providers: [CitationRefreshCoordinator],
})
class CitationRefreshHarnessComponent {
  constructor(readonly coordinator: CitationRefreshCoordinator) {}
}

describe('CitationRefreshCoordinator', () => {
  let fixture: ComponentFixture<CitationRefreshHarnessComponent>;
  let harness: CitationRefreshHarnessComponent;
  let citationService: {
    renderer: ReturnType<typeof vi.fn>;
    book: ReturnType<typeof vi.fn>;
    insertion: ReturnType<typeof vi.fn>;
    bibliographyEntries: ReturnType<typeof vi.fn>;
  };
  let toast: { error: ReturnType<typeof vi.fn>; show: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    citationService = {
      renderer: vi.fn(),
      book: vi.fn(),
      insertion: vi.fn(),
      bibliographyEntries: vi.fn(),
    };
    toast = { error: vi.fn(), show: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [CitationRefreshHarnessComponent],
      providers: [
        { provide: CitationService, useValue: citationService },
        { provide: ToastService, useValue: toast },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CitationRefreshHarnessComponent);
    harness = fixture.componentInstance;
  });

  afterEach(() => {
    vi.useRealTimers();
    fixture?.destroy();
  });

  it('clears scheduled citationRefreshTimer on destroy without executing callback', () => {
    vi.useFakeTimers();
    const callback = vi.fn();

    harness.coordinator.scheduleRefresh(callback, 250);
    fixture.destroy();
    vi.advanceTimersByTime(500);

    expect(callback).not.toHaveBeenCalled();
  });

  it('drops stale refresh runs when a newer refresh or document switch occurs', async () => {
    let resolveFirst!: (val: unknown) => void;
    const firstRenderer = new Promise((r) => {
      resolveFirst = r;
    });
    const secondRenderer = {
      placement: 'in-text' as const,
      bibliographyHeading: 'Bibliography',
      bibliographyBooks: [],
      inText: () => null,
      note: () => null,
      bibliography: () => null,
    };

    citationService.renderer
      .mockReturnValueOnce(firstRenderer)
      .mockResolvedValueOnce(secondRenderer);

    const refreshCitations = vi.fn(() => true);
    const editor = {
      citedBooks: () => ['book-1'],
      refreshCitations,
    };
    const style = citationStyle('apa');

    const firstRun = harness.coordinator.refreshCitations({
      editor,
      writingId: 'w-1',
      isCurrent: () => true,
      style,
      keptSources: [],
    });
    const secondRun = harness.coordinator.refreshCitations({
      editor,
      writingId: 'w-1',
      isCurrent: () => true,
      style,
      keptSources: [],
    });

    await expect(secondRun).resolves.toBe(true);
    resolveFirst({
      placement: 'note',
      bibliographyHeading: 'Notes',
      bibliographyBooks: [],
      inText: () => null,
      note: () => null,
      bibliography: () => null,
    });
    await expect(firstRun).resolves.toBe(false);
    expect(refreshCitations).toHaveBeenCalledTimes(1);
    expect(refreshCitations).toHaveBeenCalledWith(secondRenderer);
  });
});
