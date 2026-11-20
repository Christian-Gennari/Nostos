import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { BrainStateService } from '../../brain/brain-state.service';
import type { NoteSearchHit } from '../../core/dtos/note.dtos';
import { NotesService } from '../../core/services/notes.service';
import { TopicsService } from '../../core/services/topics.service';
import { ToastService } from '../../core/services/toast.service';
import { TopicRowComponent } from '../topic-row/topic-row.component';

/**
 * A thought captured in Ask Nostos, as its topics (issue #1449): the same pill
 * row a note's card has, so a capture can get a topic where it was written,
 * without opening the Brain. A `[[link]]` typed in the capture shows here too.
 */
@Component({
  selector: 'app-captured-note-topics',
  standalone: true,
  imports: [TopicRowComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (quote(); as words) {
      <blockquote class="captured-quote" data-testid="assistant-captured-quote">{{ words }}</blockquote>
    }
    @if (names(); as topics) {
      <app-topic-row
        [names]="topics"
        [topics]="brain.topics()"
        [topicMap]="brain.lookup()"
        [disabled]="busy()"
        label="Add a topic to this captured note"
        (added)="change('link', $event)"
        (removed)="change('unlink', $event)"
      />
    }
  `,
  styles: `
    :host {
      display: block;
      margin-top: 0.4rem;
    }
    .captured-quote {
      margin: 0 0 0.4rem;
      padding-left: 0.7rem;
      border-left: 2px solid var(--border-color);
      color: var(--color-text-muted);
      font-size: 0.9em;
    }
  `,
})
export class CapturedNoteTopicsComponent implements OnInit {
  readonly noteId = input.required<string>();
  /**
   * The reply text beside this card. A saved quote's words are shown under the
   * card unless that text already shows them (issue #1748); a thought the user
   * typed is theirs and is already in the transcript.
   */
  readonly shownText = input('');

  protected readonly brain = inject(BrainStateService);
  private readonly notes = inject(NotesService);
  private readonly topics = inject(TopicsService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  /** The note's topics; null until read (and when it could not be read). */
  protected readonly names = signal<readonly string[] | null>(null);
  protected readonly busy = signal(false);
  /** The saved note's quoted words, when the reply does not already show them. */
  private readonly savedQuote = signal<string | null>(null);
  protected readonly quote = computed(() => {
    const words = this.savedQuote();
    if (!words) return null;
    const shown = this.shownText().toLowerCase();
    return shown.includes(words.slice(0, 80).toLowerCase()) ? null : words;
  });

  ngOnInit(): void {
    this.brain.ensureTopicsLoaded();
    this.notes
      .get(this.noteId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (note) => this.show(note), error: () => this.names.set(null) });
  }

  protected change(kind: 'link' | 'unlink', name: string): void {
    if (this.busy()) return;
    this.busy.set(true);
    const call = kind === 'link' ? this.topics.link([name], [this.noteId()]) : this.topics.unlink([name], [this.noteId()]);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => {
        this.busy.set(false);
        const note = result.notes.find((held) => held.id === this.noteId());
        if (note) this.show(note);
        if (result.changedNoteIds.length) this.brain.refresh();
      },
      error: () => {
        this.busy.set(false);
        this.toast.error(kind === 'link' ? 'Could not add the topic. Try again.' : 'Could not remove the topic. Try again.');
      },
    });
  }

  private show(note: NoteSearchHit): void {
    const words = (note.selectedText ?? '').trim();
    this.savedQuote.set(words ? (words.length > 300 ? `${words.slice(0, 300).trimEnd()}…` : words) : null);
    this.names.set(note.topicNames ?? []);
  }
}
