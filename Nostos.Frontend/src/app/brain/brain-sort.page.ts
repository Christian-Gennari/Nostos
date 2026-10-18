import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { isPassage, type NoteOrder, type NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { AssistantService } from '../ui/assistant/assistant.service';
import { ButtonComponent } from '../ui/button/button.component';
import { ChipComponent } from '../ui/chip/chip.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { NoteFormatPipe } from '../ui/pipes/note-format.pipe';
import { ProgressComponent } from '../ui/progress/progress.component';
import { noteSourceLink } from '../core/note-source';
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import {
  hasTopic,
  inlineTopicHref,
  newTopicName,
  noteLocationLabel,
  rankTopicChoices,
  thoughtForDisplay,
  topicChoices,
} from '../ui/note-item/note-item.helpers';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainNoteDialogsComponent } from './brain-note-dialogs.component';
import { BrainSortPositionService, sortQueueKey } from './brain-sort-position.service';
import { BrainStateService } from './brain-state.service';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';
import { isOrder, stillBelongs } from './brain.helpers';

const PAGE_SIZE = 25;
const RECENT_SHOWN = 6;

interface Choice {
  kind: 'topic' | 'create';
  name: string;
  count?: number;
}

/**
 * Sort one by one: the Unsorted notes, a whole screen for one at a time and
 * one question, which topics does it belong to? Type and Enter adds a topic;
 * Enter on an empty field saves and moves on. The queue is Unsorted's, in its
 * book and order; a saved note leaves it, a skipped one stays. Where the
 * reader was is remembered.
 */
@Component({
  selector: 'app-brain-sort-page',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    ...NOSTOS_MENU,
    ButtonComponent,
    ChipComponent,
    IconButtonComponent,
    NostosIconComponent,
    LoadingIndicatorComponent,
    NoteFormatPipe,
    ProgressComponent,
    BrainNoteDialogsComponent,
  ],
  providers: [BrainTopicSuggestionsService, BrainNoteActionsService],
  templateUrl: './brain-sort.page.html',
  styleUrls: ['./brain-sort.page.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown.escape)': 'onEscape($event)' },
})
export class BrainSortPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly notes = inject(NotesService);
  private readonly topicsService = inject(TopicsService);
  private readonly toast = inject(ToastService);
  private readonly position = inject(BrainSortPositionService);
  private readonly assistant = inject(AssistantService);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  readonly state = inject(BrainStateService);
  readonly suggestions = inject(BrainTopicSuggestionsService);

  readonly bookId = this.route.snapshot.queryParamMap.get('book');
  /** Unsorted's search, so the queue is the notes that list showed. */
  readonly query = this.route.snapshot.queryParamMap.get('q')?.trim() || null;
  readonly order: NoteOrder = (() => {
    const value = this.route.snapshot.queryParamMap.get('order');
    return isOrder(value) && value !== 'book' ? value : 'oldest';
  })();
  private readonly queueKey = sortQueueKey(this.bookId, this.order, this.query);

  /** The notes fetched so far that are still unsorted, in queue order. */
  readonly queue = signal<NoteSearchHit[]>([]);
  /** How many are still unsorted, including those not fetched yet. */
  readonly remaining = signal(0);
  readonly index = signal(0);
  readonly sortedCount = signal(0);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly failed = signal(false);
  readonly saving = signal(false);

  readonly chips = signal<string[]>([]);
  readonly typed = signal('');
  readonly active = signal(0);
  /** Topics given this session, latest first: one tap for a run of related notes. */
  readonly recent = signal<string[]>([]);

  readonly actions = inject(BrainNoteActionsService);

  private readonly field = viewChild<ElementRef<HTMLInputElement>>('field');

  readonly note = computed(() => this.queue()[this.index()] ?? null);
  readonly done = computed(() => this.loaded() && !this.loading() && this.remaining() === 0);
  readonly positionLabel = computed(() => `${Math.min(this.index() + 1, this.remaining())} of ${this.remaining()}`);
  readonly progress = computed(() => {
    const handled = this.sortedCount() + this.index();
    const all = this.sortedCount() + this.remaining();
    return all ? Math.min(100, Math.round((handled / all) * 100)) : 100;
  });

  readonly quote = computed(() => this.note()?.selectedText?.trim() ?? '');
  readonly thought = computed(() => thoughtForDisplay(this.note()?.content));
  readonly where = computed(() => {
    const note = this.note();
    return note ? noteLocationLabel(note) : null;
  });
  readonly passage = computed(() => isPassage(this.note()));
  readonly sourceLabel = computed(() => {
    const note = this.note();
    return note ? noteSourceLink(note).label : 'Open book';
  });

  private readonly choicesSource = computed(() => topicChoices(this.state.topics()));

  /** What the typed text matches, then a new topic by that name. */
  readonly matches = computed<Choice[]>(() => {
    const typed = this.typed().trim();
    if (!typed) return [];
    const choices: Choice[] = rankTopicChoices(this.choicesSource(), typed, this.chips(), 6)
      .map((topic) => ({ kind: 'topic', name: topic.name, count: topic.usageCount }));
    const create = newTopicName(this.state.topics(), typed);
    if (create && !hasTopic(this.chips(), create)) choices.push({ kind: 'create', name: create });
    return choices;
  });

  /** Topics used this session, then the most used, without the note's chips. */
  readonly recentChoices = computed(() => {
    const chosen = this.chips();
    const names = [...this.recent()];
    for (const topic of this.state.sidebarTopics()) {
      if (names.length >= RECENT_SHOWN * 2) break;
      names.push(topic.name);
    }
    return names.filter((name, i) => !hasTopic(chosen, name) && !hasTopic(names.slice(0, i), name))
      .slice(0, RECENT_SHOWN);
  });

  readonly suggested = computed(() => {
    const note = this.note();
    return note ? this.suggestions.namesFor(note.id).filter((name) => !hasTopic(this.chips(), name)) : [];
  });
  readonly suggestionState = computed(() => {
    const note = this.note();
    return note ? this.suggestions.stateFor(note.id) : 'off';
  });
  readonly aiAvailable = computed(() => this.assistant.surfaceAvailable());

  constructor() {
    this.state.ensureLoaded();
    // The queue holds only unsorted notes: one that gained a topic leaves it.
    this.actions.attach({
      replace: (note) => { if (!stillBelongs(note, 'unsorted', null, null)) this.drop(note.id); },
      remove: (noteId) => this.drop(noteId),
    });
    this.position.read().pipe(takeUntilDestroyed(this.destroyRef)).subscribe((saved) => {
      this.loadPage(saved?.queue === this.queueKey ? saved.noteId : null);
    });
  }

  /** Fetches the next page; while resuming, keeps fetching (up to 8 pages) until the remembered note is found. */
  private loadPage(resumeId: string | null = null, pagesLeft = 8): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.failed.set(false);
    this.notes.browse({
      withoutTopics: true,
      query: this.query ?? undefined,
      bookId: this.bookId ?? undefined,
      order: this.order,
      limit: PAGE_SIZE,
      offset: this.queue().length,
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (page) => {
        const held = new Set(this.queue().map((note) => note.id));
        const fresh = page.items.filter((note) => !held.has(note.id));
        this.queue.update((queue) => [...queue, ...fresh]);
        // A page that brought nothing new (notes sorted elsewhere): the queue
        // ends at what is held, rather than waiting for a note that never comes.
        this.remaining.set(fresh.length ? page.totalCount : this.queue().length);
        this.loading.set(false);
        this.loaded.set(true);
        if (resumeId) {
          const at = this.queue().findIndex((note) => note.id === resumeId);
          if (at >= 0) this.index.set(at);
          else if (fresh.length && pagesLeft > 1 && this.queue().length < this.remaining()) {
            this.loadPage(resumeId, pagesLeft - 1);
            return;
          }
        }
        if (this.index() >= this.remaining()) this.index.set(Math.max(0, this.remaining() - 1));
        this.remember();
        this.focusField();
      },
      error: () => {
        this.loading.set(false);
        this.failed.set(true);
      },
    });
  }

  /** Make sure the note at the index is fetched, then remember it. */
  private settle(): void {
    if (this.index() >= this.queue().length && this.queue().length < this.remaining()) this.loadPage();
    this.remember();
    this.suggestions.clear();
    this.typed.set('');
    this.chips.set([]);
    this.active.set(0);
    this.focusField();
  }

  /** Keeps the note in focus for a reload; a note not fetched yet changes nothing. */
  private remember(): void {
    const note = this.note();
    if (note) this.position.write({ noteId: note.id, queue: this.queueKey });
    else if (this.done()) this.position.write(null);
  }

  // --- The field ----------------------------------------------------------

  onInput(value: string): void {
    this.typed.set(value);
    this.active.set(0);
  }

  onKeydown(event: KeyboardEvent): void {
    const empty = this.typed().length === 0;
    const matches = this.matches();
    switch (event.key) {
      case 'Enter':
        event.preventDefault();
        if (!empty) {
          const choice = matches[this.active()];
          if (choice) this.addChip(choice.name);
        } else if (this.chips().length) {
          this.save();
        } else {
          this.skip();
        }
        break;
      case 'ArrowDown':
        if (matches.length) {
          event.preventDefault();
          this.active.set((this.active() + 1) % matches.length);
        }
        break;
      case 'ArrowUp':
        if (matches.length) {
          event.preventDefault();
          this.active.set((this.active() - 1 + matches.length) % matches.length);
        }
        break;
      case 'ArrowRight':
        if (empty) {
          event.preventDefault();
          this.skip();
        }
        break;
      case 'ArrowLeft':
        if (empty) {
          event.preventDefault();
          this.previous();
        }
        break;
      case 'Backspace':
        if (empty && this.chips().length) {
          event.preventDefault();
          this.chips.update((chips) => chips.slice(0, -1));
        }
        break;
    }
  }

  addChip(name: string): void {
    const topic = name.trim();
    if (!topic || hasTopic(this.chips(), topic)) return;
    this.chips.update((chips) => [...chips, topic]);
    this.typed.set('');
    this.active.set(0);
    this.focusField();
  }

  removeChip(name: string): void {
    this.chips.update((chips) => chips.filter((chip) => chip !== name));
    this.focusField();
  }

  // --- Moving through the queue -------------------------------------------

  save(): void {
    const note = this.note();
    const chips = this.chips();
    if (!note || !chips.length || this.saving()) return;
    this.saving.set(true);
    this.topicsService.link(chips, [note.id]).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.saving.set(false);
        this.recent.update((recent) => [...chips, ...recent.filter((name) => !hasTopic(chips, name))].slice(0, RECENT_SHOWN));
        this.drop(note.id);
        this.sortedCount.update((count) => count + 1);
        this.state.refresh();
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Could not save the topics. Try again.');
      },
    });
  }

  skip(): void {
    if (this.saving() || !this.note() || this.index() + 1 >= this.remaining()) return;
    this.index.update((index) => index + 1);
    this.settle();
  }

  previous(): void {
    if (this.saving() || this.index() === 0) return;
    this.index.update((index) => index - 1);
    this.settle();
  }

  /** A note left the queue (saved or deleted); the next one takes its place. */
  private drop(noteId: string): void {
    const at = this.queue().findIndex((note) => note.id === noteId);
    if (at < 0) return;
    this.queue.update((queue) => queue.filter((note) => note.id !== noteId));
    if (at < this.index()) this.index.update((index) => index - 1);
    this.remaining.update((remaining) => Math.max(0, remaining - 1));
    if (this.index() >= this.remaining()) this.index.set(Math.max(0, this.remaining() - 1));
    this.settle();
  }

  leave(): void {
    void this.router.navigate(['/brain/unsorted'], { queryParams: { book: this.bookId, q: this.query } });
  }

  // --- The note's other actions --------------------------------------------

  /** An inline topic link opens in the app. */
  onThoughtClick(event: MouseEvent): void {
    const href = inlineTopicHref(event);
    if (!href) return;
    event.preventDefault();
    void this.router.navigateByUrl(href);
  }

  /** Its passage in the reader when the note's place is known, else the book. */
  openSource(note: NoteSearchHit): void {
    const link = noteSourceLink(note);
    void this.router.navigate(link.commands, { queryParams: link.queryParams ?? undefined });
  }

  /** Esc leaves from anywhere on the screen, unless a dialog is open over it. */
  onEscape(event: Event): void {
    // A menu or list that Escape closed has claimed it.
    if (event.defaultPrevented || this.actions.deleteIds().length || this.actions.handoffIds().length) return;
    this.leave();
  }

  private focusField(): void {
    afterNextRender(() => this.field()?.nativeElement.focus(), { injector: this.injector });
  }
}
