import {
  ChangeDetectionStrategy,
  Component,
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

import { isPassage, type NoteOrder, type NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { AssistantService } from '../ui/assistant/assistant.service';
import { ButtonComponent } from '../ui/button/button.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { ChipComponent } from '../ui/chip/chip.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import {
  hasTopic,
  newTopicName,
  noteLocationLabel,
  rankTopicChoices,
  thoughtForDisplay,
  topicChoices,
} from '../ui/note-item/note-item.helpers';
import { BrainWritingHandoffComponent } from '../second-brain/writing-handoff/brain-writing-handoff.component';
import { BrainSortPositionService } from './brain-sort-position.service';
import { BrainStateService } from './brain-state.service';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';
import { isOrder } from './brain.helpers';

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
    ConfirmModal,
    IconButtonComponent,
    NostosIconComponent,
    LoadingIndicatorComponent,
    BrainWritingHandoffComponent,
  ],
  providers: [BrainTopicSuggestionsService],
  templateUrl: './brain-sort.page.html',
  styleUrls: ['./brain-sort.page.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown.escape)': 'onEscape()' },
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

  readonly state = inject(BrainStateService);
  readonly suggestions = inject(BrainTopicSuggestionsService);

  readonly bookId = this.route.snapshot.queryParamMap.get('book');
  readonly order: NoteOrder = (() => {
    const value = this.route.snapshot.queryParamMap.get('order');
    return isOrder(value) && value !== 'book' ? value : 'oldest';
  })();

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

  readonly deleting = signal(false);
  readonly confirmingDelete = signal(false);
  readonly handoffIds = signal<string[]>([]);

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

  private readonly choicesSource = computed(() => topicChoices(this.state.topics().filter((t) => t.usageCount > 0)));

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
    return names.filter((name, i) => !hasTopic(chosen, name) && names.findIndex((n) => n.toLocaleLowerCase() === name.toLocaleLowerCase()) === i)
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
    this.position.read().subscribe((saved) => {
      const resume = saved && saved.book === this.bookId && saved.order === this.order ? saved.noteId : null;
      this.loadPage(resume);
    });
  }

  private loadPage(resumeId: string | null = null): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.failed.set(false);
    this.notes.browse({
      withoutTopics: true,
      bookId: this.bookId ?? undefined,
      order: this.order,
      limit: PAGE_SIZE,
      offset: this.queue().length,
    }).subscribe({
      next: (page) => {
        const held = new Set(this.queue().map((note) => note.id));
        this.queue.update((queue) => [...queue, ...page.items.filter((note) => !held.has(note.id))]);
        this.remaining.set(page.totalCount);
        this.loading.set(false);
        this.loaded.set(true);
        if (resumeId) {
          const at = this.queue().findIndex((note) => note.id === resumeId);
          if (at >= 0) this.index.set(at);
        }
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
    const note = this.note();
    this.position.write(note ? { noteId: note.id, book: this.bookId, order: this.order } : null);
    this.suggestions.clear();
    this.typed.set('');
    this.chips.set([]);
    this.active.set(0);
    this.focusField();
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

  askSuggestions(): void {
    const note = this.note();
    if (note) this.suggestions.request(note);
  }

  // --- Moving through the queue -------------------------------------------

  save(): void {
    const note = this.note();
    const chips = this.chips();
    if (!note || !chips.length || this.saving()) return;
    this.saving.set(true);
    this.topicsService.link(chips, [note.id]).subscribe({
      next: () => {
        this.saving.set(false);
        this.recent.update((recent) => [...chips, ...recent.filter((name) => !hasTopic(chips, name))].slice(0, RECENT_SHOWN));
        this.dropCurrent();
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
    if (!this.note() || this.index() + 1 >= this.remaining()) return;
    this.index.update((index) => index + 1);
    this.settle();
  }

  previous(): void {
    if (this.index() === 0) return;
    this.index.update((index) => index - 1);
    this.settle();
  }

  /** The note in focus left the queue (saved or deleted); the next one takes its place. */
  private dropCurrent(): void {
    const at = this.index();
    this.queue.update((queue) => queue.filter((_, i) => i !== at));
    this.remaining.update((remaining) => Math.max(0, remaining - 1));
    if (this.index() >= this.remaining()) this.index.set(Math.max(0, this.remaining() - 1));
    this.settle();
  }

  leave(): void {
    void this.router.navigate(['/brain/unsorted'], { queryParams: { book: this.bookId } });
  }

  // --- The note's other actions --------------------------------------------

  askDelete(): void {
    this.confirmingDelete.set(true);
  }

  confirmDelete(): void {
    const note = this.note();
    if (!note || this.deleting()) return;
    this.deleting.set(true);
    this.notes.delete(note.id).subscribe({
      next: () => {
        this.deleting.set(false);
        this.confirmingDelete.set(false);
        this.dropCurrent();
        this.state.refresh();
        this.toast.success('Note deleted');
      },
      error: () => {
        this.deleting.set(false);
        this.toast.error('Could not delete the note. Try again.');
      },
    });
  }

  openBook(bookId: string): void {
    void this.router.navigate(['/library', bookId]);
  }

  keepWithWriting(): void {
    const note = this.note();
    if (note) this.handoffIds.set([note.id]);
  }

  /** Esc leaves from anywhere on the screen, unless a dialog is open over it. */
  onEscape(): void {
    if (this.confirmingDelete() || this.handoffIds().length) return;
    this.leave();
  }

  private focusField(): void {
    afterNextRender(() => this.field()?.nativeElement.focus(), { injector: this.injector });
  }
}
