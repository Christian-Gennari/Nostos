import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ModalShell } from '../../ui/modal-shell/modal-shell.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { DialogActionsComponent } from '../../ui/dialog-actions/dialog-actions.component';
import { FormFieldComponent } from '../../ui/form-field/form-field.component';
import { InputDirective, TextareaDirective } from '../../ui/form-control/form-control.directive';
import { ProgressComponent } from '../../ui/progress/progress.component';
import { LOW_CONFIDENCE, OcrWord, PageOcrService, selectionText } from './page-ocr';

/** What the page saves: the quote as checked, the user's comment, the page. */
export interface PhotoQuote {
  text: string;
  comment: string;
  page: number | null;
}

type Stage = 'reading' | 'select' | 'failed';

/**
 * A quote from a photo of a paper book's page (issue #1292). The photo is read
 * on the device; the user selects the quote among the page's words (tap the
 * first and last word, or drag across), fixes any misread word, and saves it
 * as a highlight at a page. The photo itself is never uploaded or kept.
 */
@Component({
  selector: 'app-photo-quote-modal',
  standalone: true,
  imports: [
    FormsModule,
    ModalShell,
    ButtonComponent,
    DialogActionsComponent,
    FormFieldComponent,
    InputDirective,
    TextareaDirective,
    ProgressComponent,
  ],
  templateUrl: './photo-quote-modal.component.html',
  styleUrl: './photo-quote-modal.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PhotoQuoteModal {
  private readonly ocr = inject(PageOcrService);

  /** The photographed page; a new photo starts a new reading. */
  readonly photo = input<Blob | null>(null);
  readonly language = input('eng');
  /** The page the book is at, to start the page field from. */
  readonly page = input<number | null>(null);
  readonly pageCount = input<number | null>(null);
  readonly busy = input(false);

  readonly closed = output<void>();
  readonly retake = output<void>();
  readonly save = output<PhotoQuote>();

  readonly stage = signal<Stage>('reading');
  readonly progress = signal<number | null>(null);
  readonly words = signal<OcrWord[]>([]);
  /** The selection's first and last word, in the order they were picked. */
  readonly anchor = signal<number | null>(null);
  readonly focus = signal<number | null>(null);

  readonly quote = signal('');
  readonly comment = signal('');
  readonly pageValue = signal<number | null>(null);

  readonly lowConfidence = LOW_CONFIDENCE;

  readonly paragraphs = computed(() => {
    const groups: { index: number; word: OcrWord }[][] = [];
    this.words().forEach((word, index) => {
      (groups[word.paragraph] ??= []).push({ index, word });
    });
    return groups.filter(Boolean);
  });

  readonly range = computed(() => {
    const anchor = this.anchor();
    if (anchor === null) return null;
    const focus = this.focus() ?? anchor;
    return { from: Math.min(anchor, focus), to: Math.max(anchor, focus) };
  });

  readonly canSave = computed(() => this.quote().trim().length > 0 && !this.busy());

  private dragging = false;
  private reading = 0;

  constructor() {
    effect(() => {
      const photo = this.photo();
      untracked(() => {
        this.pageValue.set(this.page());
        if (photo) void this.read(photo);
      });
    });
  }

  private async read(photo: Blob): Promise<void> {
    const run = ++this.reading;
    this.stage.set('reading');
    this.progress.set(null);
    this.words.set([]);
    this.anchor.set(null);
    this.focus.set(null);
    this.quote.set('');
    try {
      const words = await this.ocr.read(photo, this.language(), (fraction) => {
        if (run === this.reading) this.progress.set(Math.round(fraction * 100));
      });
      if (run !== this.reading) return;
      this.words.set(words);
      this.stage.set(words.length > 0 ? 'select' : 'failed');
    } catch (error) {
      // The user sees "no text"; the console says why (a missing engine
      // file, an unsupported browser) for whoever is debugging it.
      console.warn('Photo quote: reading the page failed', error);
      if (run === this.reading) this.stage.set('failed');
    }
  }

  selected(index: number): boolean {
    const range = this.range();
    return range !== null && index >= range.from && index <= range.to;
  }

  /** A tap picks the first word, the next tap the last; a third starts again. */
  pick(index: number): void {
    if (this.anchor() === null || this.focus() !== null) {
      this.anchor.set(index);
      this.focus.set(null);
    } else {
      this.focus.set(index);
    }
    this.syncQuote();
  }

  pointerDown(event: PointerEvent, index: number): void {
    if (event.pointerType === 'mouse' && event.button !== 0) return;
    this.dragging = false;
    this.pressed = index;
  }

  pointerMove(event: PointerEvent): void {
    if (this.pressed === null) return;
    const index = this.wordAt(event.clientX, event.clientY);
    if (index === null || (index === this.pressed && !this.dragging)) return;
    if (!this.dragging) {
      this.dragging = true;
      this.anchor.set(this.pressed);
    }
    this.focus.set(index);
    this.syncQuote();
  }

  pointerUp(index: number): void {
    if (this.pressed === null) return;
    if (!this.dragging) this.pick(index);
    this.pressed = null;
    this.dragging = false;
  }

  /** The pointer left the page or the browser took it over (a scroll): keep what is selected. */
  pointerCancel(): void {
    this.pressed = null;
    this.dragging = false;
  }

  private pressed: number | null = null;

  private wordAt(x: number, y: number): number | null {
    const element = document.elementFromPoint(x, y)?.closest<HTMLElement>('[data-word]');
    const value = element?.dataset['word'];
    return value === undefined ? null : Number(value);
  }

  private syncQuote(): void {
    const range = this.range();
    this.quote.set(range ? selectionText(this.words(), range.from, range.to) : '');
  }

  setPage(value: string): void {
    const page = Number.parseInt(value, 10);
    this.pageValue.set(Number.isFinite(page) && page > 0 ? page : null);
  }

  submit(): void {
    if (!this.canSave()) return;
    this.save.emit({
      text: this.quote().trim(),
      comment: this.comment().trim(),
      page: this.pageValue(),
    });
  }
}
