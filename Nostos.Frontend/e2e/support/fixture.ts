/**
 * Shared Task 13 E2E helpers: fixture state access + thin REST client for the
 * supported Nostos API surface (contracts mirror Nostos.Shared/Dtos and
 * Nostos.Product/Endpoints — nothing here invents endpoints).
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';

export interface FixtureState {
  baseUrl: string;
  port: number;
  token: string;
  tempDir: string;
  repoRoot: string;
  backendPid: number | null;
  startedAt: string;
}

const STATE_FILE = path.join(__dirname, 'fixture-state.json');

export function loadFixture(): FixtureState {
  return JSON.parse(readFileSync(STATE_FILE, 'utf8')) as FixtureState;
}

/** Stable client identity used for REST seeding (distinct from the MCP fixed id). */
export const SEED_CLIENT = 'task13-e2e';

export function seedKey(scope: string, run: string, name: string): string {
  return `task13-${scope}-${run}-${name}`;
}

export function newRunId(): string {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

export async function apiGet<T = any>(baseUrl: string, urlPath: string): Promise<T> {
  const res = await fetch(`${baseUrl}${urlPath}`);
  if (!res.ok) throw new Error(`GET ${urlPath} -> ${res.status}: ${await res.text()}`);
  return (await res.json()) as T;
}

export async function apiPost<T = any>(baseUrl: string, urlPath: string, body?: unknown): Promise<T> {
  const res = await fetch(`${baseUrl}${urlPath}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${urlPath} -> ${res.status}: ${await res.text()}`);
  return (await res.json()) as T;
}

export async function apiPatch<T = any>(baseUrl: string, urlPath: string, body: unknown): Promise<T> {
  const res = await fetch(`${baseUrl}${urlPath}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PATCH ${urlPath} -> ${res.status}: ${await res.text()}`);
  return (await res.json()) as T;
}

export interface BookDto {
  id: string;
  title: string;
  author: string | null;
  type: string;
  categories: string | null;
}

