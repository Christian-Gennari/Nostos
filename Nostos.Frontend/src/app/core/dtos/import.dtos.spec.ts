import { describe, expect, it } from 'vitest';

import type { AcquisitionState } from './provider.dtos';
import {
  ImportActivity,
  importStageLabel,
  isImportInFlight,
  isImportTerminal,
} from './import.dtos';

function activity(state: AcquisitionState, stage: string): ImportActivity {
  return {
    id: 'job-1',
    source: 'job',
    state,
    stage,
    percent: 0,
    detail: null,
    providerId: 'p1',
    externalId: 'x1',
    assetId: null,
    bookId: 'b1',
    title: 'A Book',
    author: null,
    coverUrl: null,
    errorCode: null,
    message: null,
    createdAt: '2026-08-01T08:00:00Z',
    updatedAt: '2026-08-01T08:00:00Z',
    canRetry: true,
  };
}

describe('import activity predicates', () => {
  it('treats succeeded, failed and cancelled as terminal', () => {
    expect(isImportTerminal(activity('succeeded', 'done'))).toBe(true);
    expect(isImportTerminal(activity('failed', 'failed'))).toBe(true);
    expect(isImportTerminal(activity('cancelled', 'done'))).toBe(true);
  });

  it('treats queued and running as still in flight', () => {
    expect(isImportInFlight(activity('queued', 'queued'))).toBe(true);
    expect(isImportInFlight(activity('running', 'downloading'))).toBe(true);
  });

  it('does not call a finished entry in flight, or a working one terminal', () => {
    expect(isImportInFlight(activity('succeeded', 'done'))).toBe(false);
    expect(isImportTerminal(activity('running', 'downloading'))).toBe(false);
  });
});

describe('importStageLabel', () => {
  it.each([
    ['succeeded', 'done', 'Done'],
    ['failed', 'downloading', 'Failed'],
    ['cancelled', 'downloading', 'Cancelled'],
  ] as Array<[AcquisitionState, string, string]>)(
    'names the end of a %s import',
    (state, stage, expected) => {
      expect(importStageLabel(activity(state, stage))).toBe(expected);
    },
  );

  it.each([
    ['transcoding', 'Saving'],
    ['assembling', 'Saving'],
    ['importing', 'Saving'],
    ['queued', 'Queued'],
    ['starting', 'Queued'],
    ['resolving', 'Downloading'],
    ['checking', 'Downloading'],
    ['downloading', 'Downloading'],
    ['unknown-stage', 'Downloading'],
  ])('names the %s stage "%s"', (stage, expected) => {
    expect(importStageLabel(activity('running', stage))).toBe(expected);
  });
});
