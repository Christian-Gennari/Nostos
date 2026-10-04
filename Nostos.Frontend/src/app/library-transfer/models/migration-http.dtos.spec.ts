import {
  MigrationChunkRangeDto,
  chunkCount,
  chunkLength,
  fromChunkRanges,
  isRetryableJobState,
  isTerminalJobState,
  isValidChunkBytes,
  missingChunkIndexes,
  toChunkRanges,
} from './migration-http.dtos';
import { TransferFlowState, canTransitionFlow } from './library-transfer.models';

describe('migration DTO helpers', () => {
  it('computes chunk counts including a short final chunk', () => {
    expect(chunkCount(0, 1000)).toBe(0);
    expect(chunkCount(1, 1000)).toBe(1);
    expect(chunkCount(1000, 1000)).toBe(1);
    expect(chunkCount(1001, 1000)).toBe(2);
  });

  it('computes chunk lengths with only the final chunk short', () => {
    expect(chunkLength(0, 2500, 1000)).toBe(1000);
    expect(chunkLength(2, 2500, 1000)).toBe(500);
    expect(chunkLength(3, 2500, 1000)).toBe(0);
  });

  it('mirrors the contract non-final/final chunk byte rules', () => {
    const chunkSize = 4 * 1024 * 1024;
    expect(isValidChunkBytes(chunkSize, chunkSize, false)).toBe(true);
    expect(isValidChunkBytes(chunkSize - 1, chunkSize, false)).toBe(false);
    expect(isValidChunkBytes(1, chunkSize, true)).toBe(true);
    expect(isValidChunkBytes(chunkSize, chunkSize, true)).toBe(true);
    expect(isValidChunkBytes(chunkSize + 1, chunkSize, true)).toBe(false);
    expect(isValidChunkBytes(0, chunkSize, true)).toBe(false);
    expect(isValidChunkBytes(100, 1024, true)).toBe(false);
  });

  it('compresses and expands sparse received ranges', () => {
    const chunks = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 18, 19, 20, 80, 99];
    const ranges = toChunkRanges(chunks);
    expect(ranges).toEqual([
      { startIndex: 0, endIndex: 14 },
      { startIndex: 16, endIndex: 20 },
      { startIndex: 80, endIndex: 80 },
      { startIndex: 99, endIndex: 99 },
    ]);
    expect(fromChunkRanges(ranges)).toEqual(chunks);
  });

  it('derives missing chunks from the server ranges (plan §18 example)', () => {
    const received: MigrationChunkRangeDto[] = [
      { startIndex: 0, endIndex: 14 },
      { startIndex: 16, endIndex: 40 },
      { startIndex: 80, endIndex: 99 },
    ];

    const missing = missingChunkIndexes(received, 100);
    expect(missing[0]).toBe(15);
    expect(missing.slice(1, 5)).toEqual([41, 42, 43, 44]);
    expect(missing.at(-1)).toBe(79);
    expect(missing).toHaveLength(1 + 39 + 0);
    expect(missing).not.toContain(0);
    expect(missing).not.toContain(99);
  });

  it('classifies terminal and retryable job states', () => {
    expect(isTerminalJobState('Completed')).toBe(true);
    expect(isTerminalJobState('Failed')).toBe(true);
    expect(isTerminalJobState('Expired')).toBe(true);
    expect(isTerminalJobState('Cancelled')).toBe(true);
    expect(isTerminalJobState('Validating')).toBe(false);
    expect(isRetryableJobState('Failed')).toBe(true);
    expect(isRetryableJobState('Cancelled')).toBe(true);
    expect(isRetryableJobState('Expired')).toBe(true);
    expect(isRetryableJobState('Completed')).toBe(false);
  });
});

describe('import flow transitions', () => {
  const states: TransferFlowState['kind'][] = [
    'idle',
    'inspecting',
    'preflighting',
    'ready-to-upload',
    'uploading',
    'checking',
    'ready-empty',
    'replacement-confirmation',
    'completed',
    'failed',
    'cancelled',
  ];

  it('allows exactly the documented outgoing transitions for every state', () => {
    const allowed: Record<TransferFlowState['kind'], TransferFlowState['kind'][]> = {
      idle: [
        'inspecting',
        'ready-to-upload',
        'checking',
        'ready-empty',
        'replacement-confirmation',
        'completed',
        'failed',
        'cancelled',
      ],
      inspecting: ['preflighting', 'failed', 'cancelled'],
      preflighting: ['ready-to-upload', 'failed', 'cancelled'],
      'ready-to-upload': ['uploading', 'failed', 'cancelled', 'ready-to-upload'],
      uploading: ['checking', 'failed', 'cancelled', 'ready-to-upload', 'uploading'],
      checking: [
        'checking',
        'ready-empty',
        'replacement-confirmation',
        'completed',
        'failed',
        'cancelled',
      ],
      'ready-empty': ['idle', 'checking', 'completed', 'cancelled', 'failed'],
      'replacement-confirmation': ['idle', 'checking', 'completed', 'cancelled', 'failed'],
      completed: ['idle'],
      failed: ['inspecting', 'uploading', 'checking', 'ready-to-upload'],
      cancelled: ['idle', 'inspecting'],
    };

    for (const from of states) {
      for (const to of states) {
        const expected = allowed[from].includes(to);
        expect(canTransitionFlow(from, to), `${from} -> ${to}`).toBe(expected);
      }
    }
  });

  it('allows failure from every active state', () => {
    for (const kind of ['inspecting', 'preflighting', 'ready-to-upload', 'uploading', 'checking'] as const) {
      expect(canTransitionFlow(kind, 'failed')).toBe(true);
    }
  });
});
