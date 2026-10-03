/**
 * The AI provider settings contract, as frozen in the plan
 * (`PLAN-AI-PROVIDER-SETTINGS.md`). The server owns the values; the browser
 * only ever learns EFFECTIVE values plus two booleans about the key.
 *
 * The API key itself is never in any response — `hasKey` says one is usable and
 * `keyFromServerEnv` says the effective one comes from the server's environment
 * rather than a value stored in the database. There is deliberately no field
 * that could carry a key back to the client.
 */

/** The independently configurable providers behind one "AI provider" card. */
export type AiProviderKind = 'llm' | 'stt' | 'embedding';

/** One provider's effective settings, as `GET`/`PUT` report them. */
export interface AiProviderSection {
  /** Whether the provider is switched on. Voice and embeddings are user-toggleable; the LLM is not. */
  enabled: boolean;
  /** Effective endpoint, including any `/v1` suffix the provider needs. */
  baseUrl: string;
  /** Effective model id sent to the endpoint. */
  model: string;
  /** True when a key is usable, whether it is stored or comes from the environment. */
  hasKey: boolean;
  /** True when the effective key is the server's environment variable, not a stored one. */
  keyFromServerEnv: boolean;
}

/** The whole card's effective state. */
export interface AiProviderSettings {
  llm: AiProviderSection;
  stt: AiProviderSection;
  /**
   * Passage embeddings for semantic retrieval. Absent when the host manages
   * embeddings itself (or predates them); the card then shows no section.
   */
  embedding?: AiProviderSection;
}

/**
 * One section of a `PUT`. Fields are optional: omitted means "leave unchanged".
 * `apiKey` semantics are the important part — omitted/`null` leaves the stored
 * key alone, `""` clears it, and a non-empty value stores it.
 */
export interface AiProviderSectionUpdate {
  enabled?: boolean;
  baseUrl?: string;
  model?: string;
  apiKey?: string;
}

/** `PUT /api/settings/ai-provider` body: only the sections the user changed. */
export interface AiProviderUpdate {
  llm?: AiProviderSectionUpdate;
  stt?: AiProviderSectionUpdate;
  embedding?: AiProviderSectionUpdate;
}

/** `POST .../models` body. `baseUrl`/`apiKey` omitted fall back to effective values. */
export interface AiProviderModelsRequest {
  kind: AiProviderKind;
  baseUrl?: string;
  apiKey?: string;
}

/** `POST .../models` response: the ids the endpoint advertised. */
export interface AiProviderModelsResponse {
  models: string[];
}

/** `POST .../test` body. Omitted fields fall back to effective values. */
export interface AiProviderTestRequest {
  kind: AiProviderKind;
  baseUrl?: string;
  model?: string;
  apiKey?: string;
}

/**
 * `POST .../test` response. The route always answers HTTP 200 so a failure can
 * carry a message; branch on `ok`, never on the status code.
 */
export type AiProviderTestResult =
  | { ok: true; detail: string }
  | { ok: false; error: string };
