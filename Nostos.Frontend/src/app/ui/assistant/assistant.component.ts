import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  Injector,
  afterNextRender,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';

import { isPhoneViewport } from '../breakpoints';
import { ButtonComponent } from '../button/button.component';
import { Router } from '@angular/router';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import {
  AssistantEvidenceReferenceDto,
  AssistantService,
  AssistantSourceReferenceDto,
} from './assistant.service';
import { AssistantVoiceService } from './assistant-voice.service';
import type { AssistantContext } from './assistant-context.service';
import { AssistantStatusService } from './assistant-status.service';
import { AssistantAllowanceService } from './assistant-allowance.service';
import type { AssistantFailureAction } from './assistant-failure';
import { canOpenEvidence } from './assistant-sources';
import { IconTileComponent } from '../icon-tile/icon-tile.component';
import { ASSISTANT_LAUNCHER_ATTR, AssistantLauncherService } from './assistant-launcher.service';
import { AssistantTranscriptComponent } from './assistant-transcript.component';
import { AssistantComposerComponent } from './assistant-composer.component';

/**
 * One ready-made request under a capability door. Clicking it places
 * <see cref="fill"/> in the composer; the person still sends it themselves.
 */
export interface CapabilityExample {
  id: string;
  label: string;
  /** Text placed in the composer. Defaults to the label. */
  fill?: string;
}

/**
 * One of the three jobs Ask Nostos does — ask, capture, organise — with
 * examples that name a capability the registry really offers.
 */
export interface CapabilityDoor {
  id: 'ask' | 'capture' | 'organise';
  label: string;
  examples: CapabilityExample[];
}

/**
 * Capability doors for the page the reader is on. They are grouped by job, and
 * every example is worded for the surface: about this book in the reader,
 * about connections in the Brain, about a draft in the Studio. Organising the
 * library, the To read list and Studio drafts appear everywhere, because the
 * app otherwise never mentions them.
 */
export function capabilityDoors(context: AssistantContext): CapabilityDoor[] {
  const book = context.bookTitle?.trim();

  let ask: CapabilityExample[];
  switch (context.surface) {
    case 'reader':
      ask = [
        ...(context.selectedText?.trim()
          ? [{ id: 'explain-selection', label: 'Explain the passage I selected' }]
          : []),
        {
          id: 'reader-summary',
          label: book
            ? `Summarise what I have read of ${book} so far`
            : 'Summarise what I have read so far',
        },
        {
          id: 'reader-notes',
          label: book ? `What have I noted from ${book}?` : 'What have I noted from this book?',
        },
      ];
      break;
    case 'book-detail':
      ask = [
        { id: 'book-about', label: book ? `What is ${book} about?` : 'What is this book about?' },
        {
          id: 'book-notes',
          label: book ? `What have I noted from ${book}?` : 'What have I noted from this book?',
        },
        { id: 'book-related', label: 'Which of my other books connect to this one?' },
      ];
      break;
    case 'brain':
      ask = [
        { id: 'brain-connections', label: 'What connects my topics?' },
        { id: 'brain-recent', label: 'Summarise my most recent notes' },
        { id: 'brain-gaps', label: 'Which topics have only one or two notes?' },
      ];
      break;
    case 'studio':
      ask = [
        { id: 'studio-sources', label: 'Which of my notes could support what I am writing?' },
        { id: 'studio-drafts', label: 'Which Studio drafts am I working on?' },
      ];
      break;
    default:
      ask = [
        { id: 'library-next', label: 'What should I read next from my library?' },
        { id: 'library-recent', label: 'What have I been reading lately?' },
        { id: 'library-topics', label: 'Which topics come up most in my notes?' },
      ];
  }

  // A Brain review already has the note; proposing topics is the job there.
  // Everywhere else, point at the Studio documents users never hear about.
  const organiseExample: CapabilityExample = context.brainReviewNoteId
    ? { id: 'organise-topics', label: 'Suggest topics for this note' }
    : { id: 'organise-studio', label: 'Open my Studio draft about memory' };

  return [
    { id: 'ask', label: 'Ask', examples: ask },
    {
      id: 'capture',
      label: 'Capture',
      examples: [
        { id: 'capture-thought', label: 'Save this thought:', fill: 'Save this thought: ' },
        { id: 'capture-quote', label: 'Save this quote:', fill: 'Save this quote: ' },
      ],
    },
    {
      id: 'organise',
      label: 'Organise',
      examples: [
        { id: 'organise-collection', label: 'Move my Le Guin books into a collection' },
        { id: 'organise-to-read', label: 'Add Piranesi to To read' },
        organiseExample,
      ],
    },
  ];
}

/**
 * How close to the end of the transcript still counts as "reading the newest
 * turn". Comfortably above sub-pixel rounding (a scroll position is an integer,
 * line heights are not) and below one line of text, so a deliberate scroll back
 * to an older turn always registers as one.
 */
const FOLLOW_THRESHOLD_PX = 32;

/** True when the OS asks for reduced motion; the transcript then jumps, never glides. */
function prefersReducedMotion(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true
  );
}

/**
 * App-wide assistant shell (issue #261 §1, §2, §4 capture; #262 voice).
 *
 * One root-level component: a quiet collapsed capsule that opens a compact
 * capture/conversation surface. It is surface-aware — the collapsed trigger
 * moves out of the reader's text column on phones — and it never steals focus
 * while closed. No LLM: capture and push-to-talk voice only.
 *
 * The shell owns geometry, focus restoration and transcript follow state. The
 * transcript entries and the composer are their own OnPush children; both read
 * the one `AssistantService` conversation.
 *
 * The transcript follows the newest turn while the reader is on it (issue #300)
 * and leaves a view they have scrolled back on alone; see `following`.
 *
 * The microphone lives in the composer of the OPEN surface (one tap on the
 * trigger, then the mic). That placement works in every layout, including the
 * icon-only mobile reader variant, and deliberately does not touch the collapsed
 * capsule whose 154px width was measured to cover the page-turn control.
 */
@Component({
  selector: 'app-assistant',
  standalone: true,
  imports: [
    IconTileComponent,
    NostosIconComponent,
    ButtonComponent,
    IconButtonComponent,
    AssistantTranscriptComponent,
    AssistantComposerComponent,
  ],
  templateUrl: './assistant.component.html',
  styleUrl: './assistant.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.is-open]': 'assistant.isOpen()',
  },
})
export class AssistantComponent {
  readonly assistant = inject(AssistantService);
  readonly voice = inject(AssistantVoiceService);
  readonly allowance = inject(AssistantAllowanceService);
  private readonly status = inject(AssistantStatusService);
  private readonly router = inject(Router, { optional: true });
  private readonly launcher = inject(AssistantLauncherService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly composer = viewChild(AssistantComposerComponent);
  /** The transcript's scroll container. */
  private readonly body = viewChild<ElementRef<HTMLElement>>('body');
  /** The content whose post-layout growth can move the compact pane's end. */
  private readonly transcript = viewChild<AssistantTranscriptComponent>('transcript');
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  /**
   * Whether new content carries the view with it (issue #300).
   *
   * True means the reader is on the newest turn, so a reply is kept visible
   * without a manual scroll. It turns false the moment they scroll back to read
   * an older turn: an arriving reply then changes nothing until they return to
   * the end themselves, which re-arms it. Their OWN message is the deliberate
   * exception — sending returns to the end, because the reply to it is the
   * thing they are waiting to read.
   */
  private readonly following = signal(true);

  /** Keeps the end in view when the pane or transcript changes size (see below). */
  private bodyObserver: ResizeObserver | null = null;
  private observedBody: HTMLElement | null = null;
  private observedTranscript: HTMLElement | null = null;

  /**
   * The last scroll position requested by followEnd().
   *
   * Browsers may dispatch the resulting scroll event after later layout/content
   * growth. Without remembering this target, that delayed programmatic event can
   * look like a reader scroll away from the end and incorrectly disable follow
   * mode just before the reply arrives.
   */
  private programmaticScrollTop: number | null = null;

  /**
   * Whether the remembered programmatic scroll was followEnd (true) or an
   * anchor that deliberately left the end (false, #1557). A delayed event
   * from the scroll an anchor replaced must not switch following back on.
   */
  private programmaticScrollKeepsFollowing = true;

  /**
   * Review cards already brought into view (#1557), so only an ARRIVING card
   * moves the transcript. A later render — a step toggle, the composer
   * growing — must not re-anchor it.
   */
  private readonly revealedReviewCards = new Set<string>();

  /** The card whose full diff just toggled; its header stays in view next pass. */
  private detailsAnchorId: string | null = null;

  /** The element focused before opening, restored on close. */
  private previouslyFocused: HTMLElement | null = null;

  /** Deferred focus/geometry passes, cleared on teardown. */
  private readonly deferredTasks = new Set<ReturnType<typeof setTimeout>>();

  /**
   * Whether the shell exists at all: the user wants it (preference on) AND the
   * server can run it (available). The panel and the capsule share this one
   * gate, so neither can appear without the other's precondition.
   */
  readonly visible = this.launcher.available;

  /**
   * The workspace dock carries the launcher, so the floating capsule stands
   * down and the compact panel opens centred above the dock instead of from the
   * corner. Routes without a dock (the reader) keep the capsule.
   */
  readonly docked = this.launcher.dockHosted;

  /**
   * The reader rests its chrome on a tap and the capsule rests with it (#1345).
   * The capsule stays in the DOM so it can fade; the resting style makes it
   * invisible, unfocusable and unclickable.
   */
  readonly floatingHidden = this.launcher.floatingHidden;

  /** Ready-made requests for an empty conversation, grouped by capability. */
  readonly doors = computed(() => capabilityDoors(this.assistant.context()));

  /**
   * Review changesets in the transcript, oldest first (#1557).
   *
   * The newest one is the reader's next decision, so it — not the end of the
   * transcript — is what an arrival brings into view.
   */
  private readonly reviewCards = computed(() =>
    this.assistant.entries().flatMap((entry) =>
      (entry.artifacts ?? []).flatMap((artifact) =>
        artifact.kind === 'changeset' && artifact.changeset.status === 'review'
          ? [artifact.changeset.changesetId]
          : [],
      ),
    ),
  );

  /**
   * Geometry of the actually visible browser viewport.
   *
   * Mobile browsers may keep the layout viewport tall while the software
   * keyboard shrinks and offsets the visual viewport. The phone shell consumes
   * these values directly instead of guessing a keyboard height and translating
   * a bottom sheet on top of an independently changing `dvh`.
   *
   * Desktop CSS ignores these custom-property values.
   */
  readonly viewportHeightCss = signal('100dvh');
  readonly viewportTopCss = signal('0px');

  readonly isOpen = computed(() => this.assistant.isOpen());
  readonly isReader = computed(() => this.assistant.context().surface === 'reader');
  /**
   * Selection actions already include Ask Nostos, so the launcher steps aside
   * rather than offer it twice. (The bar is docked at the top now, #1344, so
   * this is no longer about sharing the bottom edge.)
   */
  readonly readerSelectionActive = computed(
    () => this.isReader() && !!this.assistant.context().selectedText?.trim(),
  );

  /** A quiet, platform-appropriate hint shown in the collapsed desktop pill. */
  readonly shortcutLabel = this.launcher.shortcutLabel;

  /**
   * Desktop-only focus workspace. This changes only the shell geometry; the
   * AssistantService remains the single owner of conversation, draft and plan
   * state, so compact <-> expanded never creates a second chat session.
   */
  readonly expanded = signal(false);

  constructor() {
    // Availability is a server fact; ask once for the life of the session.
    this.status.ensureLoaded();

    // The failure block needs the deployment contract to choose between "Open
    // AI connections" (SelfHosted) and "Open Settings" (managed), and the
    // account link for an allowance failure. The contract normally arrives at
    // bootstrap; ask once if it has not.
    this.allowance.ensureCapabilities();

    // An allowance failure is the only one whose copy also needs the renewal
    // date; fetch usage only when that block is actually on screen.
    effect(() => {
      const allowanceFailure = this.assistant.entries().some(
        (entry) => entry.kind === 'error' && entry.meta === 'ai_usage_limit_reached',
      );
      if (allowanceFailure) this.allowance.ensureUsage();
    });

    // Following the newest turn is a DOM measurement, so it belongs after
    // render, and it must run for every block that can add height under the
    // transcript: the reply, a capture acknowledgement, the topic
    // suggestions, a changeset card, or a follow-up question. The scroll
    // container is read and written in one go, which is what the mixed phase
    // is for.
    afterRenderEffect({
      mixedReadWrite: () => {
        this.assistant.entries();
        this.assistant.sending();
        this.assistant.suggestions();
        this.assistant.pendingReview();
        this.assistant.changesetErrors();
        this.assistant.pendingAnchor();
        this.observeScrollGeometry(
          this.body()?.nativeElement ?? null,
          this.transcript()?.host.nativeElement ?? null,
        );
        this.keepReviewCardsInView();
        this.followEnd();
      },
    });

    // Feature surfaces (for example a selected reader passage) request an
    // assistant open through the service, but the component still owns the UI
    // lifecycle: availability gating, focus, transcript follow state and the
    // mobile visual viewport.
    const surfaceOpenSubscription = this.assistant.surfaceOpenRequested.subscribe(() => {
      if (this.assistant.isOpen()) {
        // A reader can select a new passage while compact Ask Nostos is already
        // open. Keep the existing shell geometry/focus-return owner; just bring
        // the conversation back to its newest turn and focus the composer on
        // desktop.
        this.following.set(true);
        this.followEnd();
        if (!this.isPhoneViewport()) this.focusComposerAfterRender();
        return;
      }
      this.open();
    });

    // A launcher outside this component (the dock) toggles through the
    // launcher service; the lifecycle stays here.
    const launcherSubscription = this.launcher.toggleRequested.subscribe(() => this.toggle());

    // This shell loads lazily (app.component.html). A launcher press or a
    // surface's open request made before it existed was kept; act on it once
    // the shell has rendered.
    const pendingOpen = this.assistant.takePendingSurfaceOpen();
    const pendingToggle = this.launcher.takePendingToggle();
    if (pendingOpen || pendingToggle) {
      afterNextRender(() => {
        if (pendingOpen) this.open();
        else this.toggle();
      }, { injector: this.injector });
    }

    this.destroyRef.onDestroy(() => {
      surfaceOpenSubscription.unsubscribe();
      launcherSubscription.unsubscribe();
      this.bodyObserver?.disconnect();
      this.bodyObserver = null;
      for (const task of this.deferredTasks) clearTimeout(task);
      this.deferredTasks.clear();
      this.stopViewportTracking();
    });
  }

  /**
   * The reader scrolled. Being at the end means they are following the
   * conversation; anywhere else means they are reading an older turn and the
   * view is theirs to move.
   */
  onBodyScroll(): void {
    const element = this.body()?.nativeElement;
    if (!element) return;

    const requested = this.programmaticScrollTop;
    if (requested !== null) {
      this.programmaticScrollTop = null;

      // A programmatic scroll can be reported after the transcript has already
      // grown. Its old target is then no longer the current end, but it still
      // must not be mistaken for the reader deliberately scrolling back.
      if (Math.abs(element.scrollTop - requested) <= FOLLOW_THRESHOLD_PX) {
        this.following.set(this.programmaticScrollKeepsFollowing);
        return;
      }
      if (!this.programmaticScrollKeepsFollowing) {
        // This event belongs to the scroll the anchor replaced. The anchor
        // owns the view; a reader who wants the end scrolls again.
        this.following.set(false);
        return;
      }
    }

    this.following.set(this.distanceToEnd(element) <= FOLLOW_THRESHOLD_PX);
  }

  /** Every citation entry opens through the shell, so a phone jump closes the panel. */
  canOpenEvidence(evidence: AssistantEvidenceReferenceDto): boolean {
    return canOpenEvidence(evidence, !!this.router);
  }

  openEvidence(evidence: AssistantEvidenceReferenceDto): void {
    if (!this.router) return;

    if (evidence.handle.kind === 'note' && evidence.handle.noteId) {
      void this.router.navigate(['/brain/notes', evidence.handle.noteId]).then((navigated) => {
        if (navigated && this.isPhoneViewport()) this.close();
      });
      return;
    }

    if (evidence.handle.kind === 'topic' && evidence.handle.topicId) {
      void this.router.navigate(['/brain/topics', evidence.handle.topicId]).then((navigated) => {
        if (navigated && this.isPhoneViewport()) this.close();
      });
      return;
    }

    const bookId = evidence.handle.bookId;
    const locator = evidence.locators?.[0];
    if (!bookId || !locator) return;

    this.openSource({
      bookId,
      bookTitle: evidence.bookTitle?.trim() || evidence.label,
      bookAuthor: evidence.bookAuthor ?? null,
      format: evidence.format ?? '',
      sourceSha256: evidence.handle.sourceSha256 ?? '',
      excerpt: evidence.excerpt ?? '',
      locators: evidence.locators ?? [],
    });
  }

  openSource(source: AssistantSourceReferenceDto): void {
    const locator = source.locators[0];
    if (!locator) return;

    const queryParams: Record<string, string | number> = {};
    if (locator.type === 'pdf' && locator.pdfPageIndex !== null && locator.pdfPageIndex !== undefined) {
      queryParams['sourcePage'] = locator.pdfPageIndex + 1;
      if (locator.pdfPageLabel) queryParams['sourcePageLabel'] = locator.pdfPageLabel;
    } else if (locator.type === 'epub') {
      if (locator.epubCfi) queryParams['sourceCfi'] = locator.epubCfi;
      if (locator.epubResourceHref) queryParams['sourceHref'] = locator.epubResourceHref;
      if (locator.epubSpineIndex !== null && locator.epubSpineIndex !== undefined) {
        queryParams['sourceSpine'] = locator.epubSpineIndex;
      }
      if (locator.startTextOffset !== null && locator.startTextOffset !== undefined) {
        queryParams['sourceOffset'] = locator.startTextOffset;
      }
      queryParams['sourceExcerpt'] = source.excerpt.slice(0, 240);
    } else {
      return;
    }

    if (this.router) {
      void this.router.navigate(['/read', source.bookId], { queryParams }).then((navigated) => {
        // On phones Ask Nostos owns the full visual viewport. A successful
        // source jump should therefore reveal the cited passage immediately;
        // the conversation remains in AssistantService for one-tap return.
        if (navigated && this.isPhoneViewport()) this.close();
      });
    }
  }

  onEntryRetried(): void {
    this.following.set(true);
    // The pending bar only appears after ASSISTANT_PENDING_DELAY_MS; the
    // conversation itself is the stable place to put focus now. The live
    // progress/failure region then announces without stealing it back.
    this.defer(() => {
      this.transcript()?.host.nativeElement.focus({ preventScroll: true });
      this.followEnd();
    });
  }

  /** One deferred pass that teardown clears, so it cannot run on a dead view. */
  private defer(action: () => void): void {
    const task = setTimeout(() => {
      this.deferredTasks.delete(task);
      action();
    }, 0);
    this.deferredTasks.add(task);
  }

  onComposerSubmitted(): void {
    // Sending is the one action that always returns to the end: the reply to
    // this message is what the reader is waiting for, wherever they had
    // scrolled to.
    this.following.set(true);
    this.followEnd();
  }

  onFailureAction(action: AssistantFailureAction): void {
    if (!this.router) return;

    if (action.kind === 'book') {
      void this.router.navigate(['/read', action.bookId]).then((navigated) => {
        if (navigated && this.isPhoneViewport()) this.close();
      });
      return;
    }

    const section = action.kind === 'connections' ? 'connections' : 'assistant';
    void this.router.navigate(['/settings'], { queryParams: { section } }).then((navigated) => {
      if (navigated && this.isPhoneViewport()) this.close();
    });
  }

  /**
   * Puts a door's example in the composer and leaves it there. It never
   * submits: a prefilled write is shown to the person before it can run.
   */
  prefillFromDoor(example: CapabilityExample): void {
    this.composer()?.prefill(example.fill ?? example.label);
  }

  /** Forwarded for tests and keyboard parity with the composer's own controls. */
  onSendClick(): void {
    this.composer()?.onSendClick();
  }

  onMicTap(): void {
    this.composer()?.onMicTap();
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    // Cmd/Ctrl+J opens the assistant. Cmd/Ctrl+K belongs to the command
    // palette and is deliberately not touched here.
    const mod = event.metaKey || event.ctrlKey;
    if (mod && !event.altKey && event.key.toLowerCase() === 'j') {
      // Never hijack the shortcut when the assistant is not on screen.
      if (!this.visible()) return;
      event.preventDefault();
      if (this.assistant.isOpen()) this.close();
      else this.open();
      return;
    }
    if (event.key === 'Escape' && this.visible() && this.assistant.isOpen()) {
      event.preventDefault();
      this.close();
    }
  }

  @HostListener('document:pointerdown', ['$event'])
  onPointerDown(event: PointerEvent): void {
    if (!this.assistant.isOpen()) return;
    const target = event.target as Node | null;
    if (target && this.host.nativeElement.contains(target)) return;
    // Pressing a launcher is not an outside click: closing here would let the
    // launcher's own click reopen the panel straight away.
    if (target instanceof Element && target.closest(`[${ASSISTANT_LAUNCHER_ATTR}]`)) return;
    this.close();
  }

  toggle(): void {
    if (this.assistant.isOpen()) this.close();
    else this.open();
  }

  open(): void {
    if (!this.visible()) return;
    this.previouslyFocused =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    // Opening lands on the newest turn rather than the top of an old
    // conversation, however the reader left the view last time.
    this.following.set(true);
    this.expanded.set(false);
    this.assistant.open();
    this.startViewportTracking();

    // Opening a dedicated phone surface should not summon the software keyboard
    // before the reader asks for it. Desktop keeps the fast type-immediately
    // behavior of the compact assistant.
    if (!this.isPhoneViewport()) this.focusComposerAfterRender();
  }

  private focusComposerAfterRender(): void {
    afterNextRender(() => this.composer()?.focus(), { injector: this.injector });
  }

  close(): void {
    // Closing the surface abandons any live recording or upload: the tracks are
    // stopped and the audio discarded, never left running behind a closed panel.
    this.voice.cancel();
    this.assistant.close();
    this.expanded.set(false);
    this.programmaticScrollTop = null;
    this.stopViewportTracking();
    const previous = this.previouslyFocused;
    this.previouslyFocused = null;
    if (previous && previous.isConnected) previous.focus();
  }

  onBackdrop(): void {
    this.close();
  }

  toggleExpanded(): void {
    this.expanded.update((value) => !value);
    // The body may gain hundreds of pixels in one render. Preserve the existing
    // transcript-follow contract when the reader is already at the newest turn.
    afterNextRender(() => this.followEnd(), { injector: this.injector });
  }

  /** Pixels between the current scroll position and the end of the content. */
  private distanceToEnd(element: HTMLElement): number {
    return element.scrollHeight - element.scrollTop - element.clientHeight;
  }

  /** Brings the newest turn into view, when the reader is following it. */
  private followEnd(): void {
    if (!this.following()) return;
    const element = this.body()?.nativeElement;
    if (!element) return;

    const target = Math.max(0, element.scrollHeight - element.clientHeight);
    this.programmaticScrollTop = target;
    this.programmaticScrollKeepsFollowing = true;
    element.scrollTop = target;
  }

  /**
   * Brings an arriving review's TOP into view (#1557).
   *
   * The end of a review taller than the panel is a wall of steps; its title,
   * the headline and the first steps — the words that say what is about to
   * happen — were the part scrolled out. Only the newest review card is
   * anchored, and only once: a step toggle or a composer reflow leaves the
   * reader where they are. Following is switched off deliberately, because a
   * review is something to read, not a reply to keep pinned to the end.
   */
  private keepReviewCardsInView(): void {
    const cards = this.reviewCards();
    if (cards.length === 0) return;

    const unseen = cards.filter((id) => !this.revealedReviewCards.has(id));
    if (unseen.length === 0) {
      this.keepDetailsAnchorInView();
      return;
    }

    const body = this.body()?.nativeElement;
    if (!body) return; // Closed panel: keep the arrival pending until it shows it.

    const newest = cards[cards.length - 1];
    const card = this.changesetCardElement(newest);
    if (!card) return;

    // Every review currently rendered counts as seen; an older one restored
    // beside the newest must not win a later anchor.
    for (const id of cards) this.revealedReviewCards.add(id);

    // A reader who scrolled back to an older turn keeps their place.
    if (!this.following()) return;

    this.following.set(false);
    this.scrollBodyTo(body, this.changesetStartScrollTop(body, card));
  }

  /**
   * "Show every change" grows the card downward. Its header must stay on
   * screen (#1557), so the card's start returns to the top of the transcript
   * viewport — the same anchor the arrival uses, and one the expanding layout
   * cannot drag back to the end.
   */
  private keepDetailsAnchorInView(): void {
    const id = this.detailsAnchorId;
    if (!id) return;

    const body = this.body()?.nativeElement;
    const card = body ? this.changesetCardElement(id) : null;
    if (!body || !card) {
      this.detailsAnchorId = null;
      return;
    }

    this.detailsAnchorId = null;
    this.scrollBodyTo(body, this.changesetStartScrollTop(body, card));
  }

  /** The full diff of a review opened or closed; see keepDetailsAnchorInView. */
  onChangesetDetailsToggled(changesetId: string, open: boolean): void {
    // Inspecting a card is not a request to follow the transcript's end.
    this.following.set(false);
    this.detailsAnchorId = open ? changesetId : null;
    this.keepDetailsAnchorInView();
  }

  private changesetCardElement(changesetId: string): HTMLElement | null {
    const transcript = this.transcript()?.host.nativeElement as HTMLElement | undefined;
    if (!transcript) return null;
    const cards = transcript.querySelectorAll<HTMLElement>('[data-changeset-id]');
    for (const card of cards) {
      if (card.getAttribute('data-changeset-id') === changesetId) return card;
    }
    return null;
  }

  /** The scroll position that puts the card's top edge at the viewport's top. */
  private changesetStartScrollTop(body: HTMLElement, card: HTMLElement): number {
    const bodyTop = body.getBoundingClientRect().top;
    return Math.max(0, body.scrollTop + card.getBoundingClientRect().top - bodyTop);
  }

  /**
   * Scrolls the transcript without touching focus. Motion is the reader's
   * choice: `prefers-reduced-motion` makes the same move instantly.
   */
  private scrollBodyTo(body: HTMLElement, top: number): void {
    if (Math.abs(body.scrollTop - top) < 1) return;
    // A followEnd target from before this move no longer describes the
    // scrollport. Remember this anchor instead, so the first smooth-scroll
    // event cannot look like "still at the end" (#1557).
    this.programmaticScrollTop = top;
    this.programmaticScrollKeepsFollowing = false;
    if (prefersReducedMotion() || typeof body.scrollTo !== 'function') {
      body.scrollTop = top;
      return;
    }
    body.scrollTo({ top, behavior: 'smooth' });
  }

  /**
   * Watches both sides of the scroll geometry.
   *
   * The body can resize when the composer, keyboard, or viewport changes. The
   * transcript can also grow after Angular's render pass while the compact
   * flyout is still resolving its max-height/flex layout. Watching only the
   * body misses that second case: expanded mode has a stable fixed-height pane,
   * but compact mode can otherwise stop just above a newly-arrived reply.
   */
  private observeScrollGeometry(
    body: HTMLElement | null,
    transcript: HTMLElement | null,
  ): void {
    if (body === this.observedBody && transcript === this.observedTranscript) return;

    this.bodyObserver?.disconnect();
    this.bodyObserver = null;
    this.observedBody = body;
    this.observedTranscript = transcript;

    if (!body || typeof ResizeObserver === 'undefined') return;

    this.bodyObserver = new ResizeObserver(() => this.followEnd());
    this.bodyObserver.observe(body);
    if (transcript) this.bodyObserver.observe(transcript);
  }

  private startViewportTracking(): void {
    const viewport = typeof window !== 'undefined' ? window.visualViewport : null;
    this.updateViewportGeometry();
    if (!viewport) return;
    viewport.addEventListener('resize', this.onViewportChange);
    viewport.addEventListener('scroll', this.onViewportChange);
  }

  private stopViewportTracking(): void {
    const viewport = typeof window !== 'undefined' ? window.visualViewport : null;
    if (viewport) {
      viewport.removeEventListener('resize', this.onViewportChange);
      viewport.removeEventListener('scroll', this.onViewportChange);
    }
    this.viewportHeightCss.set('100dvh');
    this.viewportTopCss.set('0px');
  }

  private readonly onViewportChange = (): void => this.updateViewportGeometry();

  private updateViewportGeometry(): void {
    const viewport = typeof window !== 'undefined' ? window.visualViewport : null;
    if (!viewport) {
      this.viewportHeightCss.set('100dvh');
      this.viewportTopCss.set('0px');
      return;
    }

    this.viewportHeightCss.set(`${Math.max(1, Math.round(viewport.height))}px`);
    this.viewportTopCss.set(`${Math.max(0, Math.round(viewport.offsetTop))}px`);
  }

  private isPhoneViewport(): boolean {
    return isPhoneViewport();
  }
}
