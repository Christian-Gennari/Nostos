/**
 * The settings sections a `?section=` link may open, and the spellings people
 * guess for them.
 *
 * Kept apart from `SettingsComponent` because the section components are the
 * same router-facing vocabulary: a card that jumps to another section emits one
 * of these values, and importing the page component from a section component
 * would be a cycle.
 */
export type SettingsSection =
  | 'general'
  | 'library'
  | 'import'
  | 'assistant'
  | 'connections'
  | 'external'
  | 'apps'
  | 'devices'
  | 'data'
  | 'account';

/** Sections a `?section=` link may open. `data` is its own route, `/settings/library`. */
export const LINKABLE_SECTIONS: readonly SettingsSection[] = [
  'general',
  'library',
  'import',
  'assistant',
  'connections',
  'external',
  'apps',
  'devices',
  'account',
];

/** Spellings people reasonably guess for a section key. */
export const SECTION_ALIASES: Readonly<Record<string, SettingsSection>> = {
  'external-media': 'external',
  'connected-apps': 'apps',
  'carrel': 'devices',
  'backups': 'data',
  'backups-data': 'data',
};
