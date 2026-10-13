import { Component, inject, Injector, OnInit, OnDestroy, signal, computed, effect, untracked, ViewChild, HostListener, ElementRef } from '@angular/core';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { CommonModule, Location } from '@angular/common';
import { FormsModule } from '@angular/forms';

// Services
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { TopicsService, TopicDto } from '../core/services/topics.service';
import { TopicAutocompleteService } from '../ui/topic-autocomplete-panel/topic-autocomplete.service';

// DTOs & Interfaces
import {
  CreateNoteDto,
  Note,
  formatAudioTime,
  importedNoteLocation,
  noteAudioSeconds,
  noteNavigationTarget,
} from '../core/dtos/note.dtos';
import { IReader, ReaderSearchState, ReaderSourceTarget, TocItem } from './reader.interface';
import { isInteractiveTarget, isTypingTarget, pageActionForKey } from './reader-keyboard';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HIGHLIGHT_COLOURS,
  HighlightColour,
  readHighlightColour,
  writeHighlightColour,
} from './highlight-colours';

// Components
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { ButtonComponent } from '../ui/button/button.component';
import { ScrollModeType } from 'ngx-extended-pdf-viewer';
import { PdfReader } from './pdf-reader/pdf-reader.component';
import { EpubReader } from './epub-reader/epub-reader.component';
import type { SelectionAnchor } from './epub-reader/epub-annotation-manager';
import { AudioReader } from './audio-reader/audio-reader.component';
import { EditionSummaryDto } from '../core/dtos/book.dtos';
import { SwitchEditionDialog } from './switch-edition/switch-edition-dialog.component';
import { EditionPlaceKeeper, KeeperPlace } from './switch-edition/edition-place-keeper';
import { AlignmentService } from '../core/services/alignment.service';
import { TopicInputComponent } from '../ui/topic-input.component/topic-input.component';
import { NoteCardComponent } from '../ui/note-card.component/note-card.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { InputDirective, TextareaDirective } from '../ui/form-control/form-control.directive';
import { readReaderReturnOrigin } from '../core/navigation/studio-reader-navigation';
import { Theme, ThemeService } from '../core/services/theme.service';
import { ToastService } from '../core/services/toast.service';
import { FeedbackLinkService } from '../core/services/feedback-link.service';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import { AssistantService } from '../ui/assistant/assistant.service';

/** Longest quote a selection surface renders (#657); the full text is still saved. */
export const SELECTION_PREVIEW_MAX = 320;

/**
 * The quote shown in the selection menu and bar (#657). Whitespace is folded so
 * a passage spanning paragraphs reads as one line of prose, and a very long
 * selection is cut at a word boundary. CSS clamps what is visible (three lines
 * in the menu, one in the docked bar); this cap only keeps the DOM small.
 */
export function selectionPreview(text: string | null, max = SELECTION_PREVIEW_MAX): string {
  if (!text) return '';
  const flat = text.replace(/\s+/g, ' ').trim();
  if (flat.length <= max) return flat;
  const cut = flat.slice(0, max);
  const space = cut.lastIndexOf(' ');
  return `${(space > max * 0.6 ? cut.slice(0, space) : cut).trimEnd()}…`;
}

import { BrowserVoice } from './read-aloud/browser-voice';
import { ReadAloudBarComponent } from './read-aloud/read-aloud-bar.component';
import { ReadAloudController } from './read-aloud/read-aloud-controller';
import { ServerVoice } from './read-aloud/server-voice';
import {
  READ_ALOUD_UNAVAILABLE,
  ReadAloudService,
  ReadAloudStatus,
} from '../core/services/read-aloud.service';
import { firstValueFrom } from 'rxjs';

const READ_ALOUD_VOICE_KEY = 'nostos.readAloud.voice';

function readStoredReadAloudVoice(): string | null {
  try {
    return localStorage.getItem(READ_ALOUD_VOICE_KEY);
  } catch {
    return null;
  }
}

/** Why the server voice gave way to the device's, in the person's terms. */
function readAloudFallbackNotice(code: string | null): string {
  switch (code) {
    case 'ai_usage_limit_reached':
      return 'Your AI allowance is used up. Reading with this device’s voice.';
    case 'ai_access_denied':
    case 'read_aloud_access_denied':
      return 'Your plan does not include the natural voice. Reading with this device’s voice.';
    case 'read_aloud_unsupported_language':
      return 'The natural voice cannot read this language. Reading with this device’s voice.';
    default:
      return 'The natural voice is not available right now. Reading with this device’s voice.';
  }
}

@Component({
  selector: 'app-reader-shell',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    NostosIconComponent,
    PdfReader,
    EpubReader,
    AudioReader,
    SwitchEditionDialog,
    TopicInputComponent,
    NoteCardComponent,
    IconButtonComponent,
    ButtonComponent,
    ConfirmModal,
    InputDirective,
    TextareaDirective,
    ReadAloudBarComponent,
  ],
  providers: [ReadAloudController],
  templateUrl: './reader-shell.component.html',
  styleUrl: './reader-shell.component.css',
})
export class ReaderShell implements OnInit, OnDestroy {
  constructor() {
    // Read aloud follows whichever text reader is mounted.
    effect(() => {
      const source = this.activeReader()?.readAloud ?? null;
      untracked(() => {
        this.readAloudServer.set(null);
        this.readAloud.setVoices(this.browserVoice);
        this.readAloud.attach(source);
      });
    });

    // While a book is read aloud, a headset's or lock screen's buttons drive it.
    effect(() => {
      const active = this.readAloud.active();
      const book = this.book();
      untracked(() => this.syncReadAloudMediaSession(active, book?.title, book?.author));
    });

    effect(() => {
    const bookId = this.book()?.id;
    if (!bookId) return;
    this.highlightColour.set(readHighlightColour(bookId));
    });

    // A page turn moves the text away from an anchored selection menu (#650).
    // Rather than float over the wrong words, the menu docks to the bottom
    // edge; the pending mark and any typed note are kept.
    effect(() => {
      this.progressState()?.label;
      untracked(() => {
        if (this.selectionAnchor()) this.selectionAnchor.set(null);
      });
    });

    // Each page turn restarts the place keeper's wait for a settled page.
    effect(() => {
      this.progressState()?.label;
      this.book()?.id;
      const type = this.fileType();
      untracked(() => {
        if (type === 'epub' || type === 'pdf') this.placeKeeper.moved();
        else this.placeKeeper.stop();
      });
    });

    this.dockQuery?.addEventListener?.('change', this.onDockQueryChange);
    this.veryNarrowQuery?.addEventListener?.('change', this.onVeryNarrowChange);
    this.phoneQuery?.addEventListener?.('change', this.onPhoneChange);
  }

  // ------------------------------------------------------------------
  // Selection menu (#650)
  // ------------------------------------------------------------------

  /**
   * Phones and touch-first devices keep the bottom-docked bar: the selection
   * handles occupy the space around the text there, and the bar is the
   * thumb-safe place. Fine-pointer desktops anchor the menu at the text.
   */
  private readonly dockQuery: MediaQueryList | null =
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia('(max-width: 768px), (pointer: coarse)')
      : null;
  dockedLayout = signal(this.dockQuery?.matches ?? true);
  private readonly onDockQueryChange = (event: MediaQueryListEvent) =>
    this.dockedLayout.set(event.matches);

  /**
   * The narrowest supported phone width. A PDF header cannot hold five 44px
   * tools here, so Feedback moves into the existing View settings panel rather
   * than shrinking touch targets or adding another menu.
   */
  private readonly veryNarrowQuery: MediaQueryList | null =
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia('(max-width: 360px)')
      : null;
  readonly veryNarrow = signal(this.veryNarrowQuery?.matches ?? false);
  private readonly onVeryNarrowChange = (event: MediaQueryListEvent) =>
    this.veryNarrow.set(event.matches);

  /**
   * Phone widths. Six 44px tools squeeze the title to a few characters here, so
   * when the switch-edition tool is present Feedback yields its header slot.
   */
  private readonly phoneQuery: MediaQueryList | null =
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia('(max-width: 480px)')
      : null;
  readonly phone = signal(this.phoneQuery?.matches ?? false);
  private readonly onPhoneChange = (event: MediaQueryListEvent) =>
    this.phone.set(event.matches);

  /** Where the captured selection sits; null docks the menu. */
  selectionAnchor = signal<SelectionAnchor | null>(null);
  noteDraftOpen = signal(false);
  noteDraft = signal('');
  private committingNote = false;

  /**
   * Viewport placement for the anchored menu, or null for the docked bar.
   * Opens below the selection when there is room, else above; it grows away
   * from the text (top-anchored below, bottom-anchored above) so neither the
   * collapsed menu nor the note editor covers the selection, and it is clamped
   * horizontally to the viewport.
   */
  selectionMenuPlacement = computed<
    { left: number; width: number; top: number | null; bottom: number | null } | null
  >(() => {
    const anchor = this.selectionAnchor();
    if (!anchor || this.dockedLayout()) return null;
    if (typeof window === 'undefined') return null;

    const gap = 8;
    const edge = 8;
    const minVisible = 64;
    const reserve = 240; // room for the note editor when it opens
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const width = Math.min(360, vw - edge * 2);
    const centre = (anchor.left + anchor.right) / 2;
    const left = Math.min(Math.max(centre - width / 2, edge), vw - width - edge);

    const roomBelow = vh - anchor.bottom;
    const roomAbove = anchor.top;
    if (roomBelow >= reserve || roomBelow >= roomAbove) {
      return { left, width, top: Math.min(anchor.bottom + gap, vh - minVisible), bottom: null };
    }
    return { left, width, top: null, bottom: Math.min(vh - anchor.top + gap, vh - minVisible) };
  });

  // Template-ref query (not type query): the epub child is stubbed in specs,
  // and a type query would resolve to null against the stub.
  @ViewChild('epubReader') epubReader?: EpubReader;
  // Concrete type query: the shell drives fixed-layout PDF controls through the
  // real reader instance. Specs that need this delegation attach their stub
  // explicitly rather than turning every lightweight PDF stub into the full API.
  @ViewChild(PdfReader) pdfReader?: PdfReader;
  @ViewChild(AudioReader) audioReader?: IReader;

  private host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private location = inject(Location);
  private booksService = inject(BooksService);
  private notesService = inject(NotesService);
  private topicsService = inject(TopicsService);
  private autocompleteService = inject(TopicAutocompleteService);

  private themeService = inject(ThemeService);
  private toast = inject(ToastService);
  private readonly assistant = inject(AssistantService);
  readonly askNostosAvailable = this.assistant.surfaceAvailable;
  private readonly assistantContext = inject(AssistantContextService);
  private unregisterSelectionAssistantContext: (() => void) | null = null;

  /**
   * The Cloud feedback destination for the Reader (`?from=reader`), or null on
   * SelfHosted. The Reader is its own immersive shell, so it owns this utility
   * entry rather than the workspace dock.
   */
  readonly feedbackUrl = inject(FeedbackLinkService).url;

  /** Formats whose View settings panel exists and can host the narrow entry. */
  private readonly hasViewSettingsPanel = computed(
    () => this.fileType() === 'epub' || this.fileType() === 'pdf',
  );

  /**
   * The header entry hides only where the View settings panel can take over.
   * Audio keeps it at every width, because that format has no such panel and
   * its three-tool header never overflows.
   */
  readonly showHeaderFeedback = computed(() => !this.showPanelFeedback());

  /** The narrow replacement, a quiet link row inside the View settings panel. */
  readonly showPanelFeedback = computed(
    () =>
      this.hasViewSettingsPanel() &&
      (this.veryNarrow() || (this.phone() && (this.canSwitchEdition() || this.readAloudOffered()))),
  );

  // --- Continue in another edition ---

  /** Other editions of this work that can actually be opened. */
  private readonly switchableEditions = computed<EditionSummaryDto[]>(() => {
    const book = this.book();
    const editions: EditionSummaryDto[] = book?.otherEditions ?? [];
    return editions.filter((edition) => edition.id !== book?.id && edition.hasFile);
  });

  readonly canSwitchEdition = computed(
    () => !this.loadError() && !!this.fileType() && this.switchableEditions().length > 0,
  );

  /**
   * The tool shows where it leads: headphones when every other edition is an
   * audiobook, an open book when none is, the editions mark when they are mixed.
   */
  readonly switchEditionIcon = computed<'headphones' | 'book-open' | 'stack'>(() => {
    const editions = this.switchableEditions();
    const audio = editions.filter((edition) => edition.type?.toLowerCase() === 'audiobook').length;
    if (audio === editions.length) return 'headphones';
    return audio === 0 ? 'book-open' : 'stack';
  });

  /**
   * At 320px an EPUB/PDF header holds four tools, so this one joins the panel.
   * On a phone it also yields to Read aloud, which is used far more often while
   * reading: five tools is what a 390px header holds before the title goes.
   */
  readonly showPanelSwitch = computed(
    () =>
      this.canSwitchEdition() &&
      this.hasViewSettingsPanel() &&
      (this.veryNarrow() || (this.phone() && this.readAloudOffered())),
  );
  readonly showHeaderSwitch = computed(() => this.canSwitchEdition() && !this.showPanelSwitch());

  // --- Read aloud ---

  readonly readAloud = inject(ReadAloudController);
  private readonly browserVoice = BrowserVoice.isSupported() ? new BrowserVoice() : null;

  /**
   * The open book's text can be read and this device has a voice for it. A
   * scanned PDF has pictures of pages and no text, so there is nothing to read.
   */
  readonly readAloudOffered = computed(
    () =>
      !this.loadError() &&
      this.readAloud.available() &&
      this.browserVoice !== null &&
      !(this.fileType() === 'pdf' && this.pdfReader?.textCapability() === 'unavailable'),
  );
  /** At 320px the header has no room for it, so it joins the View settings panel. */
  readonly showPanelReadAloud = computed(() => this.readAloudOffered() && this.veryNarrow());
  readonly showHeaderReadAloud = computed(() => this.readAloudOffered() && !this.veryNarrow());

  // The server voice, when this host has one. It is asked for per book
  // language; null means not asked yet.
  private readonly readAloudApi = inject(ReadAloudService);
  readonly readAloudServer = signal<ReadAloudStatus | null>(null);
  private readAloudServerRequest: Promise<ReadAloudStatus> | null = null;
  private readonly serverVoice = new ServerVoice((text) =>
    this.readAloudApi.speech(
      text,
      this.readAloudServerVoiceId(),
      this.activeReader()?.readAloud?.language() ?? null,
    ),
  );

  /** 'device', or the id of a server voice. Remembered on this device. */
  readonly readAloudVoiceChoice = signal<string | null>(readStoredReadAloudVoice());
  readonly readAloudServerVoices = computed(() => {
    const server = this.readAloudServer();
    return server?.available ? server.voices : [];
  });
  /** The server voice in use: the remembered one when this book's language has it, else the default. */
  readonly readAloudServerVoiceId = computed<string | null>(() => {
    const server = this.readAloudServer();
    if (!server?.available) return null;
    const choice = this.readAloudVoiceChoice();
    return server.voices.some((voice) => voice.id === choice) ? choice : server.defaultVoice;
  });
  /** What the voice row shows as selected. */
  readonly readAloudSelectedVoice = computed(() =>
    this.readAloudVoiceChoice() === 'device' ? 'device' : (this.readAloudServerVoiceId() ?? 'device'),
  );

  async toggleReadAloud(): Promise<void> {
    if (this.readAloud.active()) {
      this.readAloud.stop();
      return;
    }

    // Inside the tap: a phone only lets audio start from one, and asking the
    // server which voices it has would come too late.
    this.serverVoice.prime();
    this.browserVoice?.prime();
    this.applyReadAloudVoices(await this.loadReadAloudServer());
    await this.readAloud.start();
  }

  setReadAloudVoice(choice: string): void {
    this.readAloudVoiceChoice.set(choice);
    try {
      localStorage.setItem(READ_ALOUD_VOICE_KEY, choice);
    } catch {
      // The choice still applies for this session.
    }

    const server = this.readAloudServer();
    if (!server) return;
    const wasActive = this.readAloud.active();
    if (wasActive) this.readAloud.stop();
    this.applyReadAloudVoices(server);
    if (wasActive) {
      this.serverVoice.prime();
      void this.readAloud.start();
    }
  }

  /** Asks the server once per open book which voices it has for the book's language. */
  private loadReadAloudServer(): Promise<ReadAloudStatus> {
    const known = this.readAloudServer();
    if (known) return Promise.resolve(known);

    this.readAloudServerRequest ??= firstValueFrom(
      this.readAloudApi.status(this.activeReader()?.readAloud?.language() ?? null),
    )
      .catch(() => READ_ALOUD_UNAVAILABLE)
      .then((status) => {
        this.readAloudServerRequest = null;
        this.readAloudServer.set(status);
        return status;
      });
    return this.readAloudServerRequest;
  }

  private applyReadAloudVoices(server: ReadAloudStatus): void {
    const useServer = server.available && this.readAloudVoiceChoice() !== 'device';
    if (useServer) {
      this.readAloud.setVoices(this.serverVoice, this.browserVoice, readAloudFallbackNotice);
    } else {
      this.readAloud.setVoices(this.browserVoice);
    }
  }

  private syncReadAloudMediaSession(active: boolean, title?: string, author?: string): void {
    const session = typeof navigator !== 'undefined' ? navigator.mediaSession : undefined;
    if (!session) return;

    const handlers: Array<[MediaSessionAction, (() => void) | null]> = [
      ['play', active ? () => this.readAloud.resume() : null],
      ['pause', active ? () => this.readAloud.pause() : null],
      ['nexttrack', active ? () => this.readAloud.next() : null],
      ['previoustrack', active ? () => this.readAloud.previous() : null],
      ['stop', active ? () => this.readAloud.stop() : null],
    ];
    for (const [action, handler] of handlers) {
      try {
        session.setActionHandler(action, handler);
      } catch {
        // An action this browser does not know.
      }
    }

    if (active && typeof MediaMetadata === 'function') {
      session.metadata = new MediaMetadata({ title: title ?? 'Nostos', artist: author ?? '' });
    }
  }

  /**
   * Where the place keeper would look: a text edition being read, with an
   * audiobook edition to continue in. Null while the dialog has the floor.
   */
  private async keeperPlace(): Promise<KeeperPlace | null> {
    const book = this.book();
    const type = this.fileType();
    if (!book?.id || this.loadError() || this.switchEditionOpen()) return null;
    if (type !== 'epub' && type !== 'pdf') return null;

    const audio = this.switchableEditions().find(
      (edition) => edition.type?.toLowerCase() === 'audiobook',
    );
    if (!audio) return null;

    const excerpt =
      type === 'epub'
        ? (this.epubReader?.visibleExcerpt?.() ?? null)
        : await (this.pdfReader?.visibleExcerpt?.() ?? Promise.resolve(null));

    return excerpt && this.book()?.id === book.id
      ? { bookId: book.id, audioEditionId: audio.id, excerpt }
      : null;
  }

  // The alignment service is resolved only if the keeper ever has work to do.
  private readonly placeKeeper = new EditionPlaceKeeper(
    {
      locateAudio: (textBookId, request) =>
        this.injector.get(AlignmentService).locateAudio(textBookId, request),
    } as AlignmentService,
    () => this.keeperPlace(),
  );

  readonly switchEditionOpen = signal(false);
  readonly switchAudioPosition = signal<number | null>(null);
  readonly switchVisibleExcerpt = signal<string | null>(null);

  /**
   * Captures where the reader is right now, then opens the dialog. The
   * position is read once, on opening: the audiobook pauses and saves, and the
   * ebook reports the first text on the visible page.
   */
  openSwitchEdition(): void {
    if (!this.canSwitchEdition()) return;

    const audio = this.audioReader as unknown as { handoffPosition?: () => number | null } | undefined;
    this.switchAudioPosition.set(
      this.fileType() === 'audio' ? (audio?.handoffPosition?.() ?? null) : null,
    );
    this.switchVisibleExcerpt.set(
      this.fileType() === 'epub' ? (this.epubReader?.visibleExcerpt?.() ?? null) : null,
    );

    this.typoOpen.set(false);

    if (this.fileType() !== 'pdf') {
      this.switchEditionOpen.set(true);
      return;
    }

    // A PDF page's text is read from the document, which is asynchronous. Open
    // the dialog once it is known, so every row states the right promise.
    const bookId = this.book()?.id;
    void (this.pdfReader?.visibleExcerpt?.() ?? Promise.resolve(null))
      .catch(() => null)
      .then((excerpt) => {
        if (this.book()?.id !== bookId) return;
        this.switchVisibleExcerpt.set(excerpt);
        this.switchEditionOpen.set(true);
      });
  }

  /** View settings panel (EPUB and PDF) toggled by the Aa control. */
  typoOpen = signal(false);

  /**
   * EPUB/PDF chrome is absent while the reader is resting (#759). It is only a
   * visibility state: controls are absolutely overlaid, so revealing them can
   * never resize or repaginate the document.
   */
  chromeVisible = signal(false);

  /**
   * The app-wide theme, switchable from inside a book (#651). The readers
   * already follow ThemeService, so a switch is colour-only: no reload,
   * reflow or position change.
   */
  readonly appTheme = this.themeService.theme;
  readonly appThemes: readonly { value: Theme; label: string }[] = [
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
  ];

  setAppTheme(theme: Theme): void {
    this.themeService.setTheme(theme);
  }

  toggleTypo(): void {
    const opening = !this.typoOpen();
    if (opening) {
      this.closeSearch(false);
      this.rememberOverlayFocus();
      this.tocOpen.set(false);
      this.notesOpen.set(false);
      this.typoOpen.set(true);
      if (this.readAloudOffered()) void this.loadReadAloudServer();
      this.focusOverlay('.typo-panel button');
      return;
    }
    this.typoOpen.set(false);
    this.restoreOverlayFocus();
  }

  // ------------------------------------------------------------------
  // Shared in-book search (#761)
  // ------------------------------------------------------------------

  searchPanelOpen = signal(false);
  searchQuery = signal('');
  private searchDebounce: ReturnType<typeof setTimeout> | null = null;

  canSearch(): boolean {
    // Readiness belongs to the mounted format adapter: EPUB must finish its
    // opening/restore sequence, and PDF must have a live PDF.js find engine.
    return this.activeReader()?.searchAvailable?.() ?? false;
  }

  readerSearchState(): ReaderSearchState {
    return this.activeReader()?.searchState?.() ?? { status: 'idle', current: 0, total: 0 };
  }

  openSearch(): void {
    if (!this.canSearch()) return;
    if (!this.searchPanelOpen()) {
      this.rememberOverlayFocus();
      this.tocOpen.set(false);
      this.notesOpen.set(false);
      this.typoOpen.set(false);
      this.searchPanelOpen.set(true);
    }
    this.focusOverlay('.reader-search-input');
  }

  toggleSearch(): void {
    if (this.searchPanelOpen()) this.closeSearch();
    else this.openSearch();
  }

  closeSearch(restoreFocus = true): void {
    if (this.searchDebounce) {
      clearTimeout(this.searchDebounce);
      this.searchDebounce = null;
    }
    const wasOpen = this.searchPanelOpen();
    this.searchPanelOpen.set(false);
    this.searchQuery.set('');
    this.activeReader()?.clearSearch?.();
    if (restoreFocus && wasOpen) this.restoreOverlayFocus();
  }

  onSearchInput(event: Event): void {
    const query = (event.target as HTMLInputElement).value;
    this.searchQuery.set(query);
    if (this.searchDebounce) clearTimeout(this.searchDebounce);
    this.searchDebounce = setTimeout(() => {
      this.searchDebounce = null;
      void this.activeReader()?.search?.(query);
    }, 150);
  }

  nextSearchResult(): void {
    void this.activeReader()?.nextSearchResult?.();
  }

  previousSearchResult(): void {
    void this.activeReader()?.previousSearchResult?.();
  }

  /**
   * Fixed-layout view controls, driven by the shell's Aa panel. These delegate to
   * the PDF reader so the render scale lives with the document that owns it, and
   * so the panel can show which fit is in effect.
   */
  pdfZoomPresets(): { value: string | number; label: string }[] {
    return this.pdfReader?.zoomPresets ?? [];
  }

  pdfZoomLabel(): string {
    return this.pdfReader?.zoomLabel() ?? '';
  }

  isZoomPreset(value: string | number): boolean {
    return this.pdfReader?.isZoomPreset(value) ?? false;
  }

  setZoomPreset(value: string | number): void {
    this.pdfReader?.setZoom(value);
  }

  /** Reading mode (continuous vs page-by-page), driven by the same view panel. */
  pdfReadingModes(): { value: ScrollModeType; label: string }[] {
    return this.pdfReader?.readingModes ?? [];
  }

  isScrollMode(mode: ScrollModeType): boolean {
    return this.pdfReader?.isScrollMode(mode) ?? false;
  }

  setScrollMode(mode: ScrollModeType): void {
    this.pdfReader?.setScrollMode(mode);
  }

  book = signal<any>(null);
  loading = signal(true);
  loadError = signal<string | null>(null);
  notesOpen = signal(false);
  tocOpen = signal(false);
  ready = signal(false);
  private pendingGroundedSourceTarget: ReaderSourceTarget | null = null;
  private pendingSourceNoteId: string | null = null;
  private observedGroundedSourceKey: string | null | undefined;
  private sourceNavigationGeneration = 0;
  private sourceNavigationSubscription: { unsubscribe(): void } | null = null;
  private bookNavigationSubscription: { unsubscribe(): void } | null = null;
  private currentRouteBookId: string | null = null;
  private bookLoadGeneration = 0;
  private overlayReturnFocus: HTMLElement | null = null;
  private saveFeedbackTimer: ReturnType<typeof setTimeout> | null = null;
  /**
   * The book's highlighter pen (issue #208). Remembered per BOOK, like the
   * reader's zoom: the pen you want depends on what you are marking up, and
   * a book you annotate in sage should come back in sage.
   */
  highlightColour = signal<HighlightColour>(DEFAULT_HIGHLIGHT_COLOUR);
  readonly highlightColours = HIGHLIGHT_COLOURS;
  pendingSelectionText = signal<string | null>(null);
  readonly selectionPreview = computed(() => selectionPreview(this.pendingSelectionText()));
  highlightSaving = signal(false);

  dbNotes = signal<Note[]>([]);
  quickNoteContent = signal('');
  quickNoteSaving = signal(false);
  /**
   * The audio position the quick-note draft was started at, as the reader
   * reports it (issue #906). Playback keeps running while the user types, so
   * the moment is pinned at the first keystroke instead of read on Save.
   */
  quickNotePinnedLocation = signal<string | null>(null);
  readonly quickNotePinnedTime = computed(() => {
    const pinned = this.quickNotePinnedLocation();
    return pinned === null ? null : formatAudioTime(Number(pinned));
  });
  saveFeedback = signal<string | null>(null);

  /** Note id awaiting delete confirmation (asked through ConfirmModal). */
  pendingNoteDelete = signal<string | null>(null);

  // Topic map for the note cards
  topicMap = signal<Map<string, TopicDto>>(new Map());

  // --- UNIFIED READER LOGIC ---
  fileType = computed<'pdf' | 'epub' | 'audio' | null>(() => {
    // A multi-track audiobook has tracks instead of a single file name.
    if (this.book()?.tracks?.length) return 'audio';
    const fileName = this.book()?.fileName?.toLowerCase();
    if (!fileName) return null;
    if (fileName.endsWith('.pdf')) return 'pdf';
    if (fileName.endsWith('.epub')) return 'epub';
    if (fileName.endsWith('.m4b') || fileName.endsWith('.m4a') || fileName.endsWith('.mp3'))
      return 'audio';
    return null;
  });

  readonly immersiveReader = computed(
    () => this.fileType() === 'epub' || this.fileType() === 'pdf',
  );

  /**
   * Open panels pin chrome visible even when it was invoked from a keyboard
   * shortcut while the reader was resting. Audio keeps its established chrome.
   */
  readonly chromeShown = computed(
    () =>
      !this.immersiveReader()
      || this.chromeVisible()
      || this.searchPanelOpen()
      || this.typoOpen()
      || this.tocOpen()
      || this.notesOpen(),
  );

  /**
   * The docked selection bar sits where the pager does, so the pager steps
   * aside while a passage is offered there. The bar lives inside the page's
   * own stacking context and cannot simply be raised above the chrome.
   */
  readonly pagerShown = computed(
    () =>
      this.chromeShown()
      && !(this.pendingSelectionText() !== null && !this.selectionMenuPlacement()),
  );

  handleSurfaceInteraction(): void {
    if (!this.immersiveReader()) return;

    // Selection UI and open tools own the gesture until they are explicitly
    // dismissed; a page tap must never make controls disappear under the user.
    if (
      this.pendingSelectionText() !== null
      || this.searchPanelOpen()
      || this.typoOpen()
      || this.tocOpen()
      || this.notesOpen()
    ) {
      return;
    }

    if (this.chromeVisible()) this.hideImmersiveChrome();
    else this.chromeVisible.set(true);
  }

  /**
   * Remove focus from transient chrome before making it inert. Without this,
   * Escape could strand keyboard focus inside a
   * hidden button even though the book had returned to its resting state.
   */
  private hideImmersiveChrome(): void {
    const active = this.host.nativeElement.ownerDocument.activeElement as HTMLElement | null;
    if (
      active
      && typeof active.closest === 'function'
      && active.closest(
        '.reader-header, .reader-toolbar, .notes-panel, .toc-panel, .typo-panel, .reader-search-panel',
      )
    ) {
      active.blur();
    }
    this.chromeVisible.set(false);
  }

  activeReader = computed<IReader | null>(() => {
    if (!this.ready()) return null;
    switch (this.fileType()) {
      case 'epub':
        return this.epubReader ?? null;
      case 'pdf':
        return this.pdfReader ?? null;
      case 'audio':
        return this.audioReader ?? null;
      default:
        return null;
    }
  });

  toc = computed(() => this.activeReader()?.toc() ?? []);
  progressState = computed(() => this.activeReader()?.progress());
  progressLabel = computed(() => this.progressState()?.label ?? '');
  progressTooltip = computed(() => this.progressState()?.tooltip ?? '');

  nextPage() {
    this.activeReader()?.next();
  }
  prevPage() {
    this.activeReader()?.previous();
  }
  zoomIn() {
    this.activeReader()?.zoomIn();
  }
  zoomOut() {
    this.activeReader()?.zoomOut();
  }

  handleTocClick(item: TocItem) {
    this.activeReader()?.goTo(item.target);
    this.tocOpen.set(false);
    this.restoreOverlayFocus();
  }

  isActive(item: TocItem): boolean {
    const activeTarget = this.activeReader()?.currentLocationTarget?.();
    return activeTarget != null && item.target === activeTarget;
  }

  // --- INITIALIZATION ---

  ngOnInit() {
    // A captured selection remains explicit assistant context while its
    // contextual menu is open. This survives focus moving into Ask Nostos,
    // which otherwise collapses a native PDF selection before the turn is sent.
    this.unregisterSelectionAssistantContext = this.assistantContext.register(
      () => ({ selectedText: this.pendingSelectionText() }),
      { explicit: true },
    );
    this.loadTopics();
    this.watchBookNavigation();
    this.watchGroundedSourceNavigation();
  }

  ngOnDestroy(): void {
    this.placeKeeper.stop();
    if (this.searchDebounce) clearTimeout(this.searchDebounce);
    this.activeReader()?.clearSearch?.();
    this.bookNavigationSubscription?.unsubscribe();
    this.sourceNavigationSubscription?.unsubscribe();
    this.sourceNavigationGeneration++;
    this.bookLoadGeneration++;
    this.pendingGroundedSourceTarget = null;
    this.unregisterSelectionAssistantContext?.();
    this.unregisterSelectionAssistantContext = null;
    if (this.saveFeedbackTimer) clearTimeout(this.saveFeedbackTimer);
    this.dockQuery?.removeEventListener?.('change', this.onDockQueryChange);
    this.veryNarrowQuery?.removeEventListener?.('change', this.onVeryNarrowChange);
    this.phoneQuery?.removeEventListener?.('change', this.onPhoneChange);
  }

  private watchBookNavigation(): void {
    const paramMap = this.route.paramMap;
    if (paramMap?.subscribe) {
      this.bookNavigationSubscription = paramMap.subscribe((params) => {
        const id = params.get('id');
        if (id && id !== this.currentRouteBookId) this.loadBook(id);
      });
      return;
    }

    const id = this.route.snapshot.paramMap.get('id');
    if (id) this.loadBook(id);
    else {
      this.loading.set(false);
      this.loadError.set('This reader link is missing a book.');
    }
  }

  private loadBook(id: string): void {
    // Clear transient format state while the previous mounted reader is still
    // reachable. Once ready=false, activeReader() deliberately disappears.
    this.closeSearch(false);
    this.currentRouteBookId = id;
    const generation = ++this.bookLoadGeneration;

    this.ready.set(false);
    this.loading.set(true);
    this.loadError.set(null);
    this.book.set(null);
    this.dbNotes.set([]);
    this.pendingSelectionText.set(null);
    this.highlightSaving.set(false);
    this.quickNoteSaving.set(false);
    this.quickNotePinnedLocation.set(null);
    this.tocOpen.set(false);
    this.notesOpen.set(false);
    this.typoOpen.set(false);
    this.chromeVisible.set(false);

    // The same locator can be valid for two different books. Reset the source
    // key when the route book changes so a cross-book citation is consumed
    // after the new reader binds rather than being mistaken for a duplicate.
    this.observedGroundedSourceKey = undefined;
    // ActivatedRoute.snapshot is already updated for the navigation when
    // paramMap emits. Reading it here avoids briefly carrying the previous
    // book's source query into the new reader if query params are also changing.
    this.onGroundedSourceParams(this.route.snapshot.queryParamMap);

    this.booksService.get(id).subscribe({
      next: (b) => {
        if (generation !== this.bookLoadGeneration) return;
        this.book.set(b);
        this.loading.set(false);
        this.loadNotes(b.id);
        setTimeout(() => {
          if (generation !== this.bookLoadGeneration) return;
          this.ready.set(true);
          this.navigateGroundedSource(this.sourceNavigationGeneration);
        }, 100);
      },
      error: () => {
        if (generation !== this.bookLoadGeneration) return;
        this.loading.set(false);
        this.ready.set(false);
        this.loadError.set('Nostos could not open this book.');
      },
    });
  }

  retryBookLoad(): void {
    const id = this.currentRouteBookId ?? this.route.snapshot.paramMap.get('id');
    if (id) this.loadBook(id);
  }

  private watchGroundedSourceNavigation(): void {
    // Real Angular routes expose queryParamMap as a live observable. Keep the
    // snapshot fallback for lightweight embedded/test hosts that only provide
    // a minimal ActivatedRoute shape.
    const queryParamMap = this.route.queryParamMap;
    if (queryParamMap?.subscribe) {
      this.sourceNavigationSubscription = queryParamMap.subscribe((params) => {
        this.onGroundedSourceParams(params);
      });
      return;
    }

    this.onGroundedSourceParams(this.route.snapshot.queryParamMap);
  }

  private onGroundedSourceParams(params: ParamMap | null | undefined): void {
    const target = this.parseGroundedSourceTarget(params);
    const sourceNote = params?.get('sourceNote')?.trim().toLowerCase() || null;
    const key = target ? JSON.stringify(target) : sourceNote ? `note:${sourceNote}` : null;

    // Query-param emissions can include unrelated reader state. Re-navigate only
    // when the grounded target itself changes. Clearing the source params resets
    // the observed key, so browser back/forward can consume the same citation
    // again later.
    if (key === this.observedGroundedSourceKey) return;

    this.observedGroundedSourceKey = key;
    this.sourceNavigationGeneration++;
    this.pendingGroundedSourceTarget = target;
    this.pendingSourceNoteId = target ? null : sourceNote;
    if (this.pendingSourceNoteId) this.jumpToSourceNote(this.sourceNavigationGeneration);

    if (target && this.ready()) {
      const generation = this.sourceNavigationGeneration;
      // Route reuse can emit query params just before the new :id. Defer one
      // turn so a cross-book citation never navigates the old mounted reader.
      setTimeout(() => {
        if (generation !== this.sourceNavigationGeneration) return;
        if (!this.isLoadedBookForCurrentRoute()) return;
        this.navigateGroundedSource(generation);
      }, 0);
    }
  }

  private parseGroundedSourceTarget(
    params: ParamMap | null | undefined,
  ): ReaderSourceTarget | null {
    if (!params) return null;

    const sourcePage = Number(params.get('sourcePage'));
    const sourceTimeRaw = params.get('sourceTime');
    const sourceTime = sourceTimeRaw === null ? Number.NaN : Number(sourceTimeRaw);
    const sourceCfi = params.get('sourceCfi');
    const sourceHref = params.get('sourceHref');
    const sourceSpineRaw = params.get('sourceSpine');
    const sourceOffsetRaw = params.get('sourceOffset');
    const sourceExcerpt = params.get('sourceExcerpt');

    if (Number.isFinite(sourceTime) && sourceTime >= 0) {
      return { type: 'audio', audioTime: sourceTime };
    }

    if (Number.isInteger(sourcePage) && sourcePage > 0) {
      return {
        type: 'pdf',
        pdfPage: sourcePage,
        pdfPageLabel: params.get('sourcePageLabel'),
        // Only a landing from another edition carries the passage to mark.
        ...(sourceExcerpt ? { excerpt: sourceExcerpt } : {}),
      };
    }

    if (sourceCfi || sourceHref) {
      const spine = sourceSpineRaw === null ? null : Number(sourceSpineRaw);
      const offset = sourceOffsetRaw === null ? null : Number(sourceOffsetRaw);
      return {
        type: 'epub',
        epubCfi: sourceCfi,
        epubResourceHref: sourceHref,
        epubSpineIndex: Number.isInteger(spine) ? spine : null,
        epubTextOffset: Number.isInteger(offset) && (offset ?? -1) >= 0 ? offset : null,
        excerpt: sourceExcerpt,
        ...(params.get('sourceMark') === 'sentence' ? { markSentence: true } : {}),
      };
    }

    return null;
  }

  private isLoadedBookForCurrentRoute(): boolean {
    const routeBookId = this.currentRouteBookId;
    const loadedBookId = this.book()?.id;
    if (!routeBookId || typeof loadedBookId !== 'string') return false;

    // Keep exact equality for lightweight/test hosts that use synthetic ids,
    // but only relax casing when both values are well-formed GUIDs. This avoids
    // treating arbitrary malformed route text as equivalent book identity.
    if (routeBookId === loadedBookId) return true;

    const guidPattern =
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    if (!guidPattern.test(routeBookId) || !guidPattern.test(loadedBookId)) return false;

    return routeBookId.toLowerCase() === loadedBookId.toLowerCase();
  }

  private navigateGroundedSource(generation: number, attempt = 0): void {
    // A newer query-param target supersedes any delayed retry from an older one.
    if (generation !== this.sourceNavigationGeneration) return;

    const target = this.pendingGroundedSourceTarget;
    if (!target) return;

    // All grounded-navigation entry points, including the initial delayed load,
    // must still belong to the book currently represented by the route.
    if (!this.isLoadedBookForCurrentRoute()) return;

    const reader = this.activeReader();
    if (!reader?.goToSource) {
      if (attempt < 12) {
        setTimeout(() => this.navigateGroundedSource(generation, attempt + 1), 50);
      }
      return;
    }

    this.pendingGroundedSourceTarget = null;
    void reader.goToSource(target);
  }

  loadTopics() {
    this.topicsService.list().subscribe({
      next: (topics) => {
        // Populate service for autocomplete
        this.autocompleteService.setTopics(topics);

        // Populate map for NoteCard display
        const map = new Map<string, TopicDto>();
        topics.forEach((c) => map.set(c.name.trim().toLowerCase(), c));
        this.topicMap.set(map);
      },
    });
  }

  loadNotes(bookId: string) {
    this.notesService.list(bookId).subscribe((notes) => {
      this.dbNotes.set(notes.reverse());
    });
  }

  /** The reader found an imported highlight in the book; keep the list in step. */
  handleNoteLocated(located: Note) {
    this.dbNotes.update((notes) =>
      notes.map((note) => (note.id === located.id ? { ...note, cfiRange: located.cfiRange } : note)),
    );
  }

  /**
   * A link from outside the reader can name a note instead of a location: an
   * imported highlight has none until this reader has found it, and only the
   * open reader can. Waits for both the notes and the reader, then jumps.
   */
  private jumpToSourceNote(generation: number, attempt = 0): void {
    if (generation !== this.sourceNavigationGeneration) return;
    const noteId = this.pendingSourceNoteId;
    if (!noteId) return;

    const reader = this.isLoadedBookForCurrentRoute() ? this.activeReader() : null;
    const note = this.dbNotes().find((candidate) => candidate.id.toLowerCase() === noteId);
    if (!reader || !note) {
      if (attempt < 60) setTimeout(() => this.jumpToSourceNote(generation, attempt + 1), 100);
      return;
    }

    this.pendingSourceNoteId = null;
    this.jumpToNote(reader, note);
  }

  handleNoteCreated() {
    const id = this.book()?.id;
    if (id) this.loadNotes(id);
    // Saving a mark must leave the passage visible. Notes only opens when the
    // reader explicitly asks for it.
    this.pendingSelectionText.set(null);
    this.highlightSaving.set(false);
    this.showSaveFeedback(this.committingNote ? 'Note saved' : 'Highlight saved');
    this.resetSelectionMenu();
  }

  toggleNotes() {
    const opening = !this.notesOpen();
    if (opening) {
      this.closeSearch(false);
      this.rememberOverlayFocus();
      this.tocOpen.set(false);
      this.typoOpen.set(false);
      this.notesOpen.set(true);
      this.focusOverlay('.notes-panel.open .notes-header button');
      return;
    }
    this.notesOpen.set(false);
    this.restoreOverlayFocus();
  }

  /**
   * Adopt the stored pen whenever the open book changes. Written as an effect on
   * `book()` so it holds no matter which path loaded the book.
   */
  setHighlightColour(colour: HighlightColour): void {
    this.highlightColour.set(colour);
    const bookId = this.book()?.id;
    if (bookId) writeHighlightColour(bookId, colour);
  }

  commitHighlight() {
    if (this.highlightSaving()) return;
    this.committingNote = false;
    this.highlightSaving.set(true);
    // The pen is handed over explicitly so the save never depends on the
    // reader's pen input having caught up with a dot tapped a moment ago.
    this.activeReader()?.commitHighlight('', this.highlightColour());
  }

  /** "Add note": reveal the note field in the same menu, at the text. */
  openNoteDraft(): void {
    this.noteDraftOpen.set(true);
    setTimeout(() => {
      this.host.nativeElement
        .querySelector<HTMLTextAreaElement>('.selection-note-input')
        ?.focus({ preventScroll: true });
    }, 0);
  }

  /** Saves the mark together with the typed note, through the same commit. */
  saveNote(): void {
    const content = this.noteDraft().trim();
    if (!content || this.highlightSaving()) return;
    this.committingNote = true;
    this.highlightSaving.set(true);
    this.activeReader()?.commitHighlight(content, this.highlightColour());
  }

  onNoteDraftKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.saveNote();
    }
  }

  /**
   * Copy (#657). A desktop mouse selection now opens the menu on release, and
   * the capture replaces the native selection with the pending mark, so the
   * menu carries the copy action the browser selection used to provide. The
   * passage is copied in full, not the clamped preview; the menu then closes
   * without saving, like any other non-saving choice.
   */
  askNostosAboutSelection(): void {
    if (!this.pendingSelectionText() || this.highlightSaving()) return;
    // Keep the pending selection/menu alive behind the assistant: it remains
    // both visible context and a reversible choice if the reader closes Ask
    // Nostos and decides to Highlight or Add note instead.
    this.assistant.requestSurfaceOpen();
  }

  copySelection(): void {
    const text = this.pendingSelectionText();
    if (!text || this.highlightSaving()) return;
    const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined;
    if (!clipboard) {
      this.toast.error('Copying is not available in this browser.');
      return;
    }
    clipboard.writeText(text).then(
      () => {
        this.toast.success('Passage copied.');
        this.discardHighlight();
      },
      () => this.toast.error('Could not copy the passage.'),
    );
  }

  handleCommitFailed() {
    // Keep the bar open with the pending capture; the failed save must not
    // lose a difficult mobile selection.
    this.highlightSaving.set(false);
  }

  discardHighlight() {
    this.activeReader()?.discardHighlight();
    this.pendingSelectionText.set(null);
    this.highlightSaving.set(false);
    this.resetSelectionMenu();
  }

  handleSelectionCaptured(text: string) {
    // A touch selection is offered again each time its handles settle; that
    // must not throw away a note already being typed for it.
    const replacing = this.pendingSelectionText() !== null;
    this.pendingSelectionText.set(text);
    if (replacing) this.selectionAnchor.set(null);
    else this.resetSelectionMenu();
  }

  /**
   * A tap on the page while the docked bar is open. It drops the offered
   * passage, unless a note is being written or saved for it: a stray tap on
   * the book must not cost the reader their text.
   */
  handleSelectionDismissed(): void {
    if (this.noteDraftOpen() || this.highlightSaving()) return;
    if (this.pendingSelectionText() === null) return;
    this.discardHighlight();
  }

  /**
   * Anchors the menu at the captured selection. On desktop the menu takes
   * focus (without scrolling) so Escape and keyboard actions reach it; the
   * docked phone bar deliberately leaves focus alone.
   */
  handleSelectionAnchored(anchor: SelectionAnchor | null) {
    this.selectionAnchor.set(anchor);
    if (!this.selectionMenuPlacement()) return;
    setTimeout(() => {
      this.host.nativeElement
        .querySelector<HTMLButtonElement>('.selection-menu .selection-primary')
        ?.focus({ preventScroll: true });
    }, 0);
  }

  private resetSelectionMenu(): void {
    this.selectionAnchor.set(null);
    this.noteDraftOpen.set(false);
    this.noteDraft.set('');
  }

  toggleToc() {
    const opening = !this.tocOpen();
    if (!opening) {
      this.tocOpen.set(false);
      this.restoreOverlayFocus();
      return;
    }

    this.closeSearch(false);
    this.rememberOverlayFocus();
    this.notesOpen.set(false);
    this.typoOpen.set(false);
    this.tocOpen.set(true);
    this.focusOverlay('.toc-panel.open .panel-header button');
    if (this.fileType() === 'epub' || this.fileType() === 'audio') {
      setTimeout(() => this.scrollActiveTocItemIntoView(), 0);
    }
  }

  /** The audio player's chapter line opens Contents; it never closes it. */
  openContents() {
    if (!this.tocOpen()) this.toggleToc();
  }

  private scrollActiveTocItemIntoView(): void {
    const active = this.host.nativeElement.querySelector<HTMLElement>(
      '.toc-panel.open .toc-item.active',
    );
    if (active && typeof active.scrollIntoView === 'function') {
      active.scrollIntoView({ block: 'center', inline: 'nearest' });
    }
  }

  // --- NOTES LOGIC ---

  onQuickNoteInput(value: string) {
    this.quickNoteContent.set(value);
    if (!value) {
      this.quickNotePinnedLocation.set(null);
      return;
    }
    // Seeking while drafting keeps the pinned moment: only a draft with no pin
    // takes one.
    if (this.fileType() === 'audio' && this.quickNotePinnedLocation() === null) {
      this.quickNotePinnedLocation.set(this.activeReader()?.getCurrentLocation() ?? null);
    }
  }

  /** The time to show on a note's card: audiobooks only. */
  noteAudioTime(note: Note): number | null {
    return this.fileType() === 'audio' ? noteAudioSeconds(note) : null;
  }

  saveQuickNote() {
    if (this.quickNoteSaving()) return;
    const content = this.quickNoteContent().trim();
    if (!content) return;
    const bookId = this.book()?.id;
    if (!bookId) return;

    const isAudio = this.fileType() === 'audio';
    const location =
      (isAudio ? this.quickNotePinnedLocation() : null) ||
      this.activeReader()?.getCurrentLocation() ||
      undefined;
    const dto: CreateNoteDto = { content, cfiRange: location };
    if (isAudio && location && Number.isFinite(Number(location))) {
      // The reader itself reported this time, so the anchor is verified and
      // Second Brain can link back to the moment. cfiRange stays for the
      // reader's own navigation.
      dto.sourceAnchorKind = 'audio_timestamp';
      dto.sourceAnchorValue = location;
      dto.anchorVerified = true;
    }
    this.quickNoteSaving.set(true);

    this.notesService.create(bookId, dto).subscribe({
      next: () => {
        // Do not erase text typed while the request was in flight.
        if (this.quickNoteContent().trim() === content) {
          this.quickNoteContent.set('');
          this.quickNotePinnedLocation.set(null);
        }
        this.quickNoteSaving.set(false);
        this.loadNotes(bookId);
        this.loadTopics();
        this.showSaveFeedback('Note saved');
      },
      error: () => {
        // The draft stays exactly where it was so a deliberate retry is safe.
        this.quickNoteSaving.set(false);
      },
    });
  }

  // --- HANDLERS FOR NOTE CARD ---

  onUpdateNote(event: {
    id: string;
    content: string;
    selectedText?: string;
    highlightColour?: HighlightColour;
  }) {
    this.notesService
      .update(event.id, {
        content: event.content,
        selectedText: event.selectedText,
        highlightColour: event.highlightColour,
      })
      .subscribe({
        next: (updated) => {
          // Update local state so we see the change immediately without reload
          this.dbNotes.update((notes) => notes.map((n) => (n.id === updated.id ? updated : n)));
          // A changed pen is redrawn on the page now, not at the next open.
          if (event.highlightColour) {
            this.activeReader()?.recolourHighlight?.(updated, event.highlightColour);
          }
        },
      });
  }
  onDeleteNote(noteId: string) {
    this.pendingNoteDelete.set(noteId);
  }

  cancelNoteDelete() {
    this.pendingNoteDelete.set(null);
  }

  confirmNoteDelete() {
    const noteId = this.pendingNoteDelete();
    if (!noteId) return;
    this.pendingNoteDelete.set(null);

    const noteToDelete = this.dbNotes().find((n) => n.id === noteId);

    this.notesService.delete(noteId).subscribe({
      next: () => {
        if (this.fileType() === 'epub' && noteToDelete?.cfiRange)
          this.activeReader()?.removeHighlight(noteToDelete.cfiRange);
        if (this.fileType() === 'pdf') this.activeReader()?.removeHighlight(noteId);

        this.dbNotes.update((notes) => notes.filter((n) => n.id !== noteId));
      },
    });
  }

  onJumpToNote(note: Note) {
    const reader = this.activeReader();
    if (reader && this.jumpToNote(reader, note)) {
      // A full-width phone drawer must not keep hiding the passage the user
      // just asked to reveal. Closing on desktop is also the least surprising
      // destination-jump behavior.
      this.notesOpen.set(false);
      this.restoreOverlayFocus();
    }
  }

  private jumpToNote(reader: IReader, note: Note): boolean {
    const target = noteNavigationTarget(note);
    if (target !== null) {
      if (this.fileType() === 'epub' && typeof target === 'string' && reader.goToSource) {
        // The grounded path holds the target through epub.js's layout settle.
        // A bare display of a CFI in a chapter that is not laid out yet can
        // land a page or two away from the passage.
        void reader.goToSource({ type: 'epub', epubCfi: target });
      } else {
        reader.goTo(target);
      }
      return true;
    }

    // An imported highlight has no location this reader wrote itself. Its
    // text can still be found in an EPUB, and a KOReader page is a real page
    // of a PDF.
    const imported = importedNoteLocation(note);
    if (
      this.fileType() === 'epub' &&
      (imported?.text || imported?.xpointer) &&
      reader.goToPassage
    ) {
      void reader.goToPassage(imported);
      return true;
    }
    if (this.fileType() === 'pdf' && imported?.page) {
      // The page is certain; the passage on it is marked when it can be found.
      if (reader.goToSource) {
        void reader.goToSource({ type: 'pdf', pdfPage: imported.page, excerpt: imported.text });
      } else {
        reader.goTo(imported.page);
      }
      return true;
    }
    return false;
  }

  goBack() {
    // Studio is the one explicit origin that owns a previous-entry return
    // contract. The Reader intentionally knows nothing about editor/caret state:
    // it only goes back to the history entry Studio prepared before leaving.
    if (readReaderReturnOrigin(window.history.state)?.kind === 'studio') {
      this.location.back();
      return;
    }

    const id = this.book()?.id ?? this.currentRouteBookId;
    // Existing behavior for Book Detail, direct Reader entry, and every other
    // origin stays explicit. replaceUrl avoids detail -> reader -> detail ->
    // Back -> reader loops.
    if (id) void this.router.navigate(['/library', id], { replaceUrl: true });
    else void this.router.navigate(['/library'], { replaceUrl: true });
  }

  onPageInput(event: Event) {
    const input = event.target as HTMLInputElement;
    const page = parseInt(input.value, 10);

    // check if it's a valid number and we have a reader
    if (!isNaN(page) && this.activeReader()) {
      this.activeReader()?.goTo(page);
      input.blur(); // Optional: remove focus after jumping
    }
  }

  /**
   * Select the number when the page field takes focus. A jump REPLACES the
   * current page, so without this, typing "5" after "12" reads as "125" (issue
   * the reader chrome polish this change carries).
   */
  onPageFocus(event: Event): void {
    (event.target as HTMLInputElement).select();
  }

  /**
   * Leaving the field without pressing Enter reverts it to the page actually
   * being read. Committing on blur would turn an accidental click or an
   * abandoned edit into a jump; Enter stays the explicit commit, and the box must
   * not keep a number the reader never went to.
   */
  onPageBlur(event: Event): void {
    this.restorePageInput(event);
  }

  /** Escape abandons the edit and leaves the field. */
  revertPageInput(event: Event): void {
    this.restorePageInput(event);
    (event.target as HTMLInputElement).blur();
  }

  private restorePageInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    const current = this.progressState()?.pageNumber;
    if (current) input.value = String(current);
  }

  /**
   * Page keys for the whole reader. The iframe keeps focus inside the book, so
   * the EPUB reader also listens inside its contents document and calls its own
   * next()/previous() — this handler is the path for everything else (toolbar
   * focused, PDF canvas focused, click-anywhere-then-key). Modifier chords and
   * text-entry targets are left alone.
   */
  private rememberOverlayFocus(): void {
    const active = document.activeElement;
    this.overlayReturnFocus = active instanceof HTMLElement ? active : null;
  }

  private focusOverlay(selector: string): void {
    setTimeout(() => {
      this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus();
    }, 0);
  }

  private restoreOverlayFocus(): void {
    const target = this.overlayReturnFocus;
    this.overlayReturnFocus = null;
    if (target?.isConnected) setTimeout(() => target.focus(), 0);
  }

  private showSaveFeedback(message: string): void {
    if (this.saveFeedbackTimer) clearTimeout(this.saveFeedbackTimer);
    this.saveFeedback.set(message);
    this.saveFeedbackTimer = setTimeout(() => {
      this.saveFeedback.set(null);
      this.saveFeedbackTimer = null;
    }, 1800);
  }

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    // Ctrl/Cmd+C while the anchored menu is open copies the captured passage
    // (#657): the native selection it came from has already been replaced by
    // the pending mark. The note field keeps its own copy behaviour.
    if (
      !event.defaultPrevented &&
      (event.ctrlKey || event.metaKey) &&
      !event.altKey &&
      event.key.toLowerCase() === 'c' &&
      this.selectionMenuPlacement() &&
      !isTypingTarget(event.target)
    ) {
      event.preventDefault();
      this.copySelection();
      return;
    }

    if (
      !event.defaultPrevented &&
      (event.ctrlKey || event.metaKey) &&
      !event.altKey &&
      !event.shiftKey &&
      event.key.toLowerCase() === 'f' &&
      this.canSearch()
    ) {
      event.preventDefault();
      this.openSearch();
      return;
    }

    if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;

    if (event.key === 'Escape') {
      // Ask Nostos is app-level chrome above the reader. Let its own Escape
      // handler close first regardless of document-listener registration order;
      // the next Escape may then dismiss the still-pending reader selection.
      if (this.assistant.isOpen()) return;

      if (this.searchPanelOpen()) {
        this.closeSearch();
        event.preventDefault();
        return;
      }
      // Overlays close in the order they stack: the typography panel rides on
      // top of the drawers, so it goes first. defaultPrevented still lets a
      // focused control claim Escape before the shell sees it.
      // The contextual selection surface is the reader's most recent layer and
      // sits above ordinary reader chrome, so dismiss the unsaved mark/note
      // before closing the underlying tools. EPUB may anchor it to the text;
      // PDF uses the docked form, but the keyboard contract is identical.
      if (this.pendingSelectionText() !== null && !this.highlightSaving()) {
        this.discardHighlight();
        event.preventDefault();
        return;
      }
      if (this.typoOpen()) {
        this.typoOpen.set(false);
        this.restoreOverlayFocus();
        event.preventDefault();
        return;
      }
      if (this.tocOpen()) {
        this.tocOpen.set(false);
        this.restoreOverlayFocus();
        event.preventDefault();
        return;
      }
      if (this.notesOpen()) {
        this.notesOpen.set(false);
        this.restoreOverlayFocus();
        event.preventDefault();
        return;
      }
      if (this.immersiveReader() && this.chromeVisible()) {
        this.hideImmersiveChrome();
        event.preventDefault();
        return;
      }
      return;
    }

    // Paging belongs to the resting reading surface. A contextual selection is
    // a higher-priority interaction just like a focused control: page keys must
    // not move the document underneath a pending mark/note action.
    if (
      this.pendingSelectionText() !== null
      || isTypingTarget(event.target)
      || isInteractiveTarget(event.target)
    ) return;

    const action = pageActionForKey(event);
    if (!action) return;

    if (action === 'next') this.nextPage();
    else this.prevPage();
    event.preventDefault();
  }
}
