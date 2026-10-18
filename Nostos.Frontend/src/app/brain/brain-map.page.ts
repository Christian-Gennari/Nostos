import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, map, of, startWith, switchMap } from 'rxjs';

import { NotesService } from '../core/services/notes.service';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { rankTopicChoices, thoughtForDisplay, topicChoices } from '../ui/note-item/note-item.helpers';
import { noteWikilinksToText } from '../ui/pipes/note-format.pipe';
import { TopicMapComponent, type MapCamera } from '../second-brain/topic-map/topic-map.component';
import { BrainStateService } from './brain-state.service';
import { plural } from './brain.helpers';

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
  imports: [ButtonComponent, InputDirective, NostosIconComponent, TopicMapComponent],
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
   * The view to open at, read once: Back to the same focus restores the camera
   * left behind; a new focus (Show on map) centres on it instead.
   */
  readonly startCamera: MapCamera | null = (() => {
    const kept = this.state.mapView();
    return kept && kept.focus === this.route.snapshot.queryParamMap.get('focus') ? kept.camera : null;
  })();

  readonly topics = this.state.topics;
  readonly focusedTopic = computed(() => {
    const id = this.focus();
    return id ? this.topics().find((topic) => topic.id === id) ?? null : null;
  });
  readonly countText = computed(() => {
    const topics = this.topics().filter((topic) => topic.usageCount > 0).length;
    return plural(topics, 'topic');
  });

  readonly finder = signal('');
  readonly finderActive = signal(0);
  private readonly choices = computed(() => topicChoices(this.topics().filter((topic) => topic.usageCount > 0)));
  readonly finds = computed(() => {
    const typed = this.finder().trim();
    return typed ? rankTopicChoices(this.choices(), typed, [], FINDER_LIMIT) : [];
  });

  /** A line from the selected topic's newest note, so the card says what the topic holds. */
  readonly sample = toSignal(
    toObservable(this.focus).pipe(
      switchMap((topicId) => !topicId
        ? of(null)
        : this.notes.browse({ topicId, order: 'newest', limit: 1, offset: 0 }).pipe(
          map((page) => {
            const note = page.items[0];
            if (!note) return null;
            const quote = note.selectedText?.trim();
            return quote ? { text: quote, quote: true } : { text: noteWikilinksToText(thoughtForDisplay(note.content)), quote: false };
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

  select(topicId: string): void {
    this.setFocus(topicId);
  }

  cleared(): void {
    this.setFocus(null);
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
      this.finderActive.set((this.finderActive() + step + finds.length) % finds.length);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      this.find(finds[this.finderActive()]?.id ?? finds[0].id);
    }
  }

  /** Selects a found topic and brings it to the middle of the map. */
  find(topicId: string): void {
    this.finder.set('');
    this.map()?.selectRelated(topicId);
  }

  openDrawer(): void {
    this.state.drawerOpen.set(true);
  }

  /** The selection lives in the URL (replacing, so Back leaves the map rather than stepping through clicks). */
  private setFocus(topicId: string | null): void {
    if (topicId === this.focus()) return;
    this.state.mapView.update((view) => view && { ...view, focus: topicId });
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { focus: topicId },
      replaceUrl: true,
    });
  }
}
