import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LibraryReplacementDialogComponent } from './library-replacement-dialog.component';
import {
  MigrationArchiveCountsDto,
  MigrationExistingCountsDto,
  MigrationPreflightResponseDto,
} from '../models/migration-http.dtos';
import { LibraryActivationConflictFacts } from '../models/library-transfer.models';

function archiveCounts(overrides: Partial<MigrationArchiveCountsDto> = {}): MigrationArchiveCountsDto {
  return {
    works: 40,
    books: 126,
    notes: 932,
    topics: 4,
    noteTopics: 12,
    writings: 3,
    writingNotes: 8,
    collections: 21,
    collectionMemberships: 130,
    acquisitions: 9,
    assistantSettings: 1,
    noteImportBookLinks: 0,
    mediaEntries: 126,
    totalRows: 1400,
    ...overrides,
  };
}

function existingCounts(
  overrides: Partial<MigrationExistingCountsDto> = {},
): MigrationExistingCountsDto {
  return {
    works: 20,
    books: 74,
    notes: 418,
    topics: 2,
    noteTopics: 6,
    writings: 1,
    writingNotes: 4,
    collections: 12,
    bookCollections: 80,
    acquisitions: 5,
    assistantSettings: 1,
    noteImportBookLinks: 0,
    totalRows: 700,
    ...overrides,
  };
}

function preflight(
  overrides: Partial<MigrationPreflightResponseDto['evaluation']> = {},
): MigrationPreflightResponseDto {
  return {
    evaluation: {
      decision: 'AllowedReplacementRequired',
      isCompatible: true,
      isAllowed: true,
      incomingCounts: archiveCounts(),
      existingCounts: existingCounts(),
      declaredArchiveBytes: 10 * 1024 ** 3,
      declaredMediaBytes: 9 * 1024 ** 3,
      estimatedRecoveryBytes: 2 * 1024 ** 3,
      requiredStorageBytes: 12 * 1024 ** 3,
      availableStorageBytes: 40 * 1024 ** 3,
      errors: [],
      warnings: [],
      destinationRevision: 'rev-1',
      ...overrides,
    },
    reservationId: 'reservation-1',
    reservationExpiresAtUtc: null,
    chunkSizeBytes: 4 * 1024 * 1024,
  };
}

describe('LibraryReplacementDialogComponent', () => {
  let fixture: ComponentFixture<LibraryReplacementDialogComponent>;
  let component: LibraryReplacementDialogComponent;

  async function create(options: {
    preflight?: MigrationPreflightResponseDto | null;
    preparedImport?: unknown;
    supportsSafeActivation?: boolean;
    busy?: boolean;
    retryBlocked?: boolean;
    conflict?: LibraryActivationConflictFacts | null;
  } = {}): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LibraryReplacementDialogComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(LibraryReplacementDialogComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('preflight', options.preflight ?? preflight());
    if (options.preparedImport !== undefined) {
      fixture.componentRef.setInput('preparedImport', options.preparedImport);
    }
    fixture.componentRef.setInput('supportsSafeActivation', options.supportsSafeActivation ?? false);
    fixture.componentRef.setInput('busy', options.busy ?? false);
    fixture.componentRef.setInput('retryBlocked', options.retryBlocked ?? false);
    if (options.conflict !== undefined) {
      fixture.componentRef.setInput('conflict', options.conflict);
    }
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function element(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function confirmButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('.replacement-confirm') as HTMLButtonElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('is a labelled modal with the verified-archive description', async () => {
    await create();

    const card = fixture.nativeElement.querySelector('.replacement-dialog-card') as HTMLElement;
    expect(card.getAttribute('role')).toBe('alertdialog');
    expect(card.getAttribute('aria-modal')).toBe('true');
    expect(card.getAttribute('aria-labelledby')).toBe('replacement-dialog-title');
    expect(card.getAttribute('aria-describedby')).toBe('replacement-dialog-description');
    expect(card.querySelector('#replacement-dialog-title')?.textContent).toContain(
      'Replace this library?',
    );
    expect(card.querySelector('#replacement-dialog-description')?.textContent).toContain(
      'verified and ready',
    );
  });

  it('compares current and incoming counts and marks manifest counts as estimates', async () => {
    await create();

    expect(element('replacement-existing')?.textContent).toContain(
      '74 books · 418 notes · 12 collections',
    );
    expect(element('replacement-incoming')?.textContent).toContain(
      '126 books · 932 notes · 21 collections',
    );
    expect(element('replacement-counts-source')?.textContent).toContain('Estimated');
  });

  it('prefers server-derived prepared-import counts over the manifest estimate', async () => {
    await create({
      preparedImport: {
        incomingCounts: archiveCounts({ books: 200, notes: 999, collections: 30 }),
      },
    });

    expect(element('replacement-incoming')?.textContent).toContain(
      '200 books · 999 notes · 30 collections',
    );
    expect(element('replacement-counts-source')?.textContent).toContain('Checked by Nostos');
  });

  it('gates the destructive action until the host supports safe activation', async () => {
    await create({ supportsSafeActivation: false });

    expect(confirmButton().disabled).toBe(true);
    expect(element('replacement-blocked')?.textContent).toContain(
      'Replacement is not available on this Nostos host yet',
    );
    expect(element('replacement-recovery')).toBeNull();

    const confirmed = vi.fn();
    component.confirmed.subscribe(confirmed);
    confirmButton().click();
    expect(confirmed).not.toHaveBeenCalled();
  });

  it('enables the destructive action and emits exactly one confirmation', async () => {
    await create({ supportsSafeActivation: true });

    expect(confirmButton().disabled).toBe(false);
    expect(element('replacement-recovery')?.textContent).toContain('recovery copy');
    expect(element('replacement-blocked')).toBeNull();

    const confirmed = vi.fn();
    component.confirmed.subscribe(confirmed);

    confirmButton().click();
    confirmButton().click();

    expect(confirmed).toHaveBeenCalledTimes(1);
  });

  it('is sealed while busy: confirm and cancel are disabled and Escape emits nothing', async () => {
    await create({ supportsSafeActivation: true, busy: true });
    fixture.componentRef.setInput('busyLabel', 'Replacing library…');
    fixture.detectChanges();

    const confirmed = vi.fn();
    const cancelled = vi.fn();
    component.confirmed.subscribe(confirmed);
    component.cancelled.subscribe(cancelled);

    confirmButton().click();
    (fixture.nativeElement.querySelector('.replacement-cancel') as HTMLButtonElement).click();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(confirmed).not.toHaveBeenCalled();
    expect(cancelled).not.toHaveBeenCalled();
    expect(element('replacement-sealed')).toBeTruthy();
    expect(confirmButton().textContent).toContain('Replacing library…');
    expect(confirmButton().disabled).toBe(true);
  });

  it('shows a host activation error, unseals and re-arms the destructive action as retry', async () => {
    await create({ supportsSafeActivation: true });
    const confirmed = vi.fn();
    component.confirmed.subscribe(confirmed);

    confirmButton().click();
    expect(confirmed).toHaveBeenCalledTimes(1);

    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(element('replacement-sealed')).toBeTruthy();

    fixture.componentRef.setInput('errorMessage', 'Activation failed.');
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();

    expect(element('replacement-error')?.textContent).toContain('Activation failed.');
    expect(element('replacement-sealed')).toBeNull();
    expect(confirmButton().disabled).toBe(false);

    confirmButton().click();
    expect(confirmed).toHaveBeenCalledTimes(2);
  });

  it('emits cancelled from the Cancel action and from Escape', async () => {
    await create();
    const cancelled = vi.fn();
    component.cancelled.subscribe(cancelled);

    (fixture.nativeElement.querySelector('.replacement-cancel') as HTMLButtonElement).click();
    expect(cancelled).toHaveBeenCalledTimes(1);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(cancelled).toHaveBeenCalledTimes(2);
  });

  it('wires CDK focus trapping with initial focus on the non-destructive action', async () => {
    await create();

    const cancel = fixture.nativeElement.querySelector('.replacement-cancel') as HTMLButtonElement;
    // jsdom performs no layout, so CDK's visibility check cannot move focus in
    // tests; the wiring is the assertable contract here. The browser focus
    // behaviour is covered by the real-browser verdict in B10.
    expect(cancel.hasAttribute('cdkfocusinitial')).toBe(true);
    expect(document.querySelectorAll('.cdk-focus-trap-anchor').length).toBeGreaterThanOrEqual(2);
  });

  it('locks both actions while a replacement is in flight', async () => {
    await create({ supportsSafeActivation: true, busy: true });

    expect(confirmButton().disabled).toBe(true);
    expect(
      (fixture.nativeElement.querySelector('.replacement-cancel') as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it('shows the server 409 counts when the library changed and asks again', async () => {
    await create({
      supportsSafeActivation: true,
      conflict: {
        destinationRevision: 'rev-9',
        destinationStatus: 'Populated',
        existingCounts: existingCounts({ books: 9, notes: 12, collections: 2 }),
      },
    });

    expect(element('replacement-existing')?.textContent).toContain(
      '9 books · 12 notes · 2 collections',
    );
    expect(element('replacement-conflict')?.textContent).toContain('changed');

    const confirmed = vi.fn();
    component.confirmed.subscribe(confirmed);
    confirmButton().click();
    expect(confirmed).toHaveBeenCalledTimes(1);
  });

  it('blocks a retry after a fail-closed activation and explains the operator path', async () => {
    await create({ supportsSafeActivation: true, retryBlocked: true });

    expect(element('replacement-retry-blocked')?.textContent).toContain('restart');
    expect(confirmButton().disabled).toBe(true);

    const confirmed = vi.fn();
    component.confirmed.subscribe(confirmed);
    confirmButton().click();
    expect(confirmed).not.toHaveBeenCalled();
  });
});
