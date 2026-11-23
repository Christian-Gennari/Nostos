import { Injectable, effect, signal } from '@angular/core';
import { BookSort } from '../dtos/book.enums';

export type NoteTextSize = 'sm' | 'md' | 'lg';

export const NOTE_TEXT_SIZE_PX: Record<NoteTextSize, string> = {
  sm: '18px',
  md: '21px',
  lg: '24px',
};

export interface LibraryPreferences {
  viewMode: 'grid' | 'list';
  sort: BookSort;
  pageSize: number;
  sidebarExpanded: boolean;
  groupByWork: boolean;
  /**
   * Whether the reading assistant's dock capsule and panel are shown. This is
   * user INTENT only; whether the server can actually run the assistant is a
   * separate signal (`AssistantStatusService`). Defaults off so the AI
   * assistant is explicitly opt-in rather than appearing automatically.
   */
  assistantEnabled: boolean;
  /**
   * Whether push-to-talk transcription is offered in Ask Nostos. This is a
   * product preference only: provider identity and credentials remain a server
   * concern. It defaults on to preserve the assistant's existing behavior.
   */
  assistantVoiceEnabled: boolean;
  /**
   * Reading size for note body text in the Brain reader (issue #1808):
   * S (18px), M (21px, default), or L (24px).
   */
  noteTextSize?: NoteTextSize;
}

export const LIBRARY_PREFERENCES_STORAGE_KEY = 'nostos.library.preferences';
export const WORK_EDITIONS_STORAGE_KEY = 'nostos.work_editions';
const LEGACY_VIEW_MODE_STORAGE_KEY = 'nostos.viewMode';

const DEFAULT_PREFERENCES: Required<LibraryPreferences> = {
  viewMode: 'grid',
  sort: BookSort.LastRead,
  pageSize: 20,
  sidebarExpanded: true,
  groupByWork: true,
  assistantEnabled: false,
  assistantVoiceEnabled: true,
  noteTextSize: 'md',
};

const VALID_VIEW_MODES: readonly LibraryPreferences['viewMode'][] = ['grid', 'list'];
const VALID_SORTS: readonly BookSort[] = Object.values(BookSort);
const VALID_NOTE_TEXT_SIZES: readonly NoteTextSize[] = ['sm', 'md', 'lg'];

@Injectable({ providedIn: 'root' })
export class LibraryPreferencesService {
  readonly viewMode = signal<LibraryPreferences['viewMode']>(DEFAULT_PREFERENCES.viewMode);
  readonly sort = signal<BookSort>(DEFAULT_PREFERENCES.sort);
  readonly pageSize = signal(DEFAULT_PREFERENCES.pageSize);
  readonly sidebarExpanded = signal(DEFAULT_PREFERENCES.sidebarExpanded);
  readonly groupByWork = signal(DEFAULT_PREFERENCES.groupByWork);
  readonly assistantEnabled = signal(DEFAULT_PREFERENCES.assistantEnabled);
  readonly assistantVoiceEnabled = signal(DEFAULT_PREFERENCES.assistantVoiceEnabled);
  readonly noteTextSize = signal<NoteTextSize>(DEFAULT_PREFERENCES.noteTextSize);
  private workEditions = new Map<string, string>();

  /**
   * Whether the library has already shown results in this browser session.
   * Intentionally session-only and never persisted: returning to the library
   * from another section must cross-fade its results in, not re-run the first-paint waiting field
   * again (see Library.refreshBooks / commitResults).
   */
  readonly hasLoadedBooks = signal(false);

  constructor() {
    this.hydrate();
    this.hydrateWorkEditions();
    this.applyNoteTextSize(this.noteTextSize());

    effect(() => {
      const noteTextSize = this.noteTextSize();
      this.applyNoteTextSize(noteTextSize);
      const preferences: LibraryPreferences = {
        viewMode: this.viewMode(),
        sort: this.sort(),
        pageSize: this.pageSize(),
        sidebarExpanded: this.sidebarExpanded(),
        groupByWork: this.groupByWork(),
        assistantEnabled: this.assistantEnabled(),
        assistantVoiceEnabled: this.assistantVoiceEnabled(),
        ...(noteTextSize !== DEFAULT_PREFERENCES.noteTextSize ? { noteTextSize } : {}),
      };

      this.writePreferences(preferences);
    });
  }

  setViewMode(mode: LibraryPreferences['viewMode']): void {
    if (VALID_VIEW_MODES.includes(mode)) this.viewMode.set(mode);
  }

  setSort(sort: BookSort): void {
    if (VALID_SORTS.includes(sort)) this.sort.set(sort);
  }

  setSidebarExpanded(expanded: boolean): void {
    this.sidebarExpanded.set(expanded);
  }

  setGroupByWork(grouped: boolean): void {
    this.groupByWork.set(grouped);
  }

  setAssistantEnabled(enabled: boolean): void {
    this.assistantEnabled.set(enabled);
  }

  setAssistantVoiceEnabled(enabled: boolean): void {
    this.assistantVoiceEnabled.set(enabled);
  }

  setNoteTextSize(size: NoteTextSize): void {
    if (VALID_NOTE_TEXT_SIZES.includes(size)) {
      this.noteTextSize.set(size);
      this.applyNoteTextSize(size);
    }
  }

  getActiveEditionId(workId: string | null | undefined, fallbackBookId: string): string {
    return workId ? (this.workEditions.get(workId) ?? fallbackBookId) : fallbackBookId;
  }

  setActiveEditionId(workId: string | null | undefined, bookId: string): void {
    if (!workId || this.workEditions.get(workId) === bookId) return;

    this.workEditions.set(workId, bookId);
    this.writeWorkEditions();
  }

  private hydrate(): void {
    const stored = this.readStorage(LIBRARY_PREFERENCES_STORAGE_KEY);
    if (stored !== null) {
      const parsed = this.parsePreferences(stored);
      if (parsed) this.apply(parsed);
      return;
    }

    // Before preferences were consolidated, only the view mode was saved.
    // Migrate that one value into an otherwise-default preferences object.
    const legacyViewMode = this.readStorage(LEGACY_VIEW_MODE_STORAGE_KEY);
    if (legacyViewMode === 'grid' || legacyViewMode === 'list') {
      this.viewMode.set(legacyViewMode);
    }
  }

  private parsePreferences(raw: string): Required<LibraryPreferences> | null {
    try {
      const value: unknown = JSON.parse(raw);
      if (!this.isRecord(value)) return null;

      const {
        viewMode,
        sort,
        pageSize,
        sidebarExpanded,
        groupByWork,
        assistantEnabled,
        assistantVoiceEnabled,
        noteTextSize,
      } = value;
      if (
        !this.isViewMode(viewMode) ||
        !this.isBookSort(sort) ||
        !this.isPageSize(pageSize) ||
        typeof sidebarExpanded !== 'boolean' ||
        typeof groupByWork !== 'boolean'
      ) {
        return null;
      }

      // `assistantEnabled` was added after this object shipped. A stored object
      // without it is a valid older preference set, not a corrupt one: default
      // it off rather than silently opting an existing browser into AI.
      if (assistantEnabled !== undefined && typeof assistantEnabled !== 'boolean') {
        return null;
      }
      if (assistantVoiceEnabled !== undefined && typeof assistantVoiceEnabled !== 'boolean') {
        return null;
      }
      if (noteTextSize !== undefined && !this.isNoteTextSize(noteTextSize)) {
        return null;
      }

      return {
        viewMode,
        sort,
        pageSize,
        sidebarExpanded,
        groupByWork,
        assistantEnabled: assistantEnabled ?? DEFAULT_PREFERENCES.assistantEnabled,
        assistantVoiceEnabled:
          assistantVoiceEnabled ?? DEFAULT_PREFERENCES.assistantVoiceEnabled,
        noteTextSize: noteTextSize ?? DEFAULT_PREFERENCES.noteTextSize,
      };
    } catch {
      return null;
    }
  }

  private apply(preferences: Required<LibraryPreferences>): void {
    this.viewMode.set(preferences.viewMode);
    this.sort.set(preferences.sort);
    this.pageSize.set(preferences.pageSize);
    this.sidebarExpanded.set(preferences.sidebarExpanded);
    this.groupByWork.set(preferences.groupByWork);
    this.assistantEnabled.set(preferences.assistantEnabled);
    this.assistantVoiceEnabled.set(preferences.assistantVoiceEnabled);
    this.noteTextSize.set(preferences.noteTextSize);
    this.applyNoteTextSize(preferences.noteTextSize);
  }

  private applyNoteTextSize(size: NoteTextSize): void {
    if (typeof document !== 'undefined') {
      document.documentElement.style.setProperty('--note-reading-size', NOTE_TEXT_SIZE_PX[size]);
    }
  }

  private hydrateWorkEditions(): void {
    const stored = this.readStorage(WORK_EDITIONS_STORAGE_KEY);
    if (stored === null) return;

    try {
      const value: unknown = JSON.parse(stored);
      if (!this.isRecord(value)) return;

      for (const [workId, bookId] of Object.entries(value)) {
        if (typeof bookId === 'string') this.workEditions.set(workId, bookId);
      }
    } catch {
      // Ignore malformed edition memory and start with an empty map.
    }
  }

  private writePreferences(preferences: LibraryPreferences): void {
    try {
      localStorage.setItem(LIBRARY_PREFERENCES_STORAGE_KEY, JSON.stringify(preferences));
    } catch {
      // Persistence is best effort; private browsing and storage quotas can
      // make localStorage unavailable without affecting the library UI.
    }
  }

  private writeWorkEditions(): void {
    try {
      localStorage.setItem(WORK_EDITIONS_STORAGE_KEY, JSON.stringify(Object.fromEntries(this.workEditions)));
    } catch {
      // Persistence is best effort; private browsing and storage quotas can
      // make localStorage unavailable without affecting the library UI.
    }
  }

  private readStorage(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  private isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
  }

  private isViewMode(value: unknown): value is LibraryPreferences['viewMode'] {
    return typeof value === 'string' && VALID_VIEW_MODES.includes(value as LibraryPreferences['viewMode']);
  }

  private isBookSort(value: unknown): value is BookSort {
    return typeof value === 'string' && VALID_SORTS.includes(value as BookSort);
  }

  private isPageSize(value: unknown): value is number {
    return typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= 100;
  }

  private isNoteTextSize(value: unknown): value is NoteTextSize {
    return typeof value === 'string' && VALID_NOTE_TEXT_SIZES.includes(value as NoteTextSize);
  }
}
