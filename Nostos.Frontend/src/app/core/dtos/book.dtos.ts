// Define the valid types to match your backend Discriminator
export type BookType = 'physical' | 'ebook' | 'audiobook';

// --- NEW: Pagination Wrapper ---
export interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface LibraryStatusCountsDto {
  all: number;
  notStarted: number;
  reading: number;
  favorites: number;
  finished: number;
  unsorted: number;
  audiobooks?: number;
  ebooks?: number;
  pdfs?: number;
}

export interface BookChapter {
  title: string;
  startTime: number;
}

/** One track of a multi-track audiobook. `duration` is in seconds. */
export interface BookTrack {
  number: number;
  title: string;
  duration: number;
  bytes: number;
  contentType: string;
}

export interface EditionSummaryDto {
  id: string;
  type: 'ebook' | 'audiobook' | 'physical' | string;
  format?: string;
  progressPercent: number;
  finishedAt?: string | null;
  lastReadAt?: string | null;
  hasFile: boolean;
  fileName?: string | null;
  narrator?: string | null;
  duration?: string | null;
  edition?: string | null;
  /** The book's own title, so a sibling can be named rather than inferred. */
  title?: string;
  author?: string | null;
}

export interface Book {
  id: string;
  title: string;
  subtitle: string | null;

  // Authorship
  author: string | null;
  editor: string | null;
  translator: string | null;
  narrator: string | null;

  description: string | null;

  // Polymorphic Fields
  type: BookType;
  edition: string | null;
  asin: string | null;
  duration: string | null;

  isbn: string | null;
  publisher: string | null;
  placeOfPublication: string | null;
  publishedDate: string | null;
  pageCount: number | null;
  language: string | null;
  categories: string | null;
  series: string | null;
  volumeNumber: string | null;
  createdAt: string;
  hasFile: boolean;
  fileName: string | null;
  coverUrl: string | null;

  /**
   * Every collection this book belongs to. The sole source of truth — the
   * former singular `collectionId` is gone from responses, because a one-slot
   * field could not represent a book that sits in more than one collection.
   */
  collectionIds: string[];

  lastLocation: string | null;
  progressPercent: number;
  lastReadAt: string | null;

  // Metadata Fields
  rating: number;
  isFavorite: boolean;
  personalReview: string | null;
  finishedAt: string | null;

  workId?: string;
  editionCount?: number;
  otherEditions?: EditionSummaryDto[];

  chapters?: BookChapter[]; // <--- Add this

  /**
   * Where this book's file came from, when it was imported from an external
   * source rather than uploaded by hand. Absent otherwise.
   */
  source?: BookSource | null;

  /**
   * Life-cycle status: 0 = Ready, 1 = Downloading, 2 = Transcoding, 3 = Failed, 4 = UploadPending.
   */
  status?: number;
  statusMessage?: string | null;
}

/**
 * Provenance of an imported book. `rightsStatement` carries the SOURCE's own
 * wording — it is quoted, not a claim by Nostos that the work is unrestricted
 * everywhere.
 */
export interface BookSource {
  providerId: string;
  providerDisplayName: string;
  externalId: string;
  sourceUrl: string | null;
  assetFormat: string | null;
  rightsStatement: string | null;
  acquiredAt: string;
}

// --- MANUAL WORK MEMBERSHIP (issue #143) ---
// The override for automatic multi-edition grouping. Only WorkId moves; every
// book-level value (file, progress, notes, rating, review, collections,
// metadata) belongs to the book and is untouched.

/** Result of a link/unlink: where the book ended up, and the group's new size. */
export interface WorkMembershipDto {
  bookId: string;
  workId: string;
  editionCount: number;
  /** Set when a merge emptied a work and it was removed. */
  removedWorkId?: string | null;
}

/** A candidate row in the "link this book to…" picker. */
export interface LinkableBookDto {
  id: string;
  title: string;
  author: string | null;
  workId?: string | null;
  editionCount?: number;
}

export interface CreateBookDto {
  type: BookType;
  title: string;
  subtitle: string | null;

  author: string | null;
  editor: string | null;
  translator: string | null;
  narrator: string | null;

  description: string | null;

  edition: string | null;
  asin: string | null;
  duration: string | null;

  isbn: string | null;
  publisher: string | null;
  placeOfPublication: string | null;
  publishedDate: string | null;
  pageCount: number | null;
  language: string | null;
  categories: string | null;
  series: string | null;
  volumeNumber: string | null;

  /**
   * Membership set — the authoritative shape.
   */
  collectionIds?: string[];

  /** Explicitly marks create as the first half of a local-file upload. */
  fileUploadExpected?: boolean;

  // Initial Metadata
  rating?: number;
  isFavorite?: boolean;
  personalReview?: string | null;
  finishedAt?: string | null;
}

export interface UpdateBookDto {
  title?: string;
  subtitle?: string | null;

  author?: string | null;
  editor?: string | null;
  translator?: string | null;
  narrator?: string | null;

  description?: string | null;

  edition?: string | null;
  asin?: string | null;
  duration?: string | null;

  isbn?: string | null;
  publisher?: string | null;
  placeOfPublication?: string | null;
  publishedDate?: string | null;
  pageCount?: number | null;
  language?: string | null;
  categories?: string | null;
  series?: string | null;
  volumeNumber?: string | null;

  /**
   * Full replacement membership set. Sending `[]` clears every collection —
   * which the singular nullable `collectionId` could never express, so
   * "remove from collection" did nothing. Omit the field to leave membership
   * untouched (e.g. a rating-only update).
   */
  collectionIds?: string[];

  // Update Fields
  rating?: number;
  isFavorite?: boolean;
  personalReview?: string | null;
  finishedAt?: string | null;
  isFinished?: boolean;
}

export interface UpdateProgressDto {
  location: string;
  percentage: number;
}
