/**
 * Client/server contract parity, checked against the merged backend source
 * itself (the mirror image of `MigrationHttpDtoParityTests` in
 * `Nostos.Backend.Tests/Portability`).
 *
 * These tests read the C# files as text so a renamed field, enum member, error
 * code, header or limit on either side fails here even when both sides compile
 * independently. Vite resolves the `?raw` imports at build time; TypeScript
 * learns their shape from `src/vite-glob.d.ts`.
 */

import { describe, expect, it } from 'vitest';

import {
  MIGRATION_LIMITS,
  SERVER_MIGRATION_ERROR_CODES,
} from './migration-http.dtos';
import {
  CHUNK_HASH_HEADER_NAME,
  MIGRATION_BASE_PATH,
} from '../services/http-library-transfer-transport';

const appSources = import.meta.glob('../../**/*.ts', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;

const backendSources = import.meta.glob(
  [
    '../../../../../Nostos.Product/Endpoints/MigrationHttpContracts.cs',
    '../../../../../Nostos.Product/Endpoints/MigrationEndpoints.cs',
    '../../../../../Nostos.Product/Services/Portability/MigrationContracts.cs',
    '../../../../../Nostos.Backend/Middleware/LibraryMaintenanceMiddleware.cs',
  ],
  { query: '?raw', import: 'default', eager: true },
) as Record<string, string>;

function backendSource(fileName: string): string {
  const key = Object.keys(backendSources).find((path) => path.endsWith(`/${fileName}`));
  if (!key) throw new Error(`Backend source ${fileName} was not found.`);
  return backendSources[key];
}

const HTTP_CONTRACTS = backendSource('MigrationHttpContracts.cs');
const ENDPOINTS = backendSource('MigrationEndpoints.cs');
const CONTRACT_MODELS = backendSource('MigrationContracts.cs');
const MIDDLEWARE = backendSource('LibraryMaintenanceMiddleware.cs');

const TS_SOURCE = appSources['./migration-http.dtos.ts'];
if (!TS_SOURCE) throw new Error('migration-http.dtos.ts raw source was not found.');

function camelCase(name: string): string {
  return name.charAt(0).toLowerCase() + name.slice(1);
}

function splitTopLevel(text: string, separator: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let current = '';
  for (const char of text) {
    if (char === '(' || char === '<' || char === '[') depth += 1;
    else if (char === ')' || char === '>' || char === ']') depth -= 1;
    if (char === separator && depth === 0) {
      parts.push(current);
      current = '';
      continue;
    }
    current += char;
  }
  if (current.trim().length > 0) parts.push(current);
  return parts;
}

function recordParameters(source: string, record: string): string[] {
  // Records may end in `);` or open a body with computed serialized
  // properties (`IsAllowed`, `TotalRows`), which the pair table accounts for.
  const match = source.match(new RegExp(`record ${record}\\(([\\s\\S]*?)\\)\\s*(?:;|\\{)`));
  if (!match) throw new Error(`C# record ${record} was not found in the backend source.`);
  return splitTopLevel(match[1], ',').map((parameter) => {
    const withoutDefault = parameter.split('=')[0].trim();
    const tokens = withoutDefault.split(/[\s?<>,]+/).filter(Boolean);
    return tokens.at(-1)!;
  });
}

function interfaceProperties(name: string): string[] {
  const match = TS_SOURCE.match(new RegExp(`export interface ${name}\\s*\\{([\\s\\S]*?)\\n\\}`));
  if (!match) throw new Error(`TS DTO ${name} was not found.`);
  return [...match[1].matchAll(/^\s*(\w+)\??:/gm)].map((property) => property[1]);
}

function extractEnumMembers(source: string, name: string): string[] {
  const match = source.match(new RegExp(`public enum ${name}\\s*\\{([\\s\\S]*?)\\n\\}`));
  if (!match) throw new Error(`C# enum ${name} was not found.`);
  return [...match[1].matchAll(/^\s*(\w+)\s*=/gm)].map((member) => member[1]);
}

function extractTsUnionMembers(name: string): string[] {
  const match = TS_SOURCE.match(new RegExp(`export type ${name} =([\\s\\S]*?);`));
  if (!match) throw new Error(`TS union ${name} was not found.`);
  return [...match[1].matchAll(/'([A-Za-z]+)'/g)].map((member) => member[1]);
}

function extractCsharpStringConstant(source: string, name: string): string {
  const match = source.match(new RegExp(`public const string ${name} = "([^"]+)";`));
  if (!match) throw new Error(`C# string constant ${name} was not found.`);
  return match[1];
}

function extractLimit(source: string, name: string): number {
  const match = source.match(new RegExp(`public const (?:long|int) ${name} = ([^;]+);`));
  if (!match) throw new Error(`C# limit ${name} was not found.`);
  const expression = match[1].replace(/L/g, '').replace(/_/g, '');
  const evaluate = Function(`"use strict"; return (${expression});`) as () => number;
  return evaluate();
}

describe('migration HTTP contract parity with the backend source', () => {
  it('mirrors every stable server error code', () => {
    const body = HTTP_CONTRACTS.match(
      /public static class MigrationHttpErrors([\s\S]*?)\n\}/,
    )?.[1];
    expect(body, 'MigrationHttpErrors class not found').toBeTruthy();
    const serverCodes = [
      ...body!.matchAll(/public const string \w+ = "([a-z_]+)";/g),
    ].map((match) => match[1]);

    // The maintenance middleware answers migration routes with the migration
    // error shape, so its code belongs to the same closed set.
    expect(MIDDLEWARE).toContain('"migration_activation_busy"');

    expect([...SERVER_MIGRATION_ERROR_CODES].sort()).toEqual(
      [...serverCodes, 'migration_activation_busy'].sort(),
    );
    expect(new Set(SERVER_MIGRATION_ERROR_CODES).size).toBe(SERVER_MIGRATION_ERROR_CODES.length);
  });

  it('mirrors every string enum member exactly', () => {
    const pairs: Array<[string, string]> = [
      ['MigrationDirection', 'MigrationDirection'],
      ['MigrationJobState', 'MigrationJobState'],
      ['MigrationPreflightDecision', 'MigrationPreflightDecision'],
      ['MigrationDestinationStatus', 'MigrationDestinationStatus'],
      ['MigrationSessionPurpose', 'MigrationSessionPurpose'],
      ['MigrationSessionState', 'MigrationSessionState'],
      ['MigrationProgressPhase', 'MigrationProgressPhase'],
      ['MigrationRecoveryStatus', 'MigrationRecoveryStatus'],
    ];

    for (const [csharp, ts] of pairs) {
      expect(extractTsUnionMembers(ts).sort(), ts).toEqual(
        extractEnumMembers(CONTRACT_MODELS, csharp).sort(),
      );
    }
  });

  it('mirrors every transport DTO field (and the computed serialized fields)', () => {
    const pairs: Array<{
      source: string;
      record: string;
      ts: string;
      computed?: string[];
    }> = [
      { source: HTTP_CONTRACTS, record: 'MigrationPreflightResponse', ts: 'MigrationPreflightResponseDto' },
      {
        source: CONTRACT_MODELS,
        record: 'MigrationPreflightResult',
        ts: 'MigrationPreflightResultDto',
        computed: ['IsAllowed'],
      },
      {
        source: CONTRACT_MODELS,
        record: 'MigrationArchiveCounts',
        ts: 'MigrationArchiveCountsDto',
        computed: ['TotalRows'],
      },
      {
        source: CONTRACT_MODELS,
        record: 'MigrationExistingCounts',
        ts: 'MigrationExistingCountsDto',
        computed: ['TotalRows'],
      },
      { source: HTTP_CONTRACTS, record: 'MigrationJobStatusResponse', ts: 'MigrationJobStatusResponseDto' },
      { source: CONTRACT_MODELS, record: 'MigrationJob', ts: 'MigrationJobDto' },
      { source: CONTRACT_MODELS, record: 'MigrationProgress', ts: 'MigrationProgressDto' },
      { source: CONTRACT_MODELS, record: 'MigrationSessionStatus', ts: 'MigrationSessionStatusDto' },
      { source: CONTRACT_MODELS, record: 'MigrationFileIdentity', ts: 'MigrationFileIdentityDto' },
      { source: HTTP_CONTRACTS, record: 'MigrationUploadSessionResponse', ts: 'MigrationUploadSessionResponseDto' },
      { source: HTTP_CONTRACTS, record: 'MigrationChunkRange', ts: 'MigrationChunkRangeDto' },
      { source: CONTRACT_MODELS, record: 'MigrationChunkUploadResult', ts: 'MigrationChunkUploadResultDto' },
      { source: HTTP_CONTRACTS, record: 'MigrationErrorResponse', ts: 'MigrationErrorResponseDto' },
      { source: HTTP_CONTRACTS, record: 'MigrationPreflightBody', ts: 'MigrationPreflightRequestDto' },
      { source: HTTP_CONTRACTS, record: 'MigrationCreateJobBody', ts: 'MigrationCreateJobRequestDto' },
      { source: HTTP_CONTRACTS, record: 'MigrationFileIdentityBody', ts: 'MigrationFileIdentityDto' },
      { source: HTTP_CONTRACTS, record: 'MigrationSessionBody', ts: 'MigrationSessionRequestDto' },
    ];

    for (const { source, record, ts, computed = [] } of pairs) {
      const contractFields = recordParameters(source, record);
      const clientFields = interfaceProperties(ts);

      for (const field of contractFields) {
        expect(clientFields, `${record}.${field} -> ${ts}`).toContain(camelCase(field));
      }
      const expectedClientFields = [...contractFields.map(camelCase), ...computed.map(camelCase)];
      for (const field of clientFields) {
        expect(expectedClientFields, `${ts}.${field} -> ${record}`).toContain(field);
      }
    }
  });

  it('mirrors the contract limits', () => {
    const pairs: Array<[keyof typeof MIGRATION_LIMITS, string]> = [
      ['maxArchiveBytes', 'MaxArchiveBytes'],
      ['maxMediaBytes', 'MaxMediaBytes'],
      ['maxSingleEntryBytes', 'MaxSingleEntryBytes'],
      ['maxDataBytes', 'MaxDataBytes'],
      ['maxManifestBytes', 'MaxManifestBytes'],
      ['maxArchiveEntries', 'MaxArchiveEntries'],
      ['minChunkBytes', 'MinChunkBytes'],
      ['defaultChunkBytes', 'DefaultChunkBytes'],
      ['maxChunkBytes', 'MaxChunkBytes'],
    ];

    for (const [ts, csharp] of pairs) {
      expect(MIGRATION_LIMITS[ts], csharp).toBe(extractLimit(CONTRACT_MODELS, csharp));
    }
  });

  it('mirrors the base path, chunk route and mandatory chunk headers', () => {
    expect(MIGRATION_BASE_PATH).toBe(extractCsharpStringConstant(ENDPOINTS, 'BasePath'));
    expect(CHUNK_HASH_HEADER_NAME).toBe(
      extractCsharpStringConstant(HTTP_CONTRACTS, 'ChunkHashHeaderName'),
    );
    expect(ENDPOINTS).toContain('"/jobs/{id}/upload-session/chunks/{index}"');
    expect(ENDPOINTS).toContain('/jobs/{id}/upload-session/complete');
  });

  it('keeps the maintenance 503 migration-shaped with Retry-After', () => {
    expect(MIDDLEWARE).toContain('new MigrationErrorResponse');
    expect(MIDDLEWARE).toContain('migration_activation_busy');
    expect(MIDDLEWARE).toMatch(/RetryAfter\s*=\s*"5"/);
  });
});
