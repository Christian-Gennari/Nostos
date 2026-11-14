import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { LibraryItemFailure } from './library-item-failure.component';
import { ImportService } from '../../core/services/import.service';
import type { ImportActivity } from '../../core/dtos/import.dtos';

function activity(overrides: Partial<ImportActivity> = {}): ImportActivity {
  return {
    id: 'job-1',
    source: 'job',
    state: 'failed',
    stage: 'failed',
    percent: 0,
    detail: null,
    providerId: 'gutenberg',
    externalId: '84',
    assetId: null,
    bookId: 'b1',
    title: 'A Title',
    author: null,
    coverUrl: null,
    errorCode: 'disk_full',
    message: 'Disk full.',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:10Z',
    canRetry: true,
    ...overrides,
  };
}

@Component({
  standalone: true,
  imports: [LibraryItemFailure],
  template: `
    <app-library-item-failure [message]="message()" [failure]="failure()" />
  `,
})
class FailureHost {
  readonly message = signal('Disk full.');
  readonly failure = signal<ImportActivity | undefined>(undefined);
}

describe('LibraryItemFailure', () => {
  let fixture: ComponentFixture<FailureHost>;
  let host: FailureHost;
  let retry: ReturnType<typeof vi.fn>;
  let dismiss: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    retry = vi.fn();
    dismiss = vi.fn();
    await TestBed.configureTestingModule({
      imports: [FailureHost],
      providers: [{ provide: ImportService, useValue: { retry, dismiss, bookPatched: of(null) } }],
    }).compileComponents();
    fixture = TestBed.createComponent(FailureHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('says what broke even when the feed no longer knows the import', () => {
    const p = fixture.nativeElement.querySelector('.item-failure') as HTMLElement;
    expect(p.textContent).toContain('Disk full.');
    expect(fixture.nativeElement.querySelector('.item-failure-actions')).toBeNull();
  });

  it('offers Try again and Dismiss from the feed, without opening the book', () => {
    const failing = activity();
    host.failure.set(failing);
    fixture.detectChanges();

    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('.item-failure-actions .item-action'),
    ) as HTMLButtonElement[];
    expect(buttons.map((button) => button.textContent?.trim())).toEqual(['Try again', 'Dismiss']);

    buttons[0].click();
    expect(retry).toHaveBeenCalledWith(failing);

    buttons[1].click();
    expect(dismiss).toHaveBeenCalledWith(failing);
  });

  it('offers only Dismiss when the feed cannot retry', () => {
    host.failure.set(activity({ canRetry: false }));
    fixture.detectChanges();

    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('.item-failure-actions .item-action'),
    ) as HTMLButtonElement[];
    expect(buttons.map((button) => button.textContent?.trim())).toEqual(['Dismiss']);
  });
});
