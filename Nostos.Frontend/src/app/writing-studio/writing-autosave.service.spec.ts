import { HttpErrorResponse } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { WritingContentDto } from '../core/dtos/writing.dtos';
import { ToastService } from '../core/services/toast.service';
import { WritingsService } from '../core/services/writings.service';
import { AutosaveHostAdapter, WritingAutosaveService } from './writing-autosave.service';

@Component({
  selector: 'app-autosave-host-harness',
  standalone: true,
  template: '',
  providers: [WritingAutosaveService],
})
class AutosaveHostHarnessComponent {
  readonly activeItem = signal<WritingContentDto | null>(null);
  readonly title = signal('');
  readonly content = signal('');
  readonly renamedCalls: Array<{ id: string; title: string }> = [];
  flushPendingCalls = 0;

  constructor(readonly autosave: WritingAutosaveService) {}

  readonly adapter: AutosaveHostAdapter = {
    getActiveItem: () => this.activeItem(),
    setActiveItem: (item) => this.activeItem.set(item),
    getTitle: () => this.title(),
    setTitle: (title) => this.title.set(title),
    getContent: () => this.content(),
    setContent: (content) => this.content.set(content),
    flushPendingEditorChange: () => {
      this.flushPendingCalls++;
    },
    onRenamedDuringSave: (id, title) => {
      this.renamedCalls.push({ id, title });
    },
  };
}

describe('WritingAutosaveService', () => {
  let fixture: ComponentFixture<AutosaveHostHarnessComponent>;
  let harness: AutosaveHostHarnessComponent;
  let writings: { update: ReturnType<typeof vi.fn> };
  let toast: { error: ReturnType<typeof vi.fn>; info: ReturnType<typeof vi.fn> };

  const doc: WritingContentDto = {
    id: 'doc-1',
    name: 'Draft',
    content: 'Initial',
    updatedAt: '2026-09-01T00:00:00Z',
  };

  async function settleQueue(): Promise<void> {
    for (let i = 0; i < 8; i++) await Promise.resolve();
  }

  beforeEach(async () => {
    writings = {
      update: vi.fn((_id: string, body: { name: string; content: string }) =>
        of({ ...doc, name: body.name, content: body.content, updatedAt: '2026-09-02T00:00:00Z' }),
      ),
    };
    toast = { error: vi.fn(), info: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [AutosaveHostHarnessComponent],
      providers: [
        { provide: WritingsService, useValue: writings },
        { provide: ToastService, useValue: toast },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AutosaveHostHarnessComponent);
    harness = fixture.componentInstance;
    harness.activeItem.set(doc);
    harness.title.set(doc.name);
    harness.content.set(doc.content);
  });

  afterEach(() => {
    vi.useRealTimers();
    fixture?.destroy();
  });

  it('cancels pending debounce timer cleanly on destroy without firing update', () => {
    vi.useFakeTimers();

    harness.autosave.scheduleDebouncedSave(doc, doc.name, 'Unsaved edits', harness.adapter, 2000);
    expect(harness.autosave.saveStatus()).toBe('Unsaved');

    fixture.destroy();
    vi.advanceTimersByTime(3000);

    expect(writings.update).not.toHaveBeenCalled();
  });

  it('unsubscribes in-flight save observable cleanly on destroy', async () => {
    let unsubscribed = false;
    writings.update.mockImplementationOnce(
      () =>
        new Observable<WritingContentDto>(() => {
          return () => {
            unsubscribed = true;
          };
        }),
    );

    const savePromise = harness.autosave.queueSave(doc, doc.name, 'Changed', 0, harness.adapter);
    void savePromise.catch(() => undefined);
    await Promise.resolve();
    await Promise.resolve();
    expect(writings.update).toHaveBeenCalledTimes(1);
    expect(unsubscribed).toBe(false);

    fixture.destroy();
    expect(unsubscribed).toBe(true);
  });

  it('handles 409 conflict and resolves Keep mine or Load newer', async () => {
    const theirs: WritingContentDto = {
      ...doc,
      content: 'Remote content',
      updatedAt: '2026-09-09T00:00:00Z',
    };
    writings.update.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: theirs })));

    harness.content.set('Local content');
    const ok = await harness.autosave.flushBeforeNavigation(doc, harness.adapter);
    expect(ok).toBe(false);
    expect(harness.autosave.saveConflict()).toEqual({
      theirs,
      title: doc.name,
      content: 'Local content',
    });

    // Keep mine overwrites without baseUpdatedAt.
    harness.autosave.keepMineAfterConflict(harness.adapter);
    await settleQueue();
    expect(writings.update).toHaveBeenLastCalledWith('doc-1', {
      name: doc.name,
      content: 'Local content',
    });
    expect(harness.autosave.saveConflict()).toBeNull();
    expect(harness.autosave.saveStatus()).toBe('Saved');
  });
});
