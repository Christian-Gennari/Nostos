/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { describe, expect, it } from 'vitest';
import { backupWarning } from './backup.dtos';

const NOW = Date.parse('2026-11-04T12:00:00Z');
const daysAgo = (d: number) => new Date(NOW - d * 86_400_000).toISOString();

describe('backupWarning', () => {
  it('is silent before the first backup, whether automatic backup is on or off', () => {
    expect(backupWarning(false, 168, null, NOW)).toBeNull();
    expect(backupWarning(true, 168, null, NOW)).toBeNull();
  });

  it('is silent when off but the last backup is recent', () => {
    expect(backupWarning(false, 168, daysAgo(10), NOW)).toBeNull();
  });

  it('offers Turn on when off and the last backup is over 14 days old', () => {
    const w = backupWarning(false, 168, daysAgo(70), NOW);
    expect(w?.action).toBe('enable');
    expect(w?.message).toBe('Automatic backup is off. The last backup was 70 days ago.');
  });

  it('is silent when on and within twice the interval', () => {
    expect(backupWarning(true, 168, daysAgo(13), NOW)).toBeNull();
  });

  it('offers Back up now when on and older than twice the interval', () => {
    const w = backupWarning(true, 168, daysAgo(15), NOW);
    expect(w?.action).toBe('backup');
    expect(w?.message).toContain('15 days ago');
  });

  it('ignores an unreadable date', () => {
    expect(backupWarning(true, 168, 'nonsense', NOW)).toBeNull();
  });
});
