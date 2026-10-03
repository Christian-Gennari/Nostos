import { expect, test } from '@playwright/test';

import { apiGet, apiPost, loadFixture } from './support/fixture';
import { apiDelete, cleanupBrain, snapshotTopicIds } from './support/brain-fixture';
import { apiPut } from './support/visual-capture';
import type { Note, NoteSearchHit } from '../src/app/core/dtos/note.dtos';
import type {
  WritingContentDto,
  WritingSourceDto,
} from '../src/app/core/dtos/writing.dtos';

test('captured source survives Brain → Keep with writing → Studio on the real backend', async ({
  page,
}) => {
  const { baseUrl } = loadFixture();
  const beforeTopicIds = await snapshotTopicIds(baseUrl);

  const stamp = Date.now().toString(36);
  const bookTitle = `Capture Source Smoke ${stamp}`;
  const writingTitle = `Source Studio Smoke ${stamp}`;

  const rawCapture =
    'I keep returning to this sentence because attention feels like a practice.';
  const selectedText = 'Attention grows where it is deliberately returned.';
  const writingContent =
    '# Existing customer writing\n\n' +
    'Customer prose must survive the source handoff and a full Studio reload.';

  let bookId: string | null = null;
  let writingId: string | null = null;

  try {
    // Seed through the supported local REST surface only. Verbatim processing
    // intentionally performs no rewrite, so a capture client that wants the
    // original wording retained supplies RawContent explicitly. The manually
    // reported physical page remains honest, unverified provenance.
    const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
      type: 'physical',
      title: bookTitle,
      author: 'Nostos QA',
    });
    bookId = book.id;

    const created = await apiPost<Note>(baseUrl, `/api/books/${book.id}/notes`, {
      content: rawCapture,
      selectedText,
      rawContent: rawCapture,
      captureSource: 'voice',
      processingMode: 'verbatim',
      sourceAnchorKind: 'physical_page',
      sourceAnchorValue: '42',
    });

    expect(created.id).toBeTruthy();
    expect(created.bookId).toBe(book.id);
    expect(created.content).toBe(rawCapture);
    expect(created.selectedText).toBe(selectedText);
    expect(created.captureSource).toBe('voice');
    expect(created.processingMode).toBe('verbatim');
    expect(created.sourceAnchorKind).toBe('physical_page');
    expect(created.sourceAnchorValue).toBe('42');
    expect(created.anchorVerified).toBe(false);

    const writing = await apiPost<{ id: string }>(baseUrl, '/api/writings', {
      name: writingTitle,
      type: 'Document',
      parentId: null,
    });
    writingId = writing.id;

    await apiPut(baseUrl, `/api/writings/${writing.id}`, {
      name: writingTitle,
      content: writingContent,
    });

    // Open the actual Brain inspector for the canonical saved note.
    await page.goto(`${baseUrl}/second-brain?noteId=${created.id}`, {
      waitUntil: 'domcontentloaded',
    });

    const inspector = page.getByTestId('brain-note-inspector');
    await expect(inspector).toBeVisible();
    await expect(inspector.getByRole('heading', { name: bookTitle })).toBeVisible();

    await expect(inspector.locator('.note-text')).toContainText(rawCapture);
    await expect(inspector.locator('.quote-text')).toContainText(selectedText);
    await expect(inspector.locator('.source-badge')).toContainText(bookTitle);

    const canonical = await apiGet<NoteSearchHit>(
      baseUrl,
      `/api/notes/${created.id}`,
    );
    expect(canonical.id).toBe(created.id);
    expect(canonical.bookId).toBe(book.id);
    expect(canonical.bookTitle).toBe(bookTitle);
    expect(canonical.content).toBe(rawCapture);
    expect(canonical.selectedText).toBe(selectedText);
    expect(canonical.captureSource).toBe('voice');
    expect(canonical.processingMode).toBe('verbatim');
    expect(canonical.sourceAnchorKind).toBe('physical_page');
    expect(canonical.sourceAnchorValue).toBe('42');
    expect(canonical.anchorVerified).toBe(false);
    expect(canonical.hasRawContent).toBe(true);

    // Raw transcript coverage stays explicit: the canonical/search DTO only
    // advertises availability. The original words themselves come from /raw.
    const captureDetails = page.getByTestId('note-capture-details');
    await expect(captureDetails).toContainText('Verbatim · Spoken thought');

    await captureDetails
      .getByRole('button', { name: 'View original', exact: true })
      .click();

    const originalDialog = page.getByRole('dialog', {
      name: 'Original wording',
      exact: true,
    });
    await expect(originalDialog).toBeVisible();
    await expect(page.getByTestId('note-original-text')).toHaveText(rawCapture);

    const original = await apiGet<{
      id: string;
      rawContent: string | null;
      content: string;
      processingMode: string;
    }>(baseUrl, `/api/notes/${created.id}/raw`);

    expect(original).toEqual({
      id: created.id,
      rawContent: rawCapture,
      content: rawCapture,
      processingMode: 'verbatim',
    });

    await originalDialog
      .getByRole('button', { name: 'Close original wording', exact: true })
      .click();
    await expect(originalDialog).toHaveCount(0);

    // Exercise the actual Brain handoff UI rather than creating the writing-note
    // relation directly through the API.
    await inspector
      .getByRole('button', { name: 'Keep with writing…', exact: true })
      .click();

    const picker = page.getByRole('dialog', {
      name: 'Keep with writing…',
      exact: true,
    });
    await expect(picker).toBeVisible();
    await expect(picker).toContainText(
      'The writing keeps links to the original notes.',
    );

    const existingWriting = picker.getByRole('button', {
      name: writingTitle,
      exact: true,
    });
    await expect(existingWriting).toBeVisible();
    await existingWriting.click();

    await picker.getByLabel('Open writing after adding').check();
    await picker
      .getByRole('button', { name: 'Keep sources', exact: true })
      .click();

    await expect(page).toHaveURL(
      new RegExp(`/studio\\?writingId=${writing.id}$`),
    );

    // Persisted kept-source identity and provenance must remain tied to the
    // original note, including the honest unverified physical-page anchor.
    const kept = await apiGet<WritingSourceDto[]>(
      baseUrl,
      `/api/writings/${writing.id}/notes`,
    );
    expect(kept).toHaveLength(1);
    expect(kept[0]).toMatchObject({
      id: created.id,
      bookId: book.id,
      bookTitle,
      content: rawCapture,
      selectedText,
      sourceAnchorKind: 'physical_page',
      sourceAnchorValue: '42',
      anchorVerified: false,
    });

    // Keeping a source must not rewrite the customer's existing manuscript.
    const persistedWriting = await apiGet<WritingContentDto>(
      baseUrl,
      `/api/writings/${writing.id}`,
    );
    expect(persistedWriting.name).toBe(writingTitle);
    expect(persistedWriting.content).toBe(writingContent);

    await expect(page.locator('.header-doc-title')).toHaveText(writingTitle);
    await expect(page.locator('.tox-tinymce')).toBeVisible();

    const editorBody = page
      .frameLocator('.tox-edit-area__iframe')
      .locator('body');
    await expect(editorBody).toContainText('Existing customer writing');
    await expect(editorBody).toContainText(
      'Customer prose must survive the source handoff and a full Studio reload.',
    );

    // "For this writing" is the default reference surface, so this row must be
    // backed by the persisted kept-source relation rather than transient Brain
    // component state.
    const keptRow = page.locator('.kept-note-row', {
      hasText: rawCapture,
    });
    await expect(keptRow).toBeVisible();
    await expect(keptRow).toContainText(bookTitle);
    await expect(keptRow).toContainText(selectedText);

    await keptRow.locator('app-note-card.inspectable-note').click();

    const inspectedSource = page.getByRole('region', {
      name: 'Inspected source',
    });
    await expect(inspectedSource).toBeVisible();
    await expect(inspectedSource).toContainText(rawCapture);
    await expect(inspectedSource).toContainText(selectedText);
    await expect(inspectedSource).toContainText(bookTitle);

    // The physical-page identity survives, but because it is explicitly
    // unverified Studio must not upgrade it into a trusted human locator.
    await expect(
      inspectedSource.locator('.inspected-source-locator'),
    ).toHaveCount(0);

    // Reload is the durability boundary for both halves of the smoke:
    // manuscript content comes back from the Writing, and source linkage comes
    // back independently from /api/writings/{id}/notes.
    await page.reload({ waitUntil: 'domcontentloaded' });

    await expect(page).toHaveURL(
      new RegExp(`/studio\\?writingId=${writing.id}$`),
    );
    await expect(page.locator('.header-doc-title')).toHaveText(writingTitle);
    await expect(page.locator('.tox-tinymce')).toBeVisible();

    const reloadedEditorBody = page
      .frameLocator('.tox-edit-area__iframe')
      .locator('body');
    await expect(reloadedEditorBody).toContainText('Existing customer writing');
    await expect(reloadedEditorBody).toContainText(
      'Customer prose must survive the source handoff and a full Studio reload.',
    );

    const reloadedKeptRow = page.locator('.kept-note-row', {
      hasText: rawCapture,
    });
    await expect(reloadedKeptRow).toBeVisible();
    await expect(reloadedKeptRow).toContainText(bookTitle);
    await expect(reloadedKeptRow).toContainText(selectedText);

    const keptAfterReload = await apiGet<WritingSourceDto[]>(
      baseUrl,
      `/api/writings/${writing.id}/notes`,
    );
    expect(keptAfterReload).toHaveLength(1);
    expect(keptAfterReload[0]).toMatchObject({
      id: created.id,
      bookId: book.id,
      bookTitle,
      content: rawCapture,
      selectedText,
      sourceAnchorKind: 'physical_page',
      sourceAnchorValue: '42',
      anchorVerified: false,
    });

    const canonicalAfterReload = await apiGet<NoteSearchHit>(
      baseUrl,
      `/api/notes/${created.id}`,
    );
    expect(canonicalAfterReload.id).toBe(created.id);
    expect(canonicalAfterReload.content).toBe(rawCapture);
    expect(canonicalAfterReload.selectedText).toBe(selectedText);
    expect(canonicalAfterReload.captureSource).toBe('voice');
    expect(canonicalAfterReload.processingMode).toBe('verbatim');
    expect(canonicalAfterReload.sourceAnchorKind).toBe('physical_page');
    expect(canonicalAfterReload.sourceAnchorValue).toBe('42');
    expect(canonicalAfterReload.anchorVerified).toBe(false);
    expect(canonicalAfterReload.hasRawContent).toBe(true);

    const originalAfterReload = await apiGet<{
      id: string;
      rawContent: string | null;
      content: string;
      processingMode: string;
    }>(baseUrl, `/api/notes/${created.id}/raw`);

    expect(originalAfterReload).toEqual({
      id: created.id,
      rawContent: rawCapture,
      content: rawCapture,
      processingMode: 'verbatim',
    });

    const writingAfterReload = await apiGet<WritingContentDto>(
      baseUrl,
      `/api/writings/${writing.id}`,
    );
    expect(writingAfterReload.content).toBe(writingContent);
  } finally {
    // Remove the Writing first so its kept-source relation cannot retain the
    // note/book while fixture cleanup restores shared Brain state.
    if (writingId) {
      await apiDelete(baseUrl, `/api/writings/${writingId}`);
    }

    if (bookId) {
      await cleanupBrain(baseUrl, {
        bookId,
        topicNames: [],
        beforeTopicIds,
      });
    }
  }
});
