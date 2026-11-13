/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

/** "1 note", "3 notes"; pass `many` for an irregular plural ("1 library", "2 libraries"). */
export function plural(count: number, one: string, many = `${one}s`): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** Seconds to `m:ss`, or `h:mm:ss` from one hour on. Never NaN. */
export function formatAudioTime(seconds: number): string {
  const whole = Number.isFinite(seconds) ? Math.max(0, Math.floor(seconds)) : 0;
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = (whole % 60).toString().padStart(2, '0');
  return h > 0 ? `${h}:${m.toString().padStart(2, '0')}:${s}` : `${m}:${s}`;
}

const SIZE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const;
const NUMBER = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
const DECIMAL = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });

/**
 * The one file-size format: 1024-based steps, named B, KB, MB, GB, TB, with one
 * decimal from KB up. Empty for a missing or invalid size.
 */
export function formatFileSize(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return '';

  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < SIZE_UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${unit === 0 ? NUMBER.format(value) : DECIMAL.format(value)} ${SIZE_UNITS[unit]}`;
}

/**
 * "1.5 MB of 4 MB" for an upload progress line. Empty when the total is
 * unknown or zero, so a progress line never claims a size the server did not
 * give it.
 */
export function formatByteProgress(movedBytes: number, totalBytes: number): string {
  if (!Number.isFinite(totalBytes) || totalBytes <= 0) return '';
  return `${formatFileSize(movedBytes)} of ${formatFileSize(totalBytes)}`;
}
