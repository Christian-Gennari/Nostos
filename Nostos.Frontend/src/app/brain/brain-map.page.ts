import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { QuoteBlockComponent } from '../ui/quote-block/quote-block.component';
import { TopicOptionComponent } from '../ui/topic-option/topic-option.component';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, map, of, startWith, switchMap } from 'rxjs';

import { NotesService } from '../core/services/notes.service';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { rankTopicChoices, thoughtForDisplay, topicChoices } from '../ui/note-item/note-item.helpers';
import { noteWikilinksToText } from '../ui/pipes/note-format.pipe';
import { TopicMapComponent, type MapCamera } from './topic-map/topic-map.component';
import { drawnTopics } from './topic-map/topic-map.helpers';
import { BrainStateService } from './brain-state.service';
import { plural } from './brain.helpers';
import { SearchFieldComponent } from '../ui/search-field/search-field.component';

const FINDER_LIMIT = 6;

/**
 * The map as a place: the topic graph at full size, a finder that centres a
 * topic, and a card whose Open topic goes to the topic's page. The camera and
 * the selected topic are kept, so Back from a topic returns to the same view;
 * `?focus=<id>` (Show on map) opens centred on a topic.
 */
@Component({
  selector: 'app-brain-map-page',
  standalone: true,
  imports: [QuoteBlockComponent, TopicOptionComponent, SearchFieldComponent, ButtonComponent, InputDirective, NostosIconComponent, TopicMapComponent],
  templateUrl: './brain-map.page.html',
  styleUrls: ['./brain-map.page.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainMapPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly notes = inject(NotesService);
  readonly state = inject(BrainStateService);

  private readonly map = viewChild(TopicMapComponent);

  readonly focus = toSignal(this.route.queryParamMap.pipe(map((params) => params.get('focus'))), {
    initialValue: this.route.snapshot.queryParamMap.get('focus'),
  });

  /**
   * The view to open at, read once: Back to the map restores the camera left
   * behind; any other arrival (Show on map, the sidebar) centres on the focus,
   * or frames the whole graph.
   */
  readonly startCamera: MapCamera | null = (() => {
    const kept = this.state.mapView();
    const back = this.router.currentNavigation()?.trigger === 'popstate';
    return back && kept && kept.focus === this.route.snapshot.queryParamMap.get('focus') ? kept.camera : null;
  })();

  readonly topics = this.state.topics;
  /** The selected topic's own words, which the card shows before a line from its notes. */
  readonly focusedWords = computed(() => this.state.topicById(this.focus())?.description?.trim() || null);
  readonly countText = computed(() => plural(this.state.sidebarTopics().length, 'topic'));

  readonly finder = signal('');
  readonly finderOpen = signal(false);
  readonly finderActive = signal(0);
  /** Only topics the map draws can be found on it. */
  private readonly choices = computed(() => topicChoices(drawnTopics(this.state.sidebarTopics())));
  readonly finds = computed(() => {
    const typed = this.finder().trim();
    return typed && this.finderOpen() ? rankTopicChoices(this.choices(), typed, [], FINDER_LIMIT) : [];
  });
  /** The highlighted find, kept inside the list as it shrinks. */
  readonly activeFind = computed(() => Math.min(this.finderActive(), Math.max(0, this.finds().length - 1)));

  /** A line from the selected topic's newest note, so the card says what the topic holds. */
  readonly sample = toSignal(
    toObservable(this.focus).pipe(
      // A topic with its own words shows those instead, so its notes are not asked for.
      switchMap((topicId) => !topicId || this.state.topicById(topicId)?.description?.trim()
        ? of(null)
        : this.notes.browse({ topicId, order: 'newest', limit: 1, offset: 0 }).pipe(
          map((page) => {
            const note = page.items[0];
            if (!note) return null;
            const quote = note.selectedText?.trim();
            if (quote) return { text: quote, quote: true };
            const words = noteWikilinksToText(thoughtForDisplay(note.content)).trim();
            return words ? { text: words, quote: false } : null;
          }),
          catchError(() => of(null)),
          startWith(null),
        )),
    ),
    { initialValue: null },
  );

  constructor() {
    this.state.ensureLoaded();
  }

  keepCamera(camera: MapCamera): void {
    this.state.mapView.set({ camera, focus: this.focus() });
  }

  openTopic(topicId: string | null = this.focus()): void {
    if (topicId) void this.router.navigate(['/brain/topics', topicId]);
  }

  // --- The finder -----------------------------------------------------------

  onFinderInput(value: string): void {
    this.finder.set(value);
    this.finderOpen.set(true);
    this.finderActive.set(0);
  }

  onFinderKeydown(event: KeyboardEvent): void {
    const finds = this.finds();
    if (event.key === 'Escape') {
      if (this.finder()) {
        event.preventDefault();
        this.finder.set('');
      }
      return;
    }
    if (!finds.length) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      this.finderActive.set((this.activeFind() + step + finds.length) % finds.length);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      this.find(finds[this.activeFind()].id);
    }
  }

  /** Selects a found topic and brings it to the middle of the map. */
  find(topicId: string): void {
    this.finder.set('');
    this.map()?.focusTopic(topicId);
  }

  openDrawer(): void {
    this.state.drawerOpen.set(true);
  }

  /** The selection lives in the URL (replacing, so Back leaves the map rather than stepping through clicks). */
  setFocus(topicId: string | null): void {
    if (topicId === this.focus()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { focus: topicId },
      replaceUrl: true,
    });
  }
}
