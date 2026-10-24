import { Component, inject, Injector, OnInit, OnDestroy, signal, computed, effect, untracked, ViewChild, HostListener, ElementRef, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { CommonModule, Location } from '@angular/common';
import { FormsModule } from '@angular/forms';

// Services
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { TopicAutocompleteService } from '../ui/topic-autocomplete-panel/topic-autocomplete.service';

// DTOs & Interfaces
import {
  CreateNoteDto,
  Note,
  formatAudioTime,
  importedNoteLocation,
  noteAudioSeconds,
  noteHasText,
  noteNavigationTarget,
} from '../core/dtos/note.dtos';
import { IReader, ReaderSearchState, ReaderSourceTarget, TocItem } from './reader.interface';
import { isInteractiveTarget, isTypingTarget, pageActionForKey } from './reader-keyboard';
import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HIGHLIGHT_COLOURS,
  HighlightColour,
  noteHighlightColour,
  readHighlightColour,
  writeHighlightColour,
} from './highlight-colours';
import type { HighlightHit } from './highlight-hit';
import { NoteFormatPipe } from '../ui/pipes/note-format.pipe';

// Components
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { ButtonComponent } from '../ui/button/button.component';
import {
  MenuComponent,
  MenuItemComponent,
  MenuSeparatorDirective,
  MenuTriggerDirective,
} from '../ui/menu/menu.component';
import { CdkMenuTrigger } from '@angular/cdk/menu';
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
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import { noteAsItem } from '../ui/note-item/note-item.helpers';
import { BrainNoteActionsService } from '../brain/brain-note-actions.service';
import { BrainNoteDialogsComponent } from '../brain/brain-note-dialogs.component';
import { BrainStateService } from '../brain/brain-state.service';
import { BrainTopicSuggestionsService } from '../brain/brain-topic-suggestions.service';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { InputDirective, TextareaDirective } from '../ui/form-control/form-control.directive';
import { readReaderReturnOrigin } from '../core/navigation/studio-reader-navigation';
import { ThemePickerComponent } from '../ui/theme-picker/theme-picker.component';
import { ToastService } from '../core/services/toast.service';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { NoteSearchHit, Passage, passageReadingMinutes, passageSizeLabel } from '../core/dtos/note.dtos';
import { PassageViewerRequest } from '../ui/passage-viewer/passage-viewer.service';
import {
  PassageDraft,
  PassageMark,
  readPassageMark,
  writePassageMark,
} from './passage/passage-mark';
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

/** Viewport placement of a menu anchored at text; see `ReaderShell.menuPlacement`. */
type MenuPlacement = { left: number; width: number; top: number | null; bottom: number | null };

/** A note's creation time for ordering; an unreadable date sorts first. */
function createdAtOf(note: Note): number {
  const time = Date.parse(note.createdAt);
  return Number.isFinite(time) ? time : 0;
}

import { BrowserVoice } from './read-aloud/browser-voice';
import { ReadAloudBarComponent } from './read-aloud/read-aloud-bar.component';
import { SegmentedComponent } from '../ui/segmented/segmented.component';
import { EPUB_LINE_OPTIONS } from './epub-reader/epub-typography';
import { ReadAloudController } from './read-aloud/read-aloud-controller';
import { ServerVoice } from './read-aloud/server-voice';
import {
  READ_ALOUD_UNAVAILABLE,
  ReadAloudService,
  ReadAloudStatus,
} from '../core/services/read-aloud.service';
import { firstValueFrom } from 'rxjs';
import { SearchFieldComponent } from '../ui/search-field/search-field.component';

const READ_ALOUD_VOICE_KEY = 'nostos.readAloud.voice';

function readStoredReadAloudVoice(): string | null {
  try {
    return localStorage.getItem(READ_ALOUD_VOICE_KEY);
  } catch {
    return null;
  }
}

/** Why no natural voice is offered for the open book, before reading starts. */
function readAloudUnavailableNote(reason: string | null, managed: boolean): string {
  switch (reason) {
    case 'read_aloud_access_denied':
      return 'Natural voices are not included in your plan.';
    case 'read_aloud_unsupported_language':
      return 'No natural voice reads this book’s language yet.';
    case 'read_aloud_not_configured':
    case 'read_aloud_disabled':
      return managed
        ? 'The natural voice is not available right now.'
        : 'No speech server is connected. Add one in Settings → AI connections.';
    default:
      return 'The natural voice is not available right now.';
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
    SearchFieldComponent,
    CommonModule,
    FormsModule,
    NostosIconComponent,
    PdfReader,
    EpubReader,
    AudioReader,
    SwitchEditionDialog,
    TopicInputComponent,
    NoteItemComponent,
    BrainNoteDialogsComponent,
    IconButtonComponent,
    ButtonComponent,
    ConfirmModal,
    InputDirective,
    TextareaDirective,
    ReadAloudBarComponent,
    SegmentedComponent,
    NoteFormatPipe,
    MenuComponent,
    MenuItemComponent,
    MenuSeparatorDirective,
    MenuTriggerDirective,
    ModalShell,
    ThemePickerComponent,
  ],
  providers: [ReadAloudController, BrainNoteActionsService, BrainTopicSuggestionsService],
  templateUrl: './reader-shell.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './reader-shell.component.css',
})
export class ReaderShell implements OnInit, OnDestroy {
  constructor() {
    // The notes panel shows each note as the Brain does, topics included; its
    // topic changes, Keep with writing and restores go through the Brain's own
    // note actions. Edits and deletes stay here: they repaint the page.
    this.brain.ensureLoaded();
    // The [[ autocomplete offers the same topics, kept current by every change.
    effect(() => this.autocompleteService.setTopics(this.brain.topics()));
    this.noteActions.attach({
      replace: (hit) => this.replaceNoteFromHit(hit),
      remove: (noteId) => this.dbNotes.update((notes) => notes.filter((note) => note.id !== noteId)),
    });

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
    // A passage end marked earlier in this book is still waiting for its pair.
    this.passageMark.set(readPassageMark(bookId));
    });

    // A page turn moves the text away from an anchored selection menu (#650).
    // Rather than float over the wrong words, the menu docks to the bottom
    // edge; the pending mark and any typed note are kept.
    effect(() => {
      this.progressState()?.label;
      untracked(() => {
        if (this.selectionAnchor()) this.selectionAnchor.set(null);
        // The highlight card is about a passage that has just left the page.
        // A note being written for it is kept, docked; otherwise it closes.
        const card = this.highlightCard();
        if (card && this.highlightNoteEditing()) {
          if (card.anchor) this.highlightCard.set({ ...card, anchor: null });
        } else if (card) {
          this.closeHighlightCard();
        }
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
  selectionMenuPlacement = computed<MenuPlacement | null>(() =>
    this.menuPlacement(this.selectionAnchor()),
  );

  private menuPlacement(anchor: SelectionAnchor | null): MenuPlacement | null {
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
  }

  // ------------------------------------------------------------------
  // Highlight card: a saved highlight, opened from the page
  // ------------------------------------------------------------------

  /**
   * The saved highlight a click or tap opened, and where it sits on screen.
   * Desktop anchors the card at the mark like the selection menu; phones and
   * touch-first devices dock it at the bottom, clear of the page.
   */
  readonly highlightCard = signal<{ noteId: string; anchor: SelectionAnchor | null } | null>(null);
  readonly highlightNoteEditing = signal(false);
  readonly highlightNoteDraft = signal('');
  readonly highlightNoteSaving = signal(false);

  /** The opened highlight's note, kept current with the notes list; null closes the card. */
  readonly activeHighlight = computed<Note | null>(() => {
    const card = this.highlightCard();
    if (!card || this.pendingSelectionText() !== null) return null;
    return this.dbNotes().find((note) => note.id === card.noteId) ?? null;
  });
  readonly activeHighlightHasNote = computed(() => {
    const note = this.activeHighlight();
    return !!note && noteHasText(note);
  });
  readonly activeHighlightPen = computed<HighlightColour>(() =>
    noteHighlightColour(this.activeHighlight() ?? {}),
  );
  readonly activeHighlightPreview = computed(() =>
    selectionPreview(this.activeHighlight()?.selectedText ?? null),
  );
  readonly highlightCardPlacement = computed<MenuPlacement | null>(() =>
    this.activeHighlight() ? this.menuPlacement(this.highlightCard()?.anchor ?? null) : null,
  );
  /** The note-list card just pointed at from the highlight card. */
  readonly flashedNoteId = signal<string | null>(null);
  private flashTimer: ReturnType<typeof setTimeout> | null = null;

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
  readonly brain = inject(BrainStateService);
  readonly noteActions = inject(BrainNoteActionsService);
  readonly suggestions = inject(BrainTopicSuggestionsService);
  private autocompleteService = inject(TopicAutocompleteService);

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

  /**
   * Feedback opens in a new tab, from the More menu. It is offered even when
   * the book failed to load, so it cannot depend on a reader being mounted.
   */
  openFeedback(href: string): void {
    window.open(href, '_blank', 'noopener,noreferrer');
  }

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

  /** The More menu names the edition it opens when there is just one. */
  readonly switchEditionLabel = computed(() => {
    if (this.switchableEditions().length === 1) {
      if (this.switchEditionIcon() === 'headphones') return 'Continue in the audiobook';
      if (this.switchEditionIcon() === 'book-open') return 'Continue in the ebook';
    }
    return 'Continue in another edition';
  });

  // --- More menu ---

  /**
   * The header's fourth tool. It holds what is reached for once in a session,
   * so the header stays four tools at every width instead of growing a button
   * per feature.
   */
  @ViewChild('moreTrigger', { read: CdkMenuTrigger }) private moreTrigger?: CdkMenuTrigger;
  @ViewChild('moreTrigger', { read: ElementRef }) private moreButton?: ElementRef<HTMLButtonElement>;
  readonly moreOpen = signal(false);

  /** Search and read aloud: the More items that need an open EPUB or PDF. */
  readonly moreHasReadingTools = computed(
    () => !this.loadError() && (this.fileType() === 'epub' || this.fileType() === 'pdf'),
  );

  readonly hasMoreTools = computed(
    () =>
      this.moreHasReadingTools() ||
      this.canSwitchEdition() ||
      !!this.feedbackUrl() ||
      this.canMarkPassageHere(),
  );

  /**
   * Runs a More item from its trigger rather than from the item, which is gone
   * once the menu closes: a panel it opens then returns focus to More. It runs
   * inside the tap, which read aloud needs to start audio on a phone.
   */
  fromMoreMenu(action: () => unknown): void {
    this.moreTrigger?.close();
    this.moreButton?.nativeElement.focus();
    void action.call(this);
  }

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
  /** What the voice menu shows as selected. */
  readonly readAloudSelectedVoice = computed(() =>
    this.readAloudVoiceChoice() === 'device' ? 'device' : (this.readAloudServerVoiceId() ?? 'device'),
  );

  /**
   * Why the natural voice is not offered for this book, or why none is chosen
   * for it, in the person's terms; null when there is nothing to explain.
   */
  readonly readAloudVoiceNote = computed<string | null>(() => {
    const server = this.readAloudServer();
    if (!server) return null;
    if (server.available) {
      return server.reason === 'read_aloud_language_unknown' && !this.readAloudServerVoiceId()
        ? 'This book does not say its language. Choose a natural voice that speaks it.'
        : null;
    }
    return readAloudUnavailableNote(server.reason ?? null, server.managed);
  });

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
    this.serverVoice.forget();
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
    // A book that does not say its language has no default voice: until one
    // is chosen, the device reads it.
    const useServer =
      server.available && this.readAloudVoiceChoice() !== 'device' && this.readAloudServerVoiceId() !== null;
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

  /** Line heights as segmented options; the label is the value at one decimal. */
  readonly lineHeightOptions = EPUB_LINE_OPTIONS.map((value) => ({ value, label: value.toFixed(1) }));

  /** The PDF page-colour choice, as the two values the segmented control emits. */
  readonly pageColourOptions = [
    { value: 'inverted', label: 'Inverted' },
    { value: 'printed', label: 'As printed' },
  ] as const;

  toggleTypo(): void {
    const opening = !this.typoOpen();
    if (opening) {
      this.closeSearch(false);
      this.dismissHighlightCardForTool();
      this.rememberOverlayFocus();
      this.tocOpen.set(false);
      this.notesOpen.set(false);
      this.typoOpen.set(true);
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
      this.dismissHighlightCardForTool();
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

  /** The preset currently in effect, or null when the zoom is a free value. */
  pdfZoomPreset(): string | number | null {
    return this.pdfZoomPresets().find((preset) => this.isZoomPreset(preset.value))?.value ?? null;
  }

  pdfScrollMode(): ScrollModeType | null {
    return this.pdfReader?.scrollMode() ?? null;
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
  /**
   * Each note's topics, by note id. The reader's own list of notes does not
   * carry them, so they are read from the Brain's listing of this book.
   */
  readonly noteTopicNames = signal<ReadonlyMap<string, readonly string[]>>(new Map());
  /** The notes panel's rows: each note, and the same note as its item shows it. */
  readonly noteRows = computed(() => {
    const topics = this.noteTopicNames();
    return this.dbNotes().map((note) => ({ note, item: noteAsItem(note, topics.get(note.id)) }));
  });
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
  /** The topics by lower-cased name, for [[ ]] links in notes (the Brain's own). */
  readonly topicMap = computed(() => this.brain.lookup());

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
      || this.notesOpen()
      || this.moreOpen(),
  );

  /**
   * The docked selection bar sits where the pager does, so the pager steps
   * aside while a passage is offered there. The bar lives inside the page's
   * own stacking context and cannot simply be raised above the chrome.
   */
  readonly pagerShown = computed(
    () =>
      this.chromeShown()
      && !(this.pendingSelectionText() !== null && !this.selectionMenuPlacement())
      && !(this.activeHighlight() !== null && !this.highlightCardPlacement()),
  );

  handleSurfaceInteraction(): void {
    if (!this.immersiveReader()) return;

    // A tap away from an open highlight card closes it, and does nothing else.
    if (this.highlightCard()) {
      if (!this.highlightNoteEditing() && !this.highlightNoteSaving()) this.closeHighlightCard();
      return;
    }

    // A tap on the page dismisses an open More menu, and does nothing else.
    // The CDK cannot hear a tap inside the EPUB's iframe.
    if (this.moreOpen()) {
      this.moreTrigger?.close();
      return;
    }

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
        '.reader-header, .reader-toolbar, .notes-panel, .toc-panel, .typo-panel, .reader-search-panel, .read-aloud-bar',
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
      () => ({
        selectedText: this.pendingSelectionText() ?? this.activeHighlight()?.selectedText ?? null,
      }),
      { explicit: true },
    );
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
    if (this.flashTimer) clearTimeout(this.flashTimer);
    this.dockQuery?.removeEventListener?.('change', this.onDockQueryChange);
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
    this.closeHighlightCard();
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

  /** The topics changed (a note was saved): read them again. */
  loadTopics() {
    this.brain.refresh();
  }

  // ------------------------------------------------------------------
  // Passages (issue #1014): two marks in either order, then a save sheet.
  // ------------------------------------------------------------------

  /** One end of a passage, waiting for the other. Kept per book across visits. */
  readonly passageMark = signal<PassageMark | null>(null);
  readonly passageSheetOpen = signal(false);
  readonly passageDraft = signal<PassageDraft | null>(null);
  readonly passageBuilding = signal(false);
  readonly passageBuildFailed = signal(false);
  readonly passageSaving = signal(false);
  readonly passageTitle = signal('');
  readonly passageNote = signal('');
  private passageBuildSeq = 0;
  readonly readingMinutes = passageReadingMinutes;
  readonly formatAudioTime = formatAudioTime;

  /** The draft's size as its card will show it: words, pages or a time range. */
  readonly passageDraftSize = computed(() => {
    const draft = this.passageDraft();
    return draft
      ? passageSizeLabel({
          kind: 'passage',
          sourceAnchorKind: draft.anchorKind,
          sourceAnchorValue: draft.start,
          passageEnd: draft.end,
          passageWordCount: draft.words,
        })
      : '';
  });

  /** A clip's ends move by 15 seconds on the save sheet, never past each other. */
  nudgePassage(which: 'start' | 'end', delta: number): void {
    const draft = this.passageDraft();
    if (!draft || draft.anchorKind !== 'audio_timestamp') return;
    let start = Number(draft.start);
    let end = Number(draft.end);
    if (which === 'start') start = Math.min(end - 1, Math.max(0, start + delta));
    else end = Math.min(draft.limit ?? Infinity, Math.max(start + 1, end + delta));
    this.passageDraft.set({
      ...draft,
      start: String(start),
      end: String(end),
      opening: formatAudioTime(start),
      closing: formatAudioTime(end),
    });
  }

  /** The reader in use can mark passages. */
  readonly canMarkPassage = computed(() => !!this.activeReader()?.passageMarkFromSelection);
  /** ...and can mark one where it stands, with nothing selected (PDF page, audio time). */
  readonly canMarkPassageHere = computed(() => !!this.activeReader()?.passageMarkHere);

  /** The passage viewer, opened from this reader's notes, turns this book. */
  readonly passageViewerOptions: Omit<PassageViewerRequest, 'noteId'> = {
    openInBook: (passage: Passage) => {
      this.notesOpen.set(false);
      // A PDF turns to a page number; a CFI or a time stays a string.
      const page = passage.anchorKind === 'pdf_page' ? Number(passage.start) : NaN;
      this.activeReader()?.goTo(Number.isInteger(page) && page > 0 ? page : passage.start);
    },
  };

  /**
   * "Mark passage" on a selection. The first mark is kept and the reader reads
   * on (or turns back); the second, anywhere in the book, opens the save sheet
   * with the passage between them, whichever came first.
   */
  markPassage(): void {
    const mark = this.activeReader()?.passageMarkFromSelection?.() ?? null;
    if (!mark) return;
    this.discardHighlight();
    this.takePassageMark(mark);
  }

  /** "Mark passage here" from More: the page or time the reader is at. */
  markPassageHere(): void {
    const mark = this.activeReader()?.passageMarkHere?.() ?? null;
    if (mark) this.takePassageMark(mark);
  }

  private takePassageMark(mark: PassageMark): void {
    const bookId = this.book()?.id;
    if (!bookId) return;

    const first = this.passageMark();
    if (!first || first.format !== mark.format) {
      this.passageMark.set(mark);
      writePassageMark(bookId, mark);
      this.showSaveFeedback('Passage started');
      return;
    }
    void this.openPassageSheet(first, mark);
  }

  /** Turn to the waiting mark, to see where the passage starts or ends. */
  goToPassageMark(): void {
    const mark = this.passageMark();
    if (mark) this.activeReader()?.goTo(mark.start);
  }

  cancelPassageMark(): void {
    const bookId = this.book()?.id;
    this.passageMark.set(null);
    if (bookId) writePassageMark(bookId, null);
  }

  private async openPassageSheet(a: PassageMark, b: PassageMark): Promise<void> {
    const seq = ++this.passageBuildSeq;
    this.passageDraft.set(null);
    this.passageBuildFailed.set(false);
    this.passageBuilding.set(true);
    this.passageTitle.set('');
    this.passageNote.set('');
    this.passageSheetOpen.set(true);

    let draft: PassageDraft | null = null;
    try {
      draft = (await this.activeReader()?.buildPassage?.(a, b)) ?? null;
    } catch (error) {
      console.warn('Could not gather the passage:', error);
    }
    if (seq !== this.passageBuildSeq) return;
    this.passageBuilding.set(false);
    if (!draft) {
      this.passageBuildFailed.set(true);
      return;
    }
    this.passageDraft.set(draft);
    this.passageTitle.set(draft.title);
  }

  closePassageSheet(): void {
    if (this.passageSaving()) return;
    this.passageBuildSeq++;
    this.passageSheetOpen.set(false);
    this.passageDraft.set(null);
    this.passageBuilding.set(false);
  }

  savePassage(): void {
    const bookId = this.book()?.id;
    const draft = this.passageDraft();
    const title = this.passageTitle().trim();
    if (!bookId || !draft || !title || this.passageSaving()) return;

    this.passageSaving.set(true);
    this.notesService
      .create(bookId, {
        content: this.passageNote().trim(),
        kind: 'passage',
        passageTitle: title,
        sourceAnchorKind: draft.anchorKind,
        sourceAnchorValue: draft.start,
        passageEnd: draft.end,
        selectedText: draft.text ?? undefined,
        passageHtml: draft.html ?? undefined,
        highlightColour: this.highlightColour(),
      })
      .subscribe({
        next: () => {
          this.passageSaving.set(false);
          this.cancelPassageMark();
          this.closePassageSheet();
          this.loadNotes(bookId);
          this.showSaveFeedback('Passage saved');
        },
        error: () => {
          this.passageSaving.set(false);
          this.toast.error('Could not save the passage.');
        },
      });
  }

  loadNotes(bookId: string) {
    this.notesService.list(bookId).subscribe((notes) => {
      this.dbNotes.set(notes.reverse());
    });
    this.loadNoteTopics(bookId);
  }

  private noteTopicsSeq = 0;

  /** Every note's topics in this book, read 200 at a time until all are in. */
  private loadNoteTopics(bookId: string): void {
    const seq = ++this.noteTopicsSeq;
    const page = (held: Map<string, readonly string[]>, offset: number) =>
      this.notesService.browse({ bookId, limit: 200, offset }).subscribe({
        next: (result) => {
          if (seq !== this.noteTopicsSeq) return;
          result.items.forEach((hit) => held.set(hit.id, hit.topicNames ?? []));
          const read = offset + result.items.length;
          if (result.items.length && read < result.totalCount) page(held, read);
          else this.noteTopicNames.set(held);
        },
      });
    page(new Map(), 0);
  }

  /** A note as the Brain's actions now have it: its topics and its words. */
  private replaceNoteFromHit(hit: NoteSearchHit): void {
    this.noteTopicNames.update((current) => new Map(current).set(hit.id, hit.topicNames ?? []));
    this.dbNotes.update((notes) =>
      notes.map((note) => (note.id === hit.id ? { ...note, content: hit.content } : note)),
    );
  }

  /** A [[link]] written in a note links a topic: show the note's topics as saved. */
  private refreshNoteTopics(noteId: string): void {
    this.notesService.get(noteId).subscribe({
      next: (hit) =>
        this.noteTopicNames.update((current) => new Map(current).set(hit.id, hit.topicNames ?? [])),
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
      this.dismissHighlightCardForTool();
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
    // A new selection is the reader's next move; the card it began from closes.
    this.closeHighlightCard();
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

  /**
   * A click or tap landed on saved highlights. Overlapping marks open the
   * newest, the one drawn on top. Tapping the open highlight again closes it.
   * A pending selection owns the page until it is saved or dropped.
   */
  handleHighlightActivated(hits: HighlightHit[]): void {
    if (this.pendingSelectionText() !== null || this.highlightSaving()) return;
    if (this.highlightNoteEditing() || this.highlightNoteSaving()) return;

    const byId = this.fileType() === 'pdf';
    let best: { note: Note; anchor: HighlightHit['anchor'] } | null = null;
    for (const hit of hits) {
      const note = this.dbNotes().find((candidate) =>
        byId ? candidate.id === hit.identifier : candidate.cfiRange === hit.identifier,
      );
      if (!note) continue;
      if (!best || createdAtOf(note) > createdAtOf(best.note)) best = { note, anchor: hit.anchor };
    }
    if (!best) return;

    if (this.highlightCard()?.noteId === best.note.id) {
      this.closeHighlightCard();
      return;
    }

    this.typoOpen.set(false);
    this.highlightNoteEditing.set(false);
    this.highlightNoteDraft.set('');
    this.highlightCard.set({ noteId: best.note.id, anchor: best.anchor });

    // Desktop: focus moves into the card so Escape and the keyboard reach it.
    // A phone keeps focus where it was, as the docked selection bar does, and
    // a save notice still showing would sit on the docked card's actions.
    if (!this.highlightCardPlacement()) {
      this.clearSaveFeedback();
      return;
    }
    setTimeout(() => {
      this.host.nativeElement
        .querySelector<HTMLElement>('.highlight-card [data-autofocus]')
        ?.focus({ preventScroll: true });
    }, 0);
  }

  /**
   * A reader tool opened over the page: the card steps aside for it, unless a
   * note is being written in it, which is never thrown away unasked.
   */
  private dismissHighlightCardForTool(): void {
    if (this.highlightCard() && !this.highlightNoteEditing() && !this.highlightNoteSaving()) {
      this.closeHighlightCard();
    }
  }

  closeHighlightCard(): void {
    this.highlightCard.set(null);
    this.highlightNoteEditing.set(false);
    this.highlightNoteDraft.set('');
    this.highlightNoteSaving.set(false);
  }

  /** "Add note" / "Edit note": the note opens for writing in the card itself. */
  editHighlightNote(): void {
    const note = this.activeHighlight();
    if (!note) return;
    this.highlightNoteDraft.set(note.content ?? '');
    this.highlightNoteEditing.set(true);
    setTimeout(() => {
      const input = this.host.nativeElement.querySelector<HTMLTextAreaElement>('.highlight-card-input');
      input?.focus({ preventScroll: true });
      input?.setSelectionRange(input.value.length, input.value.length);
    }, 0);
  }

  cancelHighlightNoteEdit(): void {
    if (this.highlightNoteSaving()) return;
    this.highlightNoteEditing.set(false);
    this.highlightNoteDraft.set('');
  }

  /** True when Save would change the note: adding, editing or clearing it. */
  readonly highlightNoteChanged = computed(
    () => this.highlightNoteDraft().trim() !== (this.activeHighlight()?.content ?? '').trim(),
  );

  /**
   * Saves the note on the opened highlight. Clearing the text keeps the
   * highlight and drops only its note. The passage and pen are left as they are.
   */
  saveHighlightNote(): void {
    const note = this.activeHighlight();
    if (!note || this.highlightNoteSaving() || !this.highlightNoteChanged()) return;
    const content = this.highlightNoteDraft().trim();
    this.highlightNoteSaving.set(true);

    this.notesService.update(note.id, { content }).subscribe({
      next: (updated) => {
        this.dbNotes.update((notes) => notes.map((n) => (n.id === updated.id ? updated : n)));
        this.refreshNoteTopics(updated.id);
        // The dotted rule under the mark follows whether it now has a note.
        this.activeReader()?.recolourHighlight?.(updated, noteHighlightColour(updated));
        this.highlightNoteSaving.set(false);
        this.highlightNoteEditing.set(false);
        this.highlightNoteDraft.set('');
        // Docked, the card shows the saved note in the notice's place; a
        // notice there would cover the card's own actions.
        if (this.highlightCardPlacement()) this.showSaveFeedback(content ? 'Note saved' : 'Note removed');
        this.loadTopics();
      },
      error: () => {
        // The draft stays exactly as typed so a retry is one click.
        this.highlightNoteSaving.set(false);
        this.toast.error('Could not save the note.');
      },
    });
  }

  onHighlightNoteKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.saveHighlightNote();
    }
  }

  /** A pen dot in the card recolours the highlight straight away. */
  recolourActiveHighlight(colour: HighlightColour): void {
    const note = this.activeHighlight();
    if (!note || colour === this.activeHighlightPen()) return;
    this.onUpdateNote({ id: note.id, content: note.content ?? '', highlightColour: colour });
  }

  copyActiveHighlight(): void {
    const text = this.activeHighlight()?.selectedText;
    if (!text) return;
    const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined;
    if (!clipboard) {
      this.toast.error('Copying is not available in this browser.');
      return;
    }
    clipboard.writeText(text).then(
      () => this.toast.success('Passage copied.'),
      () => this.toast.error('Could not copy the passage.'),
    );
  }

  /** The highlight stays open behind Ask Nostos and is its context. */
  askNostosAboutHighlight(): void {
    if (!this.activeHighlight()) return;
    this.assistant.requestSurfaceOpen();
  }

  deleteActiveHighlight(): void {
    const note = this.activeHighlight();
    if (note) this.onDeleteNote(note.id);
  }

  /** Opens Notes & Highlights at this highlight's card, briefly marked. */
  showActiveHighlightInNotes(): void {
    const note = this.activeHighlight();
    if (!note) return;
    this.closeHighlightCard();
    if (!this.notesOpen()) this.toggleNotes();

    if (this.flashTimer) clearTimeout(this.flashTimer);
    this.flashedNoteId.set(note.id);
    this.flashTimer = setTimeout(() => {
      this.flashedNoteId.set(null);
      this.flashTimer = null;
    }, 2400);
    setTimeout(() => {
      const card = Array.from(
        this.host.nativeElement.querySelectorAll<HTMLElement>('.notes-panel [data-note-id]'),
      ).find((candidate) => candidate.getAttribute('data-note-id') === note.id);
      card?.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
    }, 0);
  }

  toggleToc() {
    const opening = !this.tocOpen();
    if (!opening) {
      this.tocOpen.set(false);
      this.restoreOverlayFocus();
      return;
    }

    this.closeSearch(false);
    this.dismissHighlightCardForTool();
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
    passageTitle?: string;
  }) {
    this.notesService
      .update(event.id, {
        content: event.content,
        selectedText: event.selectedText,
        highlightColour: event.highlightColour,
        passageTitle: event.passageTitle,
      })
      .subscribe({
        next: (updated) => {
          // Update local state so we see the change immediately without reload
          this.dbNotes.update((notes) => notes.map((n) => (n.id === updated.id ? updated : n)));
          this.refreshNoteTopics(updated.id);
          this.loadTopics();
          // A changed pen, or a note added to or cleared from a highlight, is
          // redrawn on the page now, not at the next open.
          if (updated.cfiRange) {
            this.activeReader()?.recolourHighlight?.(
              updated,
              event.highlightColour ?? noteHighlightColour(updated),
            );
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
        if (this.highlightCard()?.noteId === noteId) this.closeHighlightCard();
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

  private clearSaveFeedback(): void {
    if (this.saveFeedbackTimer) clearTimeout(this.saveFeedbackTimer);
    this.saveFeedbackTimer = null;
    this.saveFeedback.set(null);
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
      // The highlight card is the same kind of layer: Escape leaves the note
      // being written first, then the card.
      if (this.highlightCard() && !this.highlightNoteSaving()) {
        if (this.highlightNoteEditing()) this.cancelHighlightNoteEdit();
        else this.closeHighlightCard();
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
