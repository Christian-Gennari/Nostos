import { BOOK_FORMAT_LABEL } from '../../core/book-format';
import { Component, computed, effect, inject, input, output, signal, untracked, ChangeDetectionStrategy } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';

import { Book, EditionSummaryDto } from '../../core/dtos/book.dtos';
import {
  AlignmentProblemDetails,
  isAlignmentUnavailableCode,
} from '../../core/dtos/alignment.dtos';
import { AlignmentService } from '../../core/services/alignment.service';
import { rememberSwitched } from './edition-place-keeper';
import { ModalShell } from '../../ui/modal-shell/modal-shell.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { NoticeComponent } from '../../ui/notice/notice.component';
import { IconTileComponent } from '../../ui/icon-tile/icon-tile.component';
import { ChoiceRowComponent } from '../../ui/choice-row/choice-row.component';

/** How an edition can be reached from the one being read. */
export type SwitchKind = 'audiobook' | 'epub' | 'pdf' | 'ebook';

export interface SwitchTarget {
  edition: EditionSummaryDto;
  kind: SwitchKind;
  format: string;
  subtitle: string;
  /** True when Nostos can try to land on the same sentence. */
  sameSentence: boolean;
}

interface DeclinedState {
  editionId: string;
  message: string;
  approximateSeconds: number | null;
}

/**
 * "Continue in another edition": every other readable edition of the work, one
 * row each. Between an audiobook and an EPUB the row continues from the same
 * sentence; any other pair opens the edition where it was left. The dialog owns
 * the request, the waiting state and the honest "could not find it" outcome, so
 * neither reader carries a second, differently-placed copy of this flow.
 */
@Component({
  selector: 'app-switch-edition-dialog',
  standalone: true,
  imports: [
    ChoiceRowComponent,
    IconTileComponent,
    NoticeComponent,
    ModalShell,
    NostosIconComponent,
    ButtonComponent,
    LoadingIndicatorComponent,
  ],
  templateUrl: './switch-edition-dialog.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './switch-edition-dialog.component.css',
})
export class SwitchEditionDialog {
  private readonly alignment = inject(AlignmentService);
  private readonly router = inject(Router);

  isOpen = input.required<boolean>();
  book = input<Book | null>(null);
  /** The audiobook's position when the dialog opened; null when unknown. */
  audioPositionSeconds = input<number | null>(null);
  /** The first text on the ebook page when the dialog opened; null when none. */
  visibleExcerpt = input<string | null>(null);

  closed = output<void>();

  readonly busyEditionId = signal<string | null>(null);
  readonly declined = signal<DeclinedState | null>(null);
  readonly error = signal<{ editionId: string; message: string } | null>(null);
  /** The search has run long enough that the reader deserves a way out. */
  readonly slow = signal(false);
  /** Same-sentence switching turned out to be unavailable on this deployment. */
  readonly unavailable = signal(false);

  private request: Subscription | null = null;
  private generation = 0;
  private slowTimer: ReturnType<typeof setTimeout> | null = null;

  /** Finding a place usually takes a few seconds; past this it is "slow". */
  static readonly SlowAfterMs = 10_000;

  readonly targets = computed<SwitchTarget[]>(() => {
    const book = this.book();
    if (!book) return [];

    const fromAudio = isAudio(book.type);
    const ownKind = kindOf(book);
    const fromText = !fromAudio && (ownKind === 'epub' || ownKind === 'pdf');
    const canAlign = !this.unavailable();

    return (book.otherEditions ?? [])
      .filter((edition) => edition.id !== book.id && edition.hasFile)
      .map((edition) => {
        const kind = kindOf(edition);
        const sameSentence =
          canAlign &&
          ((fromAudio &&
            (kind === 'epub' || kind === 'pdf') &&
            (this.audioPositionSeconds() ?? 0) > 0) ||
            (fromText && kind === 'audiobook' && !!this.visibleExcerpt()?.trim()));

        return {
          edition,
          kind,
          format: formatLabel(kind),
          subtitle: subtitleOf(edition, kind),
          sameSentence,
        };
      });
  });

  constructor() {
    // A dialog reopened later, or on another book, starts clean.
    effect(() => {
      this.isOpen();
      this.book()?.id;
      untracked(() => this.reset());
    });
  }

  close(): void {
    this.reset();
    this.closed.emit();
  }

  choose(target: SwitchTarget): void {
    if (this.busyEditionId()) return;

    this.declined.set(null);
    this.error.set(null);

    if (!target.sameSentence) {
      this.open(target.edition.id);
      return;
    }

    const book = this.book();
    if (!book) return;

    const editionId = target.edition.id;
    const generation = ++this.generation;
    this.busyEditionId.set(editionId);
    this.slowTimer = setTimeout(() => {
      if (generation === this.generation && this.busyEditionId() === editionId) {
        this.slow.set(true);
      }
    }, SwitchEditionDialog.SlowAfterMs);

    if (target.kind !== 'audiobook') {
      this.request = this.alignment
        .locateText(book.id, {
          positionSeconds: this.audioPositionSeconds()!,
          targetBookId: editionId,
        })
        .subscribe({
          next: (result) => {
            if (generation !== this.generation) return;
            this.endBusy();

            if (!result.located) {
              this.decline(editionId, null);
              return;
            }

            if (result.pdf && Number.isInteger(result.pdf.pageNumber) && result.pdf.pageNumber > 0) {
              const pdfParams: Record<string, string | number> = {
                sourcePage: result.pdf.pageNumber,
              };
              if (result.pdf.pageLabel) pdfParams['sourcePageLabel'] = result.pdf.pageLabel;
              // The book's own wording, so the reader's search can mark it.
              if (result.sourceText) pdfParams['sourceExcerpt'] = result.sourceText;
              this.open(editionId, pdfParams);
              return;
            }

            const locator = result.locator;
            const queryParams: Record<string, string | number> = {};
            if (locator?.resourceHref) queryParams['sourceHref'] = locator.resourceHref;
            if (locator?.spineIndex != null) queryParams['sourceSpine'] = locator.spineIndex;
            if (locator?.textOffset != null) queryParams['sourceOffset'] = locator.textOffset;
            if (locator?.cfi) queryParams['sourceCfi'] = locator.cfi;
            if (result.excerpt) queryParams['sourceExcerpt'] = result.excerpt;
            // The reader underlines the sentence it lands in.
            if (locator?.textOffset != null) queryParams['sourceMark'] = 'sentence';

            if (!queryParams['sourceHref'] && !queryParams['sourceCfi']) {
              this.decline(editionId, null);
              return;
            }

            this.open(editionId, queryParams);
          },
          error: (failure) => this.fail(editionId, generation, failure),
        });
      return;
    }

    this.request = this.alignment
      .locateAudio(book.id, {
        targetBookId: editionId,
        excerpt: this.visibleExcerpt()!.trim(),
      })
      .subscribe({
        next: (result) => {
          if (generation !== this.generation) return;
          this.endBusy();

          if (!result.located) {
            const approximate = result.approximatePositionSeconds;
            this.decline(
              editionId,
              typeof approximate === 'number' && Number.isFinite(approximate) && approximate >= 0
                ? approximate
                : null,
            );
            return;
          }

          if (!Number.isFinite(result.positionSeconds) || result.positionSeconds < 0) {
            this.decline(editionId, null);
            return;
          }

          this.open(editionId, { sourceTime: result.positionSeconds });
        },
        error: (failure) => this.fail(editionId, generation, failure),
      });
  }

  /** After a decline: open the edition where it was left. */
  openWhereLeft(editionId: string): void {
    if (this.busyEditionId()) return;
    this.open(editionId);
  }

  /** While a search runs long: stop waiting and open the edition where it was left. */
  stopWaitingAndOpen(editionId: string): void {
    if (this.busyEditionId() !== editionId) return;
    ++this.generation;
    this.request?.unsubscribe();
    this.request = null;
    this.endBusy();
    this.open(editionId);
  }

  private endBusy(): void {
    if (this.slowTimer) clearTimeout(this.slowTimer);
    this.slowTimer = null;
    this.slow.set(false);
    this.busyEditionId.set(null);
  }

  /** After a decline with an estimate: jump there, labelled as approximate. */
  openApproximate(): void {
    const declined = this.declined();
    if (this.busyEditionId() || !declined || declined.approximateSeconds === null) return;
    this.open(declined.editionId, { sourceTime: declined.approximateSeconds });
  }

  iconFor(kind: SwitchKind): 'headphones' | 'book-open' {
    return kind === 'audiobook' ? 'headphones' : 'book-open';
  }

  private decline(editionId: string, approximateSeconds: number | null): void {
    this.declined.set({
      editionId,
      approximateSeconds,
      message:
        approximateSeconds === null
          ? 'Could not find this exact place in that edition.'
          : 'Could not find the exact sentence in that edition.',
    });
  }

  private fail(editionId: string, generation: number, failure: unknown): void {
    if (generation !== this.generation) return;
    this.endBusy();

    const problem = failure instanceof HttpErrorResponse
      ? (failure.error as AlignmentProblemDetails | null)
      : null;
    const code = problem?.title ?? '';

    if (isAlignmentUnavailableCode(code)) {
      // Rows fall back to "opens where you left off"; see the note in the template.
      this.unavailable.set(true);
      return;
    }

    this.error.set({
      editionId,
      message:
        failure instanceof HttpErrorResponse && failure.status === 429
          ? 'Too many tries in a row. Give it a moment.'
          : 'Could not find your place just now. Try again, or open it where you left off.',
    });
  }

  private open(editionId: string, queryParams?: Record<string, string | number>): void {
    const generation = this.generation;
    const bookId = this.book()?.id;
    // A landing on the same sentence: from now on the reader keeps this
    // pair's place known as the text edition is read.
    if (queryParams && bookId) rememberSwitched(bookId, editionId);
    void this.router
      .navigate(['/read', editionId], queryParams ? { queryParams } : undefined)
      .then((navigated) => {
        if (navigated) {
          this.close();
        } else if (generation === this.generation) {
          this.error.set({ editionId, message: 'Could not open that edition. Try again.' });
        }
      })
      .catch(() => {
        if (generation === this.generation) {
          this.error.set({ editionId, message: 'Could not open that edition. Try again.' });
        }
      });
  }

  private reset(): void {
    ++this.generation;
    this.request?.unsubscribe();
    this.request = null;
    this.endBusy();
    this.declined.set(null);
    this.error.set(null);
  }
}

function isAudio(type: string | undefined): boolean {
  const value = (type ?? '').toLowerCase();
  return value === 'audiobook' || value === 'audio';
}

/** The readable kind of a book or edition, from its type, format and file name. */
export function kindOf(book: {
  type?: string;
  format?: string | null;
  fileName?: string | null;
}): SwitchKind {
  if (isAudio(book.type)) return 'audiobook';

  const format = (book.format ?? '').trim().toLowerCase().replace(/^\./, '');
  const fileName = (book.fileName ?? '').trim().toLowerCase();

  if (format === 'epub' || fileName.endsWith('.epub')) return 'epub';
  if (format === 'pdf' || fileName.endsWith('.pdf')) return 'pdf';
  return 'ebook';
}

function formatLabel(kind: SwitchKind): string {
  switch (kind) {
    case 'audiobook':
      return BOOK_FORMAT_LABEL.audiobook;
    case 'epub':
      return BOOK_FORMAT_LABEL.ebook;
    case 'pdf':
      return BOOK_FORMAT_LABEL.pdf;
    default:
      return BOOK_FORMAT_LABEL.ebook;
  }
}

function subtitleOf(edition: EditionSummaryDto, kind: SwitchKind): string {
  const details = kind === 'audiobook'
    ? [edition.duration, edition.narrator, edition.edition]
    : [edition.edition];
  return details.filter((detail): detail is string => !!detail?.trim()).join(' • ');
}
