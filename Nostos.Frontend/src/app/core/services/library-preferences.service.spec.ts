import { TestBed } from '@angular/core/testing';

import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  LIBRARY_PREFERENCES_STORAGE_KEY,
  LibraryPreferences,
  LibraryPreferencesService,
  WORK_EDITIONS_STORAGE_KEY,
} from './library-preferences.service';
import { BookSort } from '../dtos/book.enums';

const STORED: LibraryPreferences = {
  viewMode: 'list',
  sort: BookSort.Title,
  pageSize: 50,
  sidebarExpanded: false,
  groupByWork: false,
  assistantEnabled: true,
  assistantVoiceEnabled: false,
};

const LEGACY_VIEW_MODE_STORAGE_KEY = 'nostos.viewMode';

function storedPreferences(overrides: Partial<Record<keyof LibraryPreferences, unknown>> = {}): string {
  return JSON.stringify({ ...STORED, ...overrides });
}

describe('LibraryPreferencesService', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  const create = () => TestBed.inject(LibraryPreferencesService);

  it('starts from the defaults when nothing is stored', () => {
    const service = create();

    expect(service.viewMode()).toBe('grid');
    expect(service.sort()).toBe(BookSort.LastRead);
    expect(service.pageSize()).toBe(20);
    expect(service.sidebarExpanded()).toBe(true);
    expect(service.groupByWork()).toBe(true);
    expect(service.assistantEnabled()).toBe(false);
    expect(service.assistantVoiceEnabled()).toBe(true);
    expect(service.hasLoadedBooks()).toBe(false);
  });

  it('hydrates every stored preference', () => {
    localStorage.setItem(LIBRARY_PREFERENCES_STORAGE_KEY, JSON.stringify(STORED));
    const service = create();

    expect(service.viewMode()).toBe('list');
    expect(service.sort()).toBe(BookSort.Title);
    expect(service.pageSize()).toBe(50);
    expect(service.sidebarExpanded()).toBe(false);
    expect(service.groupByWork()).toBe(false);
    expect(service.assistantEnabled()).toBe(true);
    expect(service.assistantVoiceEnabled()).toBe(false);
  });

  it('migrates the legacy view mode when no preferences object exists', () => {
    localStorage.setItem(LEGACY_VIEW_MODE_STORAGE_KEY, 'list');
    const service = create();

    expect(service.viewMode()).toBe('list');
    // The rest of the migrated set is the default, not the legacy value.
    expect(service.pageSize()).toBe(20);
    expect(service.groupByWork()).toBe(true);
  });

  it('prefers the consolidated object over the legacy view mode', () => {
    localStorage.setItem(LEGACY_VIEW_MODE_STORAGE_KEY, 'list');
    localStorage.setItem(LIBRARY_PREFERENCES_STORAGE_KEY, JSON.stringify({ ...STORED, viewMode: 'grid' }));

    expect(create().viewMode()).toBe('grid');
  });

  it('ignores a legacy view mode that is not one of the two modes', () => {
    localStorage.setItem(LEGACY_VIEW_MODE_STORAGE_KEY, 'table');

    expect(create().viewMode()).toBe('grid');
  });

  it('defaults the assistant switches for a stored object that predates them', () => {
    localStorage.setItem(
      LIBRARY_PREFERENCES_STORAGE_KEY,
      JSON.stringify({
        viewMode: 'list',
        sort: BookSort.Rating,
        pageSize: 10,
        sidebarExpanded: false,
        groupByWork: false,
      }),
    );
    const service = create();

    // An older browser is not opted into the assistant by being older.
    expect(service.assistantEnabled()).toBe(false);
    expect(service.assistantVoiceEnabled()).toBe(true);
    expect(service.viewMode()).toBe('list');
  });

  it.each([
    ['malformed JSON', '{not json'],
    ['an array', '[]'],
    ['a null view mode', storedPreferences({ viewMode: null })],
    ['an unknown view mode', storedPreferences({ viewMode: 'table' })],
    ['an unknown sort', storedPreferences({ sort: 'author' })],
    ['a zero page size', storedPreferences({ pageSize: 0 })],
    ['a page size above the cap', storedPreferences({ pageSize: 101 })],
    ['a fractional page size', storedPreferences({ pageSize: 12.5 })],
    ['a non-boolean sidebar', storedPreferences({ sidebarExpanded: 'yes' })],
    ['a non-boolean grouping', storedPreferences({ groupByWork: 1 })],
    ['a non-boolean assistant switch', storedPreferences({ assistantEnabled: 'on' })],
    ['a non-boolean voice switch', storedPreferences({ assistantVoiceEnabled: 'on' })],
  ])('falls back to every default for %s', (_case, raw) => {
    localStorage.setItem(LIBRARY_PREFERENCES_STORAGE_KEY, raw);
    const service = create();

    expect(service.viewMode()).toBe('grid');
    expect(service.sort()).toBe(BookSort.LastRead);
    expect(service.pageSize()).toBe(20);
    expect(service.sidebarExpanded()).toBe(true);
    expect(service.groupByWork()).toBe(true);
    expect(service.assistantEnabled()).toBe(false);
  });

  it('accepts the smallest page size', () => {
    localStorage.setItem(LIBRARY_PREFERENCES_STORAGE_KEY, storedPreferences({ pageSize: 1 }));

    expect(create().pageSize()).toBe(1);
  });

  it('accepts the largest page size', () => {
    localStorage.setItem(LIBRARY_PREFERENCES_STORAGE_KEY, storedPreferences({ pageSize: 100 }));

    expect(create().pageSize()).toBe(100);
  });

  it('refuses a view mode and sort it does not know', () => {
    const service = create();
    service.setViewMode('list');
    service.setSort(BookSort.Title);

    service.setViewMode('table' as unknown as LibraryPreferences['viewMode']);
    service.setSort('author' as unknown as BookSort);

    expect(service.viewMode()).toBe('list');
    expect(service.sort()).toBe(BookSort.Title);
  });

  it('writes the whole preference set whenever one value changes', () => {
    const service = create();

    service.setViewMode('list');
    service.setSort(BookSort.RecentlyFinished);
    service.pageSize.set(40);
    service.setSidebarExpanded(false);
    service.setGroupByWork(false);
    service.setAssistantEnabled(true);
    service.setAssistantVoiceEnabled(false);
    TestBed.flushEffects();

    expect(JSON.parse(localStorage.getItem(LIBRARY_PREFERENCES_STORAGE_KEY) as string)).toEqual({
      viewMode: 'list',
      sort: BookSort.RecentlyFinished,
      pageSize: 40,
      sidebarExpanded: false,
      groupByWork: false,
      assistantEnabled: true,
      assistantVoiceEnabled: false,
    });
  });

  it('keeps the session-only "has loaded books" flag out of storage', () => {
    const service = create();
    service.hasLoadedBooks.set(true);
    TestBed.flushEffects();

    expect(service.hasLoadedBooks()).toBe(true);
    expect(localStorage.getItem(LIBRARY_PREFERENCES_STORAGE_KEY) ?? '').not.toContain(
      'hasLoadedBooks',
    );
  });

  it('survives storage that refuses to be read or written', () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });

    const service = create();
    service.setViewMode('list');
    expect(() => TestBed.flushEffects()).not.toThrow();
    expect(service.viewMode()).toBe('list');

    getItem.mockRestore();
    setItem.mockRestore();
  });

  describe('active edition per work', () => {
    it('remembers the edition chosen for a work', () => {
      const service = create();

      expect(service.getActiveEditionId('w1', 'b1')).toBe('b1');

      service.setActiveEditionId('w1', 'b2');
      expect(service.getActiveEditionId('w1', 'b1')).toBe('b2');
      expect(JSON.parse(localStorage.getItem(WORK_EDITIONS_STORAGE_KEY) as string)).toEqual({
        w1: 'b2',
      });
    });

    it('hydrates the remembered editions', () => {
      localStorage.setItem(WORK_EDITIONS_STORAGE_KEY, JSON.stringify({ w1: 'b2', w2: 'b9' }));
      const service = create();

      expect(service.getActiveEditionId('w1', 'b1')).toBe('b2');
      expect(service.getActiveEditionId('w2', 'b1')).toBe('b9');
      expect(service.getActiveEditionId('w3', 'b1')).toBe('b1');
    });

    it('falls back to the current book without a work', () => {
      const service = create();

      expect(service.getActiveEditionId(null, 'b1')).toBe('b1');
      expect(service.getActiveEditionId(undefined, 'b1')).toBe('b1');
      service.setActiveEditionId(null, 'b2');
      expect(localStorage.getItem(WORK_EDITIONS_STORAGE_KEY)).toBeNull();
    });

    it('does not rewrite an edition that is already remembered', () => {
      const service = create();
      service.setActiveEditionId('w1', 'b2');
      const setItem = vi.spyOn(Storage.prototype, 'setItem');

      service.setActiveEditionId('w1', 'b2');

      expect(setItem).not.toHaveBeenCalled();
      setItem.mockRestore();
    });

    it('ignores malformed edition memory and starts empty', () => {
      localStorage.setItem(WORK_EDITIONS_STORAGE_KEY, '{not json');
      expect(create().getActiveEditionId('w1', 'b1')).toBe('b1');

      localStorage.setItem(WORK_EDITIONS_STORAGE_KEY, JSON.stringify({ w1: 7, w2: 'b2' }));
      expect(create().getActiveEditionId('w1', 'b1')).toBe('b1');
    });
  });
});
