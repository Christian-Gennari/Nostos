import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';

import { ButtonComponent } from '../button/button.component';
import { Router } from '@angular/router';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import {
  AssistantEntry,
  AssistantEvidenceReferenceDto,
  AssistantService,
  AssistantSourceReferenceDto,
  AssistantTurnArtifact,
  formatTimestamp,
} from './assistant.service';
import { AssistantVoiceService } from './assistant-voice.service';
import { AssistantStatusService } from './assistant-status.service';
import { AssistantMarkdownPipe } from './assistant-markdown.pipe';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';

/**
 * How close to the end of the transcript still counts as "reading the newest
 * turn". Comfortably above sub-pixel rounding (a scroll position is an integer,
 * line heights are not) and below one line of text, so a deliberate scroll back
 * to an older turn always registers as one.
 */
const FOLLOW_THRESHOLD_PX = 32;

export type EntrySourceItem =
  | {
      kind: 'evidence';
      key: string;
      bookId: string | null;
      bookTitle: string;
      excerpt: string;
      groupableBook: boolean;
      evidence: AssistantEvidenceReferenceDto;
    }
  | {
      kind: 'legacy';
      key: string;
      bookId: string | null;
      bookTitle: string;
      excerpt: string;
      groupableBook: boolean;
      source: AssistantSourceReferenceDto;
    };

export type EntrySourceDisplay =
  | {
      kind: 'item';
      key: string;
      item: EntrySourceItem;
    }
  | {
      kind: 'group';
      key: string;
      bookId: string;
      bookTitle: string;
      items: EntrySourceItem[];
    };

export interface EntrySourceView {
  items: EntrySourceDisplay[];
}

function canonicalEvidenceKey(evidence: AssistantEvidenceReferenceDto): string {
  const handle = evidence.handle;
  return [
    handle.kind,
    handle.noteId ?? '',
    handle.conceptId ?? '',
    handle.bookId ?? '',
    handle.sourceSha256 ?? '',
    handle.extractorVersion ?? '',
    handle.ordinal ?? '',
  ].map(String).join('|');
}

function legacySourceKey(source: AssistantSourceReferenceDto): string {
  return `${source.bookId}|${source.sourceSha256}|${JSON.stringify(source.locators ?? [])}|${source.excerpt.slice(0, 320)}`;
}

/**
 * Builds the one transcript source surface for a completed entry.
 *
 * Canonical evidence wins whenever it exists. Legacy `entry.sources` is only
 * the historical bridge for entries that carry no evidence artifacts.
 */
export function buildEntrySourceView(entry: AssistantEntry): EntrySourceView {
  const evidenceArtifacts = (entry.artifacts ?? []).filter(
    (artifact): artifact is Extract<AssistantTurnArtifact, { kind: 'evidence' }> =>
      artifact.kind === 'evidence',
  );

  const sourceItems: EntrySourceItem[] = [];

  if (evidenceArtifacts.length > 0) {
    const seen = new Set<string>();

    for (const artifact of evidenceArtifacts) {
      const evidence = artifact.evidence;
      const dedupeKey = canonicalEvidenceKey(evidence);
      if (seen.has(dedupeKey)) continue;
      seen.add(dedupeKey);

      const bookId = evidence.handle.bookId ?? null;
      sourceItems.push({
        kind: 'evidence',
        key: `evidence:${dedupeKey}`,
        bookId,
        bookTitle: evidence.bookTitle?.trim() || evidence.label,
        excerpt: evidence.excerpt ?? '',
        groupableBook: evidence.handle.kind === 'book_text' && !!bookId,
        evidence,
      });
    }
  } else {
    const seen = new Set<string>();

    for (const source of entry.sources ?? []) {
      const dedupeKey = legacySourceKey(source);
      if (seen.has(dedupeKey)) continue;
      seen.add(dedupeKey);

      sourceItems.push({
        kind: 'legacy',
        key: `legacy:${dedupeKey}`,
        bookId: source.bookId || null,
        bookTitle: source.bookTitle,
        excerpt: source.excerpt,
        groupableBook: !!source.bookId,
        source,
      });
    }
  }

  type PendingDisplay =
    | {
        kind: 'item';
        key: string;
        item: EntrySourceItem;
      }
    | {
        kind: 'book';
        key: string;
        bookId: string;
        bookTitle: string;
        items: EntrySourceItem[];
      };

  const pending: PendingDisplay[] = [];
  const bookGroupIndexes = new Map<string, number>();

  for (const item of sourceItems) {
    if (!item.groupableBook || !item.bookId) {
      pending.push({
        kind: 'item',
        key: item.key,
        item,
      });
      continue;
    }

    const existingIndex = bookGroupIndexes.get(item.bookId);
    if (existingIndex !== undefined) {
      const existing = pending[existingIndex];
      if (existing.kind === 'book') existing.items.push(item);
      continue;
    }

    bookGroupIndexes.set(item.bookId, pending.length);
    pending.push({
      kind: 'book',
      key: `book:${item.bookId}`,
      bookId: item.bookId,
      bookTitle: item.bookTitle,
      items: [item],
    });
  }

  return {
    items: pending.map((display): EntrySourceDisplay => {
      if (display.kind === 'item') return display;
      if (display.items.length === 1) {
        return {
          kind: 'item',
          key: display.items[0].key,
          item: display.items[0],
        };
      }

      return {
        kind: 'group',
        key: display.key,
        bookId: display.bookId,
        bookTitle: display.bookTitle,
        items: display.items,
      };
    }),
  };
}

/**
 * App-wide assistant shell (issue #261 §1, §2, §4 capture; #262 voice).
 *
 * One root-level component: a quiet collapsed capsule that opens a compact
 * capture/conversation surface. It is surface-aware — the collapsed trigger
 * moves out of the reader's text column on phones — and it never steals focus
 * while closed. No LLM: capture and push-to-talk voice only.
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
  imports: [NostosIconComponent, AssistantMarkdownPipe, ButtonComponent, IconButtonComponent],
  templateUrl: './assistant.component.html',
  styleUrl: './assistant.component.css',
  host: {
    '[class.is-open]': 'assistant.isOpen()',
  },
})
export class AssistantComponent {
  readonly assistant = inject(AssistantService);
  readonly voice = inject(AssistantVoiceService);
  private readonly status = inject(AssistantStatusService);
  private readonly router = inject(Router, { optional: true });
  private readonly preferences = inject(LibraryPreferencesService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly composer = viewChild<ElementRef<HTMLTextAreaElement>>('composer');
  /** The transcript's scroll container. */
  private readonly body = viewChild<ElementRef<HTMLElement>>('body');
  /** The content whose post-layout growth can move the compact pane's end. */
  private readonly transcript = viewChild<ElementRef<HTMLElement>>('transcript');
  private readonly destroyRef = inject(DestroyRef);

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

  /** The element focused before opening, restored on close. */
  private previouslyFocused: HTMLElement | null = null;

  /**
   * Book source groups are collapsed by default. The key includes the visible
   * entry identity because the same book may legitimately occur in many turns.
   */
  readonly expandedSourceGroups = signal<Set<string>>(new Set());

  /**
   * Whether the shell exists at all: the user wants it (preference on) AND the
   * server can run it (available). The panel and the capsule share this one
   * gate, so neither can appear without the other's precondition.
   */
  readonly visible = computed(
    () => this.preferences.assistantEnabled() && this.status.available(),
  );

  /** Product-level voice preference; provider configuration stays server-side. */
  readonly voiceTranscriptionEnabled = this.preferences.assistantVoiceEnabled;

  /** The compact header names the current book when the surface has one. */
  readonly currentBookTitle = computed(() => {
    const title = this.assistant.context().bookTitle?.trim();
    return title ? title : null;
  });

  /** Drives the quiet send affordance without making the template inspect text. */
  readonly hasDraft = computed(() => this.assistant.draft().trim().length > 0);

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

  /** A quiet, platform-appropriate hint shown in the collapsed desktop pill. */
  readonly shortcutLabel = this.resolveShortcutLabel();

  /**
   * Desktop-only focus workspace. This changes only the shell geometry; the
   * AssistantService remains the single owner of conversation, draft and plan
   * state, so compact <-> expanded never creates a second chat session.
   */
  readonly expanded = signal(false);

  constructor() {
    // Availability is a server fact; ask once for the life of the session.
    this.status.ensureLoaded();

    // A finished transcript is handed to the conversation, which owns the ONE
    // policy for whether it is reviewed or auto-sent. The composer keeps focus so
    // the user can read and edit before pressing Enter.
    this.voice.onTranscript = (text) => {
      this.assistant.insertTranscript(text);
      setTimeout(() => this.composer()?.nativeElement.focus(), 0);
    };

    // Following the newest turn is a DOM measurement, so it belongs after
    // render, and it must run for every block that can add height under the
    // transcript: the reply, a capture acknowledgement, the concept
    // suggestions, a proposed plan, a follow-up question, or the original-text
    // panel opening. The scroll container is read and written in one go, which
    // is what the mixed phase is for.
    afterRenderEffect({
      mixedReadWrite: () => {
        this.assistant.entries();
        this.assistant.sending();
        this.assistant.suggestions();
        this.assistant.pendingPlan();
        this.assistant.pendingAnchor();
        this.assistant.rawOpen();
        this.expandedSourceGroups();
        this.observeScrollGeometry(
          this.body()?.nativeElement ?? null,
          this.transcript()?.nativeElement ?? null,
        );
        this.followEnd();
      },
    });

    this.destroyRef.onDestroy(() => {
      this.bodyObserver?.disconnect();
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
        this.following.set(true);
        return;
      }
    }

    this.following.set(this.distanceToEnd(element) <= FOLLOW_THRESHOLD_PX);
  }

  sourceView(entry: AssistantEntry): EntrySourceView {
    return buildEntrySourceView(entry);
  }

  sourceGroupId(entryId: string, bookId: string): string {
    return `assistant-source-group-${encodeURIComponent(entryId)}-${encodeURIComponent(bookId)}`;
  }

  isSourceGroupExpanded(entryId: string, bookId: string): boolean {
    return this.expandedSourceGroups().has(`${entryId}:${bookId}`);
  }

  toggleSourceGroup(entryId: string, bookId: string): void {
    const key = `${entryId}:${bookId}`;
    this.expandedSourceGroups.update((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  sourceExcerpt(excerpt: string | null | undefined): string {
    return (excerpt ?? '').slice(0, 120).trim();
  }

  evidenceLabel(evidence: AssistantEvidenceReferenceDto): string {
    const locator = evidence.locators?.[0];
    const title = evidence.bookTitle?.trim() || evidence.label;
    if (!locator) return evidence.label;
    if (locator.type === 'pdf' && locator.pdfPageIndex !== null && locator.pdfPageIndex !== undefined) {
      const page = locator.pdfPageLabel?.trim() || String(locator.pdfPageIndex + 1);
      return `${title} · p. ${page}`;
    }
    if (locator.type === 'epub') return `${title} · reading position`;
    return evidence.label;
  }

  canOpenEvidence(evidence: AssistantEvidenceReferenceDto): boolean {
    if (!this.router) return false;
    if (evidence.handle.kind === 'note') return !!evidence.handle.noteId;
    if (evidence.handle.kind === 'concept') return !!evidence.handle.conceptId;
    return !!evidence.handle.bookId
      && !!evidence.locators?.some((locator) => locator.type === 'pdf' || locator.type === 'epub');
  }

  openEvidence(evidence: AssistantEvidenceReferenceDto): void {
    if (!this.router) return;

    if (evidence.handle.kind === 'note' && evidence.handle.noteId) {
      void this.router.navigate(
        ['/second-brain'],
        { queryParams: { noteId: evidence.handle.noteId } },
      ).then((navigated) => {
        if (navigated && this.isPhoneViewport()) this.close();
      });
      return;
    }

    if (evidence.handle.kind === 'concept' && evidence.handle.conceptId) {
      void this.router.navigate(
        ['/second-brain'],
        { queryParams: { conceptId: evidence.handle.conceptId } },
      ).then((navigated) => {
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

  showProposalArtifact(entry: AssistantEntry, artifact: AssistantTurnArtifact): boolean {
    if (artifact.kind !== 'proposal') return false;
    return !(entry.suggestions ?? []).some((suggestion) =>
      suggestion.noteId === artifact.proposal.noteId
      && (suggestion.value ?? suggestion.label) ===
        (artifact.proposal.value ?? artifact.proposal.label));
  }

  artifactLabel(artifact: AssistantTurnArtifact): string {
    switch (artifact.kind) {
      case 'capture':
        return 'Saved note';
      case 'action':
        return `Changed · ${this.humanizeCapability(artifact.capability)}`;
      case 'failure':
        // Cancelled failures are not rendered: the turn's stop message owns that state.
        return `Failed · ${artifact.message}`;
      case 'proposal':
        return `Proposed · ${artifact.proposal.label} — ${artifact.proposal.reason}`;
      case 'destructive-result':
        return `${artifact.outcome === 'applied' ? 'Applied'
          : artifact.outcome === 'refused' ? 'Not applied'
          : artifact.outcome === 'superseded' ? 'Superseded'
          : 'Failed'} · ${artifact.summary}`;
      case 'evidence':
        return this.evidenceLabel(artifact.evidence);
    }
  }

  private humanizeCapability(capability: string): string {
    const labels: Record<string, string> = {
      notes_link_existing_concept: 'note linked to concept',
      library_update_book: 'book updated',
      library_move_book: 'book moved',
      library_create_collection: 'collection created',
      library_delete_collection: 'collection deleted',
    };
    return labels[capability] ?? capability.replaceAll('_', ' ');
  }

  sourceLabel(source: AssistantSourceReferenceDto): string {
    const locator = source.locators[0];
    if (!locator) return source.bookTitle;
    if (locator.type === 'pdf' && locator.pdfPageIndex !== null && locator.pdfPageIndex !== undefined) {
      const page = locator.pdfPageLabel?.trim() || String(locator.pdfPageIndex + 1);
      return `${source.bookTitle} · p. ${page}`;
    }
    if (locator.type === 'epub') return `${source.bookTitle} · reading position`;
    return source.bookTitle;
  }

  canOpenSource(source: AssistantSourceReferenceDto): boolean {
    return !!this.router
      && !!source.locators?.some((locator) => locator.type === 'pdf' || locator.type === 'epub');
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

  /** The visible recorder clock, e.g. "0:07". */
  elapsedLabel(): string {
    return formatTimestamp(String(this.voice.elapsedSeconds()));
  }

  onMicTap(): void {
    if (!this.voiceTranscriptionEnabled()) return;
    if (this.voice.isRecording()) {
      this.voice.stop();
      return;
    }
    if (this.voice.status() === 'idle') this.voice.start();
  }

  onVoiceStop(): void {
    this.voice.stop();
  }

  onVoiceCancel(): void {
    this.voice.cancel();
  }

  /** Pre-dispatch Undo: keeps the transcript in the composer, sends nothing. */
  onUndoTranscript(): void {
    this.assistant.undoTranscript();
    setTimeout(() => this.composer()?.nativeElement.focus(), 0);
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
    if (!this.isPhoneViewport()) {
      setTimeout(() => this.composer()?.nativeElement.focus(), 0);
    }
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
    setTimeout(() => this.followEnd(), 0);
  }

  onComposerInput(event: Event): void {
    const element = event.target as HTMLTextAreaElement;
    this.assistant.updateDraft(element.value);
    this.autoGrow(element);
  }

  onComposerEnter(event: Event): void {
    const keyboard = event as KeyboardEvent;
    if (keyboard.shiftKey) return; // Shift+Enter is a newline.
    event.preventDefault();
    this.submitDraft();
  }

  onSendClick(): void {
    if (!this.hasDraft() || this.assistant.sending()) return;
    this.submitDraft();
    setTimeout(() => this.composer()?.nativeElement.focus(), 0);
  }

  private submitDraft(): void {
    // Sending is the one action that always returns to the end: the reply to
    // this message is what the reader is waiting for, wherever they had
    // scrolled to.
    this.following.set(true);
    this.assistant.submit();
    const element = this.composer()?.nativeElement;
    if (element) {
      element.value = this.assistant.draft();
      this.autoGrow(element);
    }
  }

  private autoGrow(element: HTMLTextAreaElement): void {
    element.style.height = 'auto';
    element.style.height = `${Math.min(element.scrollHeight, 160)}px`;
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
    element.scrollTop = target;
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

  private resolveShortcutLabel(): string {
    if (typeof navigator === 'undefined') return 'Ctrl J';
    return /Macintosh|Mac OS X|iPhone|iPad|iPod/i.test(navigator.userAgent)
      ? '⌘ J'
      : 'Ctrl J';
  }

  private isPhoneViewport(): boolean {
    if (typeof window === 'undefined') return false;
    if (typeof window.matchMedia === 'function') {
      return window.matchMedia('(max-width: 768px)').matches;
    }
    return window.innerWidth <= 768;
  }
}
