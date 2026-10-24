import {
  AfterViewInit,
  Component,
  ElementRef,
  ViewChild,
  Injector,
  OnDestroy,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  ChangeDetectionStrategy,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ButtonComponent } from '../../ui/button/button.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { SegmentedComponent, SegmentedOption } from '../../ui/segmented/segmented.component';
import { SwitchComponent } from '../../ui/switch/switch.component';
import { RangeDirective } from '../../ui/range/range.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { ThemePickerComponent } from '../../ui/theme-picker/theme-picker.component';
import { SettingsPopoverComponent, type SettingsPopoverDismissal } from '../../ui/settings-popover/settings-popover.component';
import { SoundPack, TypewriterSound, TypewriterSounds } from '../../ui/markdown-editor/typewriter-sounds';
import {
  FONT_SIZE_RANGE,
  LINE_SPACING_PRESETS,
  STUDIO_FONTS,
  StudioFont,
  StudioPreferencesService,
  TEXT_WIDTH_PRESETS,
  nearestLineSpacing,
  nearestTextWidth,
} from './studio-preferences.service';
import { Book, UpdateBookDto } from '../../core/dtos/book.dtos';
import { ToastService } from '../../core/services/toast.service';
import { BooksService } from '../../core/services/books.service';
import { CITATION_STYLES, CitationStyle } from '../citations/citation-styles';
import { CitationService } from '../citations/citation.service';
import { CitationDetail, bookToCslItem, missingForStyle, publishedYear } from '../citations/csl-item';

/** A book the writing cites, and what the chosen style misses in its details. */
export interface IncompleteSource {
  book: Book;
  missing: CitationDetail[];
}

const DETAIL_FIELDS: Record<CitationDetail, { label: string; placeholder: string }> = {
  author: { label: 'Author', placeholder: 'e.g. Simone Weil' },
  year: { label: 'Year', placeholder: 'e.g. 1947' },
  publisher: { label: 'Publisher', placeholder: 'e.g. Routledge' },
};

/** The segmented control needs a value per option; null is the standard measure. */
const STANDARD_WIDTH_VALUE = 'standard';

/**
 * The Studio's page settings (issues #1041, #1060): a small popover under the
 * Aa button (a bottom sheet on a phone). It does not dim or cover the page,
 * so a theme, face or measure is judged on the writing itself.
 */
@Component({
  selector: 'app-studio-page-settings',
  standalone: true,
  imports: [ButtonComponent, IconButtonComponent, SegmentedComponent, SwitchComponent, RangeDirective, NostosIconComponent, ThemePickerComponent, InputDirective, SettingsPopoverComponent],
  templateUrl: './studio-page-settings.component.html',
  styleUrl: './studio-page-settings.component.css',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class StudioPageSettingsComponent implements AfterViewInit, OnDestroy {
  readonly prefs = inject(StudioPreferencesService);
  /** The panel itself: it renders in the overlay, outside this component's host. */
  @ViewChild('panel', { static: true }) private readonly panel!: ElementRef<HTMLElement>;
  private readonly injector = inject(Injector);

  /** The Aa button: the popover hangs under it, and a click on it is its own toggle. */
  readonly anchor = input<HTMLElement | null>(null);
  /** A book from the open writing, to show each citation style on; else the library's latest. */
  readonly exampleBookId = input<string | null>(null);
  /** Opened on the Citations list, e.g. from a toast about a book missing details (#1157). */
  readonly startView = input<'main' | 'citations'>('main');
  /** The book whose missing details take focus when opened on the Citations list. */
  readonly focusBook = input<string | null>(null);
  /** The books the open writing cites or keeps. */
  readonly citedBookIds = input<readonly string[]>([]);
  /** A book's details were saved here; its citations follow. */
  readonly bookUpdated = output<Book>();
  /** Escape or a press outside: the Studio closes the panel (and on Escape goes back to the page). */
  readonly closed = output<SettingsPopoverDismissal>();

  readonly view = signal<'main' | 'fonts' | 'citations'>('main');

  private readonly books = inject(BooksService);
  private readonly citations = inject(CitationService);
  private readonly toast = inject(ToastService);
  readonly detailFields = DETAIL_FIELDS;
  /** The writing's books, once loaded. */
  readonly citedBooks = signal<Book[] | null>(null);
  /** The writing's books the chosen style lacks details for, the focused one first. */
  readonly incomplete = computed<IncompleteSource[]>(() => {
    const style = this.prefs.citationStyle();
    const focus = this.focusBook();
    return (this.citedBooks() ?? [])
      .map((book) => ({ book, missing: missingForStyle(bookToCslItem(book), style) }))
      .filter((entry) => entry.missing.length)
      .sort((a, b) => Number(b.book.id === focus) - Number(a.book.id === focus));
  });
  /** The book being saved, and the one whose year could not be read. */
  readonly saving = signal<string | null>(null);
  readonly invalidYear = signal<string | null>(null);
  readonly citationStyles = CITATION_STYLES;
  /** The book the examples are built from, once loaded. */
  readonly exampleBook = signal<Book | null>(null);
  /** Each style's citation and reference entry for the example book. */
  readonly examples = signal<Record<string, { citation: string; reference: string } | 'error'>>({});
  /** What the example book lacks for proper citations, e.g. "the year". */
  readonly exampleMissing = computed(() => {
    const book = this.exampleBook();
    return book ? missingForStyle(bookToCslItem(book), this.prefs.citationStyle()) : [];
  });

  readonly fontGroups = [...new Set(STUDIO_FONTS.map((f) => f.group))].map((group) => ({
    group,
    fonts: STUDIO_FONTS.filter((f) => f.group === group),
  }));
  readonly sizeRange = FONT_SIZE_RANGE;

  readonly spacingOptions: readonly SegmentedOption<number>[] = LINE_SPACING_PRESETS.map((p) => ({ value: p.value, label: p.label }));
  readonly spacing = computed(() => nearestLineSpacing(this.prefs.preferences().lineSpacing));

  readonly widthOptions: readonly SegmentedOption<string>[] = TEXT_WIDTH_PRESETS.map((p) => ({
    value: p.value === null ? STANDARD_WIDTH_VALUE : String(p.value),
    label: p.label,
  }));
  readonly width = computed(() => {
    const nearest = nearestTextWidth(this.prefs.preferences().textWidth);
    return nearest === null ? STANDARD_WIDTH_VALUE : String(nearest);
  });

  readonly soundPackOptions: readonly SegmentedOption<SoundPack>[] = [
    { value: 'typewriter', label: 'Typewriter' },
    { value: 'keyboard', label: 'Keyboard' },
  ];

  /** Plays a sample from the settings themselves; made on the first preview. */
  private soundPreview: TypewriterSounds | null = null;

  ngAfterViewInit(): void {
    if (this.startView() === 'citations') {
      this.openCitations(true);
      return;
    }
    // Focus moves into the popover, onto the theme now chosen.
    queueMicrotask(() => this.focusTheme());
  }

  ngOnDestroy(): void {
    this.soundPreview?.dispose();
  }

  setSoundPack(soundPack: SoundPack): void {
    this.prefs.update({ soundPack });
    this.previewSound('key');
  }

  /**
   * One key at the current level and pack, so the level can be set without
   * leaving the panel. Called when the slider comes to rest (release, or each
   * arrow-key step), not on every step of a drag.
   */
  previewSound(sound: TypewriterSound = 'key'): void {
    const prefs = this.prefs.preferences();
    this.soundPreview ??= new TypewriterSounds();
    this.soundPreview.setPack(prefs.soundPack);
    void this.soundPreview.preview(sound, prefs.soundVolume);
  }

  /** In the typeface list Esc steps back; on the main view it is the Studio's to close. */
  escape(event: Event): void {
    if (this.view() === 'main') return;
    event.preventDefault();
    this.backToMain();
  }

  stepSize(delta: number): void {
    this.prefs.update({ fontSize: this.prefs.preferences().fontSize + delta });
  }

  setWidth(value: string): void {
    this.prefs.update({ textWidth: value === STANDARD_WIDTH_VALUE ? null : Number(value) });
  }

  /** The typeface list is one radio group: arrows move and pick, one tab stop. */
  fontKey(event: KeyboardEvent, font: StudioFont): void {
    const step = event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1 : event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const index = STUDIO_FONTS.indexOf(font);
    const next = STUDIO_FONTS[(index + step + STUDIO_FONTS.length) % STUDIO_FONTS.length];
    this.prefs.update({ font: next.value });
    queueMicrotask(() =>
      (this.panel.nativeElement.querySelector(`[data-font-option="${next.value}"]`) as HTMLElement | null)?.focus(),
    );
  }

  openFonts(): void {
    this.view.set('fonts');
    // The button pressed is gone with the view: focus must land in the list,
    // once it exists, or Esc would reach the Studio and close everything.
    this.focusAfterRender('.ps-font[aria-checked="true"]');
  }

  pickFont(font: StudioFont): void {
    this.prefs.update({ font: font.value });
    this.backToMain();
  }

  backToMain(): void {
    const from = this.view();
    this.view.set('main');
    this.focusAfterRender(from === 'citations' ? '.ps-citations' : '.ps-typeface');
  }

  openCitations(toDetails = false): void {
    this.view.set('citations');
    void this.loadExamples();
    const loading = this.loadCitedBooks();
    if (!toDetails) {
      this.focusAfterRender('.ps-font[aria-checked="true"]');
      return;
    }
    // From a toast: straight to the first missing detail of the book it named.
    void loading.then(() => this.focusAfterRender('.ps-cite-source input', '.ps-font[aria-checked="true"]'));
  }

  private async loadCitedBooks(): Promise<void> {
    const ids = [...new Set(this.citedBookIds())];
    if (!ids.length) {
      this.citedBooks.set([]);
      return;
    }
    const books = await Promise.all(ids.map((id) => this.citations.book(id)));
    this.citedBooks.set(books.filter((book): book is Book => !!book));
  }

  /** Saves the details typed for one book; its citations in the writing follow. */
  async saveDetails(event: Event, entry: IncompleteSource): Promise<void> {
    event.preventDefault();
    const form = event.target as HTMLFormElement;
    const value = (field: CitationDetail) =>
      ((form.elements.namedItem(field) as HTMLInputElement | null)?.value ?? '').replace(/\s+/g, ' ').trim();
    const dto: UpdateBookDto = {};
    for (const field of entry.missing) {
      const text = value(field);
      if (!text) continue;
      if (field === 'author') dto.author = text;
      if (field === 'publisher') dto.publisher = text;
      if (field === 'year') {
        if (!publishedYear(text)) {
          this.invalidYear.set(entry.book.id);
          (form.elements.namedItem('year') as HTMLInputElement | null)?.focus();
          return;
        }
        dto.publishedDate = text;
      }
    }
    this.invalidYear.set(null);
    if (!Object.keys(dto).length) return;
    this.saving.set(entry.book.id);
    try {
      const saved = await firstValueFrom(this.books.update(entry.book.id, dto));
      this.citations.updated(saved);
      this.citedBooks.update((books) => (books ?? []).map((book) => (book.id === saved.id ? saved : book)));
      if (this.exampleBook()?.id === saved.id) {
        this.exampleBook.set(null);
        void this.loadExamples(saved.id);
      }
      this.bookUpdated.emit(saved);
      this.focusAfterRender('.ps-cite-source input', '.ps-font[aria-checked="true"]');
    } catch {
      this.toast.error('Could not save the book’s details');
    } finally {
      this.saving.set(null);
    }
  }

  pickCitationStyle(style: CitationStyle): void {
    this.prefs.update({ citationStyle: style.id });
    this.backToMain();
  }

  /** The style list is one radio group, like the typefaces. */
  citationKey(event: KeyboardEvent, style: CitationStyle): void {
    const step = event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1 : event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const index = CITATION_STYLES.indexOf(style);
    const next = CITATION_STYLES[(index + step + CITATION_STYLES.length) % CITATION_STYLES.length];
    this.prefs.update({ citationStyle: next.id });
    queueMicrotask(() =>
      (this.panel.nativeElement.querySelector(`[data-citation-option="${next.id}"]`) as HTMLElement | null)?.focus(),
    );
  }

  private async loadExamples(bookId: string | null = null): Promise<void> {
    if (this.exampleBook()) return;
    try {
      const id = bookId ?? this.exampleBookId();
      const book = id
        ? await firstValueFrom(this.books.get(id))
        : (await firstValueFrom(this.books.list({ page: 1, pageSize: 1 }))).items[0];
      if (!book) return;
      this.exampleBook.set(book);
      for (const style of CITATION_STYLES) {
        this.citations.example(book, style).then(
          (example) => this.examples.update((all) => ({ ...all, [style.id]: example })),
          () => this.examples.update((all) => ({ ...all, [style.id]: 'error' })),
        );
      }
    } catch {
      // The list still works without examples.
    }
  }

  example(style: CitationStyle): { citation: string; reference: string } | null {
    const value = this.examples()[style.id];
    return value && value !== 'error' ? value : null;
  }

  /** "*Title*" from an example as plain text with the italics kept. */
  exampleHtml(markdown: string): string {
    return markdown
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/\\([\\*_`[\]])/g, '$1')
      .replace(/\*([^*]+)\*/g, '<i>$1</i>');
  }

  checkedFrom(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  numberFrom(event: Event): number {
    return Number((event.target as HTMLInputElement).value);
  }

  private focusAfterRender(selector: string, fallback: string | null = null): void {
    afterNextRender(
      () => {
        const root = this.panel.nativeElement;
        ((root.querySelector(selector) ?? (fallback ? root.querySelector(fallback) : null)) as HTMLElement | null)?.focus();
      },
      { injector: this.injector },
    );
  }

  private focusTheme(): void {
    const root = this.panel.nativeElement;
    ((root.querySelector('.tp-swatch[aria-checked="true"]') ?? root.querySelector('.tp-swatch')) as HTMLElement | null)?.focus();
  }
}
