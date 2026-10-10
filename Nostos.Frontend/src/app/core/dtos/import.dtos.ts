import { AcquisitionState } from './provider.dtos';

/**
 * One row of the "Imports in Progress" surface.
 *
 * Deliberately NOT a library book: it describes work in flight, which must stay
 * visible whatever the library is filtered or sorted by. Two kinds of entry
 * share the shape — a live job in this server process (`source: 'job'`), and a
 * book row whose import a server restart interrupted (`source: 'reconciled'`,
 * which arrives already terminal).
 */
export interface ImportActivity {
  /** Stable id: the job id, or the book id when the entry is a reconciled one. */
  id: string;
  source: 'job' | 'reconciled';
  state: AcquisitionState;
  stage: string;
  /**
   * 0-100, already capped at 99 by the server unless the state is Succeeded —
   * the client must never render 100% for an import that has not landed.
   */
  percent: number;
  detail: string | null;
  providerId: string | null;
  externalId: string | null;
  assetId: string | null;
  bookId: string | null;
  /** The book row's title, present from the moment the user confirmed. */
  title: string | null;
  author: string | null;
  /** Nostos-relative cover URL for the book row, or null while it has none. */
  coverUrl: string | null;
  errorCode: string | null;
  message: string | null;
  createdAt: string;
  updatedAt: string;
  /** False when this entry cannot be re-imported (no provider/external id). */
  canRetry: boolean;
}

/** States after which nothing about an entry will change again. */
export const IMPORT_TERMINAL_STATES: ReadonlySet<AcquisitionState> = new Set([
  'succeeded',
  'failed',
  'cancelled',
]);

/** States that are still doing work. */
export const IMPORT_IN_FLIGHT_STATES: ReadonlySet<AcquisitionState> = new Set([
  'queued',
  'running',
]);

export function isImportTerminal(activity: ImportActivity): boolean {
  return IMPORT_TERMINAL_STATES.has(activity.state);
}

export function isImportInFlight(activity: ImportActivity): boolean {
  return IMPORT_IN_FLIGHT_STATES.has(activity.state);
}

/**
 * The short badge text for an entry.
 *
 * The server's stages are deliberately few and provider-agnostic, and several of
 * them are the same thing to a reader: resolving a manifest, checking free space
 * and fetching bytes are all "downloading" from where they sit. Two labels the
 * user actually cares about — Downloading and Saving — plus the terminal ones.
 * (`transcoding` and `assembling` are stages older servers reported.)
 */
export function importStageLabel(activity: ImportActivity): string {
  switch (activity.state) {
    case 'succeeded':
      return 'Done';
    case 'failed':
      return 'Failed';
    case 'cancelled':
      return 'Cancelled';
    default:
      break;
  }

  switch (activity.stage) {
    case 'transcoding':
    case 'assembling':
    case 'importing':
      return 'Saving';
    case 'queued':
    case 'starting':
      return 'Queued';
    case 'done':
      return 'Done';
    default:
      return 'Downloading';
  }
}
