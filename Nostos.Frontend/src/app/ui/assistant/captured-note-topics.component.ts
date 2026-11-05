import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
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
  `,
})
export class CapturedNoteTopicsComponent implements OnInit {
  readonly noteId = input.required<string>();

  protected readonly brain = inject(BrainStateService);
  private readonly notes = inject(NotesService);
  private readonly topics = inject(TopicsService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  /** The note's topics; null until read (and when it could not be read). */
  protected readonly names = signal<readonly string[] | null>(null);
  protected readonly busy = signal(false);

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
    this.names.set(note.topicNames ?? []);
  }
}
