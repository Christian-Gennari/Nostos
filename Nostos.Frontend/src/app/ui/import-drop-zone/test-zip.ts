/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *
 * Test helper: a zip built in memory, stored (uncompressed) so the bytes are
 * predictable. The routing only reads the central directory, so the contents
 * never need to be valid archives — but keeping the structure real means the
 * test exercises the same parser a dropped Calibre library goes through.
 */

export interface TestZipEntry {
  readonly name: string;
  readonly content?: string;
}

export function storedZip(entries: readonly TestZipEntry[], fileName: string): File {
  const encoder = new TextEncoder();
  const parts: Uint8Array[] = [];
  const centrals: Uint8Array[] = [];
  let directoryOffset = 0;

  for (const entry of entries) {
    const name = encoder.encode(entry.name);
    const data = encoder.encode(entry.content ?? '');
    const local = new Uint8Array(30 + name.length + data.length);
    const localView = new DataView(local.buffer);
    localView.setUint32(0, 0x04034b50, true);
    localView.setUint16(4, 20, true);
    localView.setUint32(18, data.length, true);
    localView.setUint32(22, data.length, true);
    localView.setUint16(26, name.length, true);
    local.set(name, 30);
    local.set(data, 30 + name.length);
    parts.push(local);

    const central = new Uint8Array(46 + name.length);
    const centralView = new DataView(central.buffer);
    centralView.setUint32(0, 0x02014b50, true);
    centralView.setUint16(4, 20, true);
    centralView.setUint16(6, 20, true);
    centralView.setUint32(20, data.length, true);
    centralView.setUint32(24, data.length, true);
    centralView.setUint16(28, name.length, true);
    centralView.setUint32(42, directoryOffset, true);
    central.set(name, 46);
    centrals.push(central);

    directoryOffset += local.length;
  }

  const end = new Uint8Array(22);
  const endView = new DataView(end.buffer);
  const directorySize = centrals.reduce((total, entry) => total + entry.length, 0);
  endView.setUint32(0, 0x06054b50, true);
  endView.setUint16(8, entries.length, true);
  endView.setUint16(10, entries.length, true);
  endView.setUint32(12, directorySize, true);
  endView.setUint32(16, directoryOffset, true);

  const all = [...parts, ...centrals, end];
  const bytes = new Uint8Array(all.reduce((total, part) => total + part.length, 0));
  let offset = 0;
  for (const part of all) {
    bytes.set(part, offset);
    offset += part.length;
  }
  return new File([bytes], fileName, { type: 'application/zip' });
}
