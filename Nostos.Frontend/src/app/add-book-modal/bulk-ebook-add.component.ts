import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpEventType } from '@angular/common/http';
import { firstValueFrom, lastValueFrom } from 'rxjs';
import { filter } from 'rxjs/operators';

import { BooksService } from '../core/services/books.service';
import {
  BookUploadService,
  describeBookUploadFailure,
} from '../core/services/book-upload.service';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NoticeComponent } from '../ui/notice/notice.component';
import { ProgressComponent } from '../ui/progress/progress.component';

/** Larger files are not sent just to read their title; the file name stays. */
const MAX_METADATA_READ_BYTES = 50 * 1024 * 1024;

export type BulkRowStatus = 'reading' | 'ready' | 'adding' | 'added' | 'duplicate' | 'failed';

export interface BulkRow {
  file: File;
  title: string;
  author: string;
  language: string;
  status: BulkRowStatus;
  note: string;
}

/** Title taken from a file name, the same way the single-file form does it. */
export function titleFromFileName(name: string): string {
  return name
    .replace(/\.[^.]+$/, '')
    .replace(/[_-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * Several EPUB or PDF files at once (#1407). Each file becomes one book,
 * created and uploaded one after another through the same single-file calls
 * the Add a book form uses, so the duplicate check, the resumable upload and
 * the plan limits behave exactly as they do for one file.
 *
 * Kept apart from the single-file form on purpose: the form owns one book's
 * full record; this owns a short list and a loop.
 */
@Component({
  selector: 'app-bulk-ebook-add',
  standalone: true,
  imports: [FormsModule, ButtonComponent, InputDirective, NoticeComponent, ProgressComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="bulk" aria-labelledby="bulk-heading">
      <div class="bulk__head">
        <h3 id="bulk-heading">{{ heading() }}</h3>
        <p>Title and author are read from each file. Change anything that looks wrong.</p>
      </div>

      <ul class="bulk__list" data-testid="bulk-list">
        @for (row of rows(); track row.file; let i = $index) {
          <li class="bulk__row" [attr.data-status]="row.status">
            <span class="bulk__file">{{ row.file.name }}</span>
            <input
              appInput
              class="bulk__title"
              type="text"
              [ngModel]="row.title"
              [ngModelOptions]="{ standalone: true }"
              (ngModelChange)="edit(i, 'title', $event)"
              [disabled]="locked()"
              [attr.aria-label]="'Title for ' + row.file.name"
            />
            <input
              appInput
              class="bulk__author"
              type="text"
              placeholder="Author"
              [ngModel]="row.author"
              [ngModelOptions]="{ standalone: true }"
              (ngModelChange)="edit(i, 'author', $event)"
              [disabled]="locked()"
              [attr.aria-label]="'Author for ' + row.file.name"
            />
            <span class="bulk__state" role="status">{{ stateText(row) }}</span>
          </li>
        }
      </ul>

      @if (running()) {
        <nostos-progress
          [value]="overallPercent()"
          label="Adding books"
          [valueText]="'Book ' + current() + ' of ' + total()"
        />
      }

      @if (summary(); as text) {
        <div appNotice tone="info" role="status" data-testid="bulk-summary">{{ text }}</div>
      }

      <div class="bulk__actions">
        <button appButton type="button" variant="secondary" (click)="cancel()">
          {{ finished() ? 'Done' : running() ? 'Stop after this book' : 'Cancel' }}
        </button>
        @if (!finished()) {
          <button
            appButton
            type="button"
            variant="primary"
            data-testid="bulk-add"
            [disabled]="!canStart()"
            [busy]="running()"
            (click)="start()"
          >
            {{ running() ? 'Adding…' : 'Add ' + addable() + (addable() === 1 ? ' book' : ' books') }}
          </button>
        }
      </div>
    </section>
  `,
  styles: [
    `
      .bulk {
        display: flex;
        flex-direction: column;
        gap: 0.9rem;
      }
      .bulk__head h3 {
        margin: 0;
        font-size: 1rem;
      }
      .bulk__head p {
        margin: 0.2rem 0 0;
        color: var(--color-text-muted);
        font-size: 0.85rem;
      }
      .bulk__list {
        display: flex;
        flex-direction: column;
        margin: 0;
        padding: 0;
        list-style: none;
        border-top: 1px solid var(--border-light);
      }
      .bulk__row {
        display: grid;
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        gap: 0.35rem 0.5rem;
        padding: 0.65rem 0;
        border-bottom: 1px solid var(--border-light);
      }
      .bulk__file,
      .bulk__state {
        grid-column: 1 / -1;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        font-size: 0.78rem;
        color: var(--color-text-muted);
      }
      .bulk__state:empty {
        display: none;
      }
      .bulk__row[data-status='failed'] .bulk__state {
        color: var(--color-danger, inherit);
      }
      .bulk__actions {
        display: flex;
        justify-content: flex-end;
        gap: 0.5rem;
      }
      @media (max-width: 520px) {
        .bulk__row {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class BulkEbookAddComponent {
  private readonly booksService = inject(BooksService);
  private readonly bookUploads = inject(BookUploadService);
  private destroyed = false;
  private stopRequested = false;

  readonly files = input.required<File[]>();
  /** Fired once at least one book was created, when the person leaves. */
  readonly closed = output<{ changed: boolean }>();

  readonly rows = signal<BulkRow[]>([]);
  readonly running = signal(false);
  readonly finished = signal(false);
  readonly current = signal(0);
  private readonly filePercent = signal(0);

  readonly total = signal(0);
  readonly locked = computed(() => this.running() || this.finished());
  readonly addable = computed(
    () => this.rows().filter((r) => r.status === 'ready' || r.status === 'reading').length,
  );
  readonly canStart = computed(
    () =>
      !this.running() &&
      this.addable() > 0 &&
      this.rows().every((r) => r.status !== 'ready' || r.title.trim().length > 0),
  );
  readonly overallPercent = computed(() => {
    const n = this.total();
    if (!n) return 0;
    return Math.min(100, Math.round(((this.current() - 1 + this.filePercent() / 100) / n) * 100));
  });
  readonly heading = computed(() => {
    const n = this.rows().length;
    return n === 1 ? '1 book file' : `${n} book files`;
  });
  readonly summary = signal<string | null>(null);
  private changed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.stopRequested = true;
    });
  }

  ngOnInit(): void {
    const rows = this.files().map<BulkRow>((file) => ({
      file,
      title: titleFromFileName(file.name),
      author: '',
      language: '',
      status: file.size > MAX_METADATA_READ_BYTES ? 'ready' : 'reading',
      note: '',
    }));
    this.rows.set(rows);
    void this.readAllMetadata(rows.map((r) => r.file));
  }

  private patch(index: number, change: Partial<BulkRow>): void {
    this.rows.update((all) => all.map((r, i) => (i === index ? { ...r, ...change } : r)));
  }

  edit(index: number, field: 'title' | 'author', value: string): void {
    this.patch(index, { [field]: value });
  }

  private async readAllMetadata(files: File[]): Promise<void> {
    for (const file of files) {
      const index = this.rows().findIndex((r) => r.file === file);
      if (index < 0 || this.rows()[index].status !== 'reading') continue;
      const nameTitle = titleFromFileName(file.name);
      try {
        const found = await firstValueFrom(this.booksService.readFileMetadata(file));
        if (this.destroyed) return;
        const row = this.rows()[index];
        // Keep what the person already typed while the file was being read.
        this.patch(index, {
          title: found.title && (!row.title.trim() || row.title === nameTitle) ? found.title : row.title,
          author: found.author && !row.author.trim() ? found.author : row.author,
          language: found.language ?? '',
          status: 'ready',
        });
      } catch {
        if (this.destroyed) return;
        // The file name stays as the title; nothing for the person to do.
        this.patch(index, { status: 'ready' });
      }
    }
  }

  stateText(row: BulkRow): string {
    switch (row.status) {
      case 'reading':
        return 'Reading the file…';
      case 'adding':
        return 'Adding…';
      case 'added':
        return row.note || 'Added';
      default:
        return row.note;
    }
  }

  cancel(): void {
    if (this.running()) {
      this.stopRequested = true;
      return;
    }
    this.closed.emit({ changed: this.changed });
  }

  async start(): Promise<void> {
    if (!this.canStart()) return;
    this.running.set(true);
    this.stopRequested = false;
    this.summary.set(null);

    const queue = this.rows()
      .map((r, i) => ({ r, i }))
      .filter(({ r }) => r.status === 'ready' || r.status === 'reading');
    this.total.set(queue.length);
    let position = 0;
    for (const { i } of queue) {
      if (this.stopRequested || this.destroyed) break;
      position++;
      this.current.set(position);
      this.filePercent.set(0);
      await this.addOne(i);
    }

    this.running.set(false);
    this.finished.set(true);
    this.summary.set(this.buildSummary());
  }

  private buildSummary(): string {
    const rows = this.rows();
    const count = (s: BulkRowStatus) => rows.filter((r) => r.status === s).length;
    const parts = [`${count('added')} added`];
    if (count('duplicate')) parts.push(`${count('duplicate')} already in your library`);
    if (count('failed')) parts.push(`${count('failed')} failed`);
    const notStarted = rows.filter((r) => r.status === 'ready' || r.status === 'reading').length;
    if (notStarted) parts.push(`${notStarted} not added`);
    return parts.join(', ') + '.';
  }

  private async addOne(index: number): Promise<void> {
    const row = this.rows()[index];
    const file = row.file;
    this.patch(index, { status: 'adding', title: row.title.trim() });
    const resumable = this.bookUploads.supportsResumable();
    const language = row.language ? this.languageName(row.language) : 'English';

    let bookSaved = false;
    try {
      const { book, created } = await firstValueFrom(
        this.booksService.createOrMatch({
          type: 'ebook',
          title: row.title.trim(),
          subtitle: null,
          author: row.author.trim() || null,
          editor: null,
          translator: null,
          narrator: null,
          description: null,
          edition: null,
          asin: null,
          duration: null,
          isbn: null,
          publisher: null,
          placeOfPublication: null,
          publishedDate: null,
          pageCount: null,
          language,
          categories: null,
          series: null,
          volumeNumber: null,
          // Same flags the single-file form sends, so the library treats
          // this exactly like one file added by hand.
          fileUploadExpected: !resumable,
          fileAttached: true,
        }),
      );
      this.changed = true;
      bookSaved = true;

      if (!created && book.hasFile) {
        this.patch(index, {
          status: 'duplicate',
          note: `Already in your library as “${book.title}”. Skipped.`,
        });
        return;
      }

      if (resumable) {
        await this.bookUploads.upload(book.id, file, {
          onProgress: (p) => this.filePercent.set(p.percent),
        });
      } else {
        await lastValueFrom(
          this.booksService
            .uploadFile(book.id, file)
            .pipe(filter((event) => event.type === HttpEventType.Response)),
        );
      }
      this.patch(index, {
        status: 'added',
        note: (book.editionCount ?? 1) > 1 ? `Added as another edition of “${book.title}”.` : '',
      });
    } catch (error) {
      this.patch(index, {
        status: 'failed',
        note: describeBookUploadFailure(error, { bookSaved }),
      });
    }
  }

  private languageName(code: string): string {
    if (code.trim().length > 3) return code.trim();
    try {
      return new Intl.DisplayNames(['en'], { type: 'language' }).of(code.trim()) || code;
    } catch {
      return code;
    }
  }
}
