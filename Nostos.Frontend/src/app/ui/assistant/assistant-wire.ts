/**
 * Wire shapes shared by the assistant's HTTP transport and the session ledger.
 *
 * Kept apart from `AssistantService` so the stream transport, the session
 * serializer and the service itself can name the same request without one
 * importing the whole service graph.
 */
import type { AssistantHistoryMessage } from './assistant.service';

/** Mirrors the backend `AssistantContextDto` field-for-field. */
export interface AssistantContextDto {
  surface: string;
  route: string;
  bookId: string | null;
  bookTitle: string | null;
  bookFormat: string | null;
  readerType: string | null;
  epubCfi: string | null;
  pdfPage: number | null;
  audioTimestamp: number | null;
  audioChapter: string | null;
  selectedText: string | null;
  brainReviewNoteId: string | null;
  topic: string | null;
  collectionId: string | null;
  anchor: { kind: string; value: string | null; verified: boolean } | null;
  /**
   * Legacy wire compatibility only. V3 continuation answers travel as the real
   * message; this stays null on new-client follow-ups and the server resolves
   * the book from its continuation state.
   */
  captureBookTitle: string | null;
}

/** One remembered message; mirrors the backend's history contract. */
export interface AssistantTurnRequestDto {
  /** Compatibility aliases: equal to conversationId / turnId for new clients. */
  clientId: string;
  idempotencyKey: string;
  /** Stable for the current working Ask Nostos conversation. */
  conversationId: string;
  /** Stable identity for this logical user turn, reused on delivery retry. */
  turnId: string;
  message: string;
  context: AssistantContextDto;
  /** Server-held deterministic continuation answered by this real user turn. */
  continuationId: string | null;
  continuationSkipped: boolean;
  /**
   * The completed session ledger from BEFORE this logical turn. The browser
   * sends it intact; the server owns the budgeted selection that reaches the
   * model, so this field is not itself a provider-context contract.
   */
  history: AssistantHistoryMessage[];
}
