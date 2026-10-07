// --- EXTERNAL CONTENT PROVIDERS (issues #166 / #167) ---
// The wire shape of the provider/acquisition boundary. Deliberately its own
// small vocabulary rather than a reuse of the library DTOs: these describe
// something that is NOT yet a Nostos book, which is what keeps provider
// topics out of the library contract.
//
// Note there is no URL field anywhere a client can *send*: the client names a
// provider, an item and an asset, and the server resolves the location.

export interface ProviderSummary {
  id: string;
  displayName: string;
  /** Lower-case capability names, e.g. ['search', 'itemretrieval', 'ebookacquisition']. */
  capabilities: string[];
  /** The source's own rights wording, shown as-is. Never a Nostos claim. */
  rightsNotice: string | null;
}

export interface ProviderAsset {
  id: string;
  kind: 'ebook' | 'audiobook' | string;
  label: string;
  sourceFormat: string | null;
  sizeBytes: number | null;
  isPreferred: boolean;
}

export interface ProviderItem {
  providerId: string;
  externalId: string;
  /** Available even on thin search results; never infer this from providerId. */
  mediaKind: 'ebook' | 'audiobook';
  title: string;
  subtitle: string | null;
  author: string | null;
  description: string | null;
  language: string | null;
  publisher: string | null;
  publishedDate: string | null;
  categories: string | null;
  narrator: string | null;
  duration: string | null;
  pageCount: number | null;
  assets: ProviderAsset[];
  /** Nostos-relative URL that proxies the artwork, or null when the source has none. */
  coverUrl: string | null;
  sourceUrl: string | null;
  rightsStatement: string | null;
  /** Source parts/tracks, e.g. the number of sections of a LibriVox recording. */
  partCount: number | null;
}

export interface ProviderSearchResult {
  items: ProviderItem[];
  hasMore: boolean;
  /**
   * A provider-supplied note about a thin result set. Several catalogues can
   * only match a whole title or an author surname, so an unexplained empty list
   * would read as a broken search.
   */
  notice: string | null;
}

export interface ProviderDiscoverySourceStatus {
  providerId: string;
  displayName: string;
  succeeded: boolean;
  notice: string | null;
  errorCode: string | null;
}

export interface ProviderDiscoverySearchResult {
  items: ProviderItem[];
  hasMore: boolean;
  sources: ProviderDiscoverySourceStatus[];
}

/**
 * The user's own edits to what the source says, sent with an import started from
 * a prefilled form.
 *
 * `null` (or absent) keeps the source's value; a string replaces it; an empty
 * string clears the field. Send only what the user actually changed — echoing
 * every field back would pin them all to this moment, so a later correction at
 * the source could never reach the library.
 */
export interface ProviderMetadataOverrides {
  title?: string | null;
  subtitle?: string | null;
  author?: string | null;
  description?: string | null;
  language?: string | null;
  publisher?: string | null;
  publishedDate?: string | null;
  categories?: string | null;
  narrator?: string | null;
  duration?: string | null;
  pageCount?: number | null;
}

export interface ProviderAcquireRequest {
  externalId: string;
  assetId?: string | null;
  collectionIds?: string[] | null;
  includeCover?: boolean;
  /** Present only when the user reviewed and edited the metadata first. */
  metadataOverrides?: ProviderMetadataOverrides | null;
}

export type AcquisitionState = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';

export interface ProviderAcquisition {
  jobId: string;
  state: AcquisitionState;
  /** queued | starting | resolving | checking | downloading | validating | assembling | importing | done | failed */
  stage: string;
  percent: number;
  detail: string | null;
  providerId: string;
  externalId: string;
  assetId: string | null;
  bookId: string | null;
  errorCode: string | null;
  message: string | null;
  createdAt: string;
  updatedAt: string;
}

export const ACQUISITION_FINISHED_STATES: ReadonlySet<AcquisitionState> = new Set([
  'succeeded',
  'failed',
  'cancelled',
]);

// --- PROVIDER ENABLEMENT (issue #774) ---
// The Settings management view. Deliberately separate from ProviderSummary:
// that stays the enabled-only Add Book consumer view, while this one lists
// disabled sources too so they can be re-enabled.

export interface ProviderSettingsItem {
  id: string;
  displayName: string;
  /** Short coverage copy, or null when the source offers none. */
  description: string | null;
  /** Lower-case capability names, e.g. ['search', 'ebookacquisition']. */
  capabilities: string[];
  rightsNotice: string | null;
  enabled: boolean;
  /** The source's own declaration, so the UI can say "off by default". */
  enabledByDefault: boolean;
}

export interface ProviderSettingsResponse {
  providers: ProviderSettingsItem[];
}

/** A missing `enabled` would be a client error; always send a real boolean. */
export interface ProviderPreferenceUpdate {
  enabled: boolean;
}
