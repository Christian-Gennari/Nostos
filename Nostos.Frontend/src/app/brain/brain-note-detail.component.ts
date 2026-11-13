import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { UNKNOWN_BOOK } from '../core/book-format';
import { TopicsService, type NoteContextDto, type TopicWritingUsageDto } from '../core/services/topics.service';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';
import { NoteItemComponent, type CitingWriting } from '../ui/note-item/note-item.component';
import type { QuoteBlockSize } from '../ui/quote-block/quote-block.component';
import { BrainNoteActionsService } from './brain-note-actions.service';
import { BrainStateService } from './brain-state.service';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';
import {
  relatedNotesFrom,
  writingsCitingNote,
  type RelatedNote,
  type TopicNotes,
  type TopicUsage,
} from './brain-note-detail.helpers';

interface TopicContext {
  topicName: string;
  notes: NoteContextDto[];
  usage: TopicWritingUsageDto | null;
}

/** How many of a note's topics are read for related notes and citations. */
const CONTEXT_TOPICS = 4;
/** How many related notes the detail shows. */
const RELATED_SHOWN = 4;
/** How many writings the topic's usage reply can name. */
const USAGE_LIMIT = 50;

/**
 * One note in full (issue #1531): the quote or passage opening, the reader's
 * own words, topics, where it sits in its book, its related notes and the
 * writings that cite it. The same component is the note's own page and the
 * wide Brain's detail pane, so the two can never drift.
 *
 * It owns no note data: the host says which note is open and how to show a
 * change. The related notes and citations are composed from the topic
 * endpoints the Brain already reads; there is no per-note API.
 */
@Component({
  selector: 'app-brain-note-detail',
  standalone: true,
  imports: [NostosDatePipe, NoteItemComponent],
  templateUrl: './brain-note-detail.component.html',
  styleUrls: ['./brain-note-detail.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainNoteDetailComponent {
  protected readonly unknownBook = UNKNOWN_BOOK;
  readonly note = input.required<NoteSearchHit>();
  /** The larger quote step where the note is the page, not a row in a list. */
  readonly quoteSize = input<QuoteBlockSize>('lg');

  readonly moreFromBook = output<void>();
  /** A related note was chosen: the host opens it in the same place. */
  readonly relatedChosen = output<string>();

  protected readonly state = inject(BrainStateService);
  protected readonly noteActions = inject(BrainNoteActionsService);
  protected readonly suggestions = inject(BrainTopicSuggestionsService);
  private readonly topicsService = inject(TopicsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly related = signal<readonly RelatedNote[]>([]);
  readonly cited = signal<readonly CitingWriting[]>([]);
  readonly busy = computed(() => this.noteActions.busy().has(this.note().id));

  /** The note's topics that the Brain knows, so their notes can be read. */
  private readonly contextTopics = computed(() => {
    const lookup = this.state.lookup();
    const seen = new Set<string>();
    const found: { id: string; name: string }[] = [];
    for (const name of this.note().topicNames ?? []) {
      const topic = lookup.get(name.trim().toLocaleLowerCase());
      if (!topic || seen.has(topic.id)) continue;
      seen.add(topic.id);
      found.push({ id: topic.id, name: topic.name });
      if (found.length === CONTEXT_TOPICS) break;
    }
    return found;
  });

  /** One label for the section head: what the related notes have in common. */
  readonly sharedTopicsLabel = computed(() =>
    this.contextTopics().map((topic) => topic.name).slice(0, RELATED_SHOWN).join(', '));

  private contextSeq = 0;
  private contextNoteId: string | null = null;

  constructor() {
    effect(() => {
      const note = this.note();
      // Read those here so a rename, a merge or the topics arriving after the
      // note does starts the section again.
      this.state.topics();
      this.state.topicsRewritten();
      untracked(() => this.loadContext(note));
    });
    this.destroyRef.onDestroy(() => this.contextSeq++);
  }

  private loadContext(note: NoteSearchHit): void {
    const topics = this.contextTopics();
    const seq = ++this.contextSeq;
    if (this.contextNoteId !== note.id) {
      this.contextNoteId = note.id;
      this.related.set([]);
      this.cited.set([]);
    }
    if (!topics.length) {
      this.related.set([]);
      this.cited.set([]);
      return;
    }
    forkJoin(topics.map((topic) => forkJoin({
      topicName: of(topic.name),
      notes: this.topicsService.get(topic.id).pipe(
        map((detail) => detail.notes),
        catchError(() => of([] as NoteContextDto[])),
      ),
      usage: this.topicsService.getWritingUsage(topic.id, USAGE_LIMIT).pipe(
        catchError(() => of(null)),
      ),
    }))).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (contexts: TopicContext[]) => {
        if (seq !== this.contextSeq) return;
        const notes: TopicNotes[] = contexts.map((context) => ({
          topicName: context.topicName,
          notes: context.notes,
        }));
        const usage: TopicUsage[] = contexts.map((context) => ({ usage: context.usage }));
        this.related.set(relatedNotesFrom(notes, note.id, RELATED_SHOWN));
        this.cited.set(writingsCitingNote(usage, note.id));
      },
      error: () => {
        if (seq !== this.contextSeq) return;
        this.related.set([]);
        this.cited.set([]);
      },
    });
  }

  openRelated(note: RelatedNote): void {
    this.relatedChosen.emit(note.id);
  }
}
