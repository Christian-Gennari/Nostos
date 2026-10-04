import { TestBed } from '@angular/core/testing';

import { PortableArchiveInspector } from './portable-archive-inspector.service';
import {
  SparseBlobDouble,
  backupManifest,
  buildZipArchive,
  concat,
  createFile,
  portableArchiveFixture,
  portableManifest,
} from '../testing/zip-archive.fixture';

const encoder = new TextEncoder();

function inspector(): PortableArchiveInspector {
  TestBed.configureTestingModule({});
  return TestBed.inject(PortableArchiveInspector);
}

describe('PortableArchiveInspector', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('identifies a valid stored portable archive and derives the summary', async () => {
    const archive = await portableArchiveFixture(
      portableManifest({
        data: { path: 'data/library.json', length: 1200, sha256: 'a'.repeat(64) },
        media: [
          {
            bookId: '11111111-1111-4111-8111-111111111111',
            kind: 'book',
            path: 'media/one',
            fileName: 'one.epub',
            contentType: 'application/epub+zip',
            length: 900_000,
            sha256: 'b'.repeat(64),
          },
        ],
      }),
    );
    const file = createFile(archive);

    const inspection = await inspector().inspect(file);

    expect(inspection.kind).toBe('portable');
    if (inspection.kind !== 'portable') return;
    expect(inspection.summary.archiveBytes).toBe(file.size);
    expect(inspection.summary.formatVersion).toBe(1);
    expect(inspection.summary.dataVersion).toBe(3);
    expect(inspection.summary.mediaBytes).toBe(900_000);
    expect(inspection.summary.maxEntryBytes).toBe(900_000);
    expect(inspection.summary.mediaEntries).toBe(1);
    expect(inspection.summary.counts).toMatchObject({
      works: 2,
      books: 3,
      collections: 1,
      collectionMemberships: 4,
      notes: 5,
      mediaEntries: 1,
    });
  });

  it.each([1, 2, 3])('accepts supported data version %i', async (dataVersion) => {
    const archive = await portableArchiveFixture(portableManifest({ dataVersion }));
    const inspection = await inspector().inspect(createFile(archive));
    expect(inspection.kind).toBe('portable');
  });

  it('reads a compressed manifest', async () => {
    const archive = await portableArchiveFixture(portableManifest({ dataVersion: 2 }), {
      compressManifest: true,
      extraEntries: [
        {
          name: 'data/library.json',
          data: encoder.encode(JSON.stringify({ version: 2 })),
          method: 8,
        },
      ],
    });
    const inspection = await inspector().inspect(createFile(archive));
    expect(inspection.kind).toBe('portable');
  });

  it('rejects an unsupported format version', async () => {
    const archive = await portableArchiveFixture(portableManifest({ formatVersion: 2 }));
    await expect(inspector().inspect(createFile(archive))).resolves.toEqual({
      kind: 'unsupported',
      reason: 'unsupported-format-version',
    });
  });

  it('rejects an unsupported data version', async () => {
    const archive = await portableArchiveFixture(portableManifest({ dataVersion: 99 }));
    await expect(inspector().inspect(createFile(archive))).resolves.toEqual({
      kind: 'unsupported',
      reason: 'unsupported-data-version',
    });
  });

  it('identifies a local operational backup archive', async () => {
    const archive = await buildZipArchive([
      { name: 'manifest.json', data: encoder.encode(JSON.stringify(backupManifest())) },
      { name: 'metadata/state.json', data: encoder.encode('{}') },
    ]);
    await expect(inspector().inspect(createFile(archive))).resolves.toEqual({
      kind: 'operational-backup',
    });
  });

  it('reports a missing manifest', async () => {
    const archive = await buildZipArchive([
      { name: 'data/library.json', data: encoder.encode('{}') },
    ]);
    await expect(inspector().inspect(createFile(archive))).resolves.toEqual({
      kind: 'unsupported',
      reason: 'missing-manifest',
    });
  });

  it('reports a duplicate manifest', async () => {
    const manifest = encoder.encode(JSON.stringify(portableManifest()));
    const archive = await buildZipArchive([
      { name: 'manifest.json', data: manifest },
      { name: 'manifest.json', data: manifest },
    ]);
    await expect(inspector().inspect(createFile(archive))).resolves.toEqual({
      kind: 'unsupported',
      reason: 'duplicate-manifest',
    });
  });

  it('rejects an oversized manifest before reading its bytes', async () => {
    const archive = await buildZipArchive([
      {
        name: 'manifest.json',
        data: encoder.encode('{}'),
        declaredUncompressedSize: 5 * 1024 * 1024,
      },
    ]);
    await expect(inspector().inspect(createFile(archive))).resolves.toEqual({
      kind: 'unsupported',
      reason: 'manifest-too-large',
    });
  });

  it('reports random bytes as not portable', async () => {
    const random = new Uint8Array(4096);
    for (let index = 0; index < random.length; index += 1) random[index] = (index * 7) % 256;
    await expect(inspector().inspect(createFile(random))).resolves.toEqual({
      kind: 'unsupported',
      reason: 'not-a-zip',
    });
  });

  it('reports a truncated archive', async () => {
    const valid = await portableArchiveFixture();
    const truncated = valid.slice(0, valid.length - 40);
    await expect(inspector().inspect(createFile(truncated))).resolves.toMatchObject({
      kind: 'unsupported',
    });
  });

  it('reports a truncated central directory with the EOCD intact', async () => {
    const valid = await portableArchiveFixture();
    // Keep the EOCD but cut most of the central directory: the declared
    // directory now runs past the retained bytes.
    const eocd = valid.slice(valid.length - 22);
    const broken = concat([valid.slice(0, 5), eocd]);
    await expect(inspector().inspect(createFile(broken))).resolves.toEqual({
      kind: 'unsupported',
      reason: 'truncated',
    });
  });

  it('reads a ZIP64 central directory', async () => {
    const zip64 = await buildZipArchive(
      [
        {
          name: 'manifest.json',
          data: encoder.encode(JSON.stringify(portableManifest({ dataVersion: 1 }))),
        },
      ],
      { forceZip64: true },
    );
    await expect(inspector().inspect(createFile(zip64))).resolves.toMatchObject({
      kind: 'portable',
    });
  });

  it('reads only bounded windows of a very large sparse-like archive', async () => {
    const manifestBytes = encoder.encode(JSON.stringify(portableManifest()));
    const eocd = await trailingRegion(manifestBytes);
    const virtualArchive = new SparseBlobDouble(2 * 1024 * 1024 * 1024, [eocd.window]);

    const inspection = await inspector().inspect(virtualArchive as unknown as Blob);

    expect(inspection.kind).toBe('portable');
    const totalRead = virtualArchive.readBytes.reduce((sum, bytes) => sum + bytes, 0);
    expect(totalRead).toBeLessThan(1024 * 1024);
  });
});

/** Rebuilds an archive so every offset sits near the end of a big sparse blob. */
async function trailingRegion(manifestBytes: Uint8Array): Promise<{
  window: { start: number; end: number; bytes: Uint8Array };
}> {
  const archive = await buildZipArchive([
    { name: 'manifest.json', data: manifestBytes },
  ]);
  const virtualSize = 2 * 1024 * 1024 * 1024;
  const prefix = virtualSize - archive.length;

  // The ZIP was built at offset 0; shift its stored absolute offsets by the
  // prefix so reads land inside the sparse blob's real window.
  const patched = archive.slice();
  const view = new DataView(patched.buffer);
  const eocd = patched.length - 22;
  const centralDirectoryOffset = view.getUint32(eocd + 16, true);
  view.setUint32(eocd + 16, centralDirectoryOffset + prefix, true);
  view.setUint32(
    centralDirectoryOffset + 42,
    view.getUint32(centralDirectoryOffset + 42, true) + prefix,
    true,
  );

  return { window: { start: prefix, end: virtualSize, bytes: patched } };
}
