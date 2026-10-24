import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PagingStatusComponent } from './paging-status.component';

type ObserverCallback = (entries: Partial<IntersectionObserverEntry>[]) => void;

class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = [];
  disconnected = false;
  constructor(readonly callback: ObserverCallback) {
    FakeIntersectionObserver.instances.push(this);
  }
  observe(): void {}
  disconnect(): void {
    this.disconnected = true;
  }
  fire(isIntersecting: boolean): void {
    this.callback([{ isIntersecting }]);
  }
}

@Component({
  standalone: true,
  imports: [PagingStatusComponent],
  template: `
    <nostos-paging-status
      [hasMore]="hasMore()"
      [loading]="loading()"
      [failed]="failed()"
      label="Loading more notes"
      failedMessage="More notes could not be loaded."
      (more)="more.set(more() + 1)"
      (retry)="retried.set(retried() + 1)"
    />
  `,
})
class PagingHarnessComponent {
  readonly hasMore = signal(true);
  readonly loading = signal(false);
  readonly failed = signal(false);
  readonly more = signal(0);
  readonly retried = signal(0);
}

describe('PagingStatusComponent', () => {
  let fixture: ComponentFixture<PagingHarnessComponent>;
  let host: PagingHarnessComponent;
  const globals = globalThis as { IntersectionObserver?: unknown };
  const original = globals.IntersectionObserver;
  const live = () => FakeIntersectionObserver.instances.filter((o) => !o.disconnected);

  beforeEach(async () => {
    FakeIntersectionObserver.instances = [];
    globals.IntersectionObserver = FakeIntersectionObserver;
    await TestBed.configureTestingModule({ imports: [PagingHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(PagingHarnessComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    globals.IntersectionObserver = original;
  });

  it('asks for the next page when the list end comes into view', () => {
    expect(live().length).toBe(1);
    live()[0].fire(false);
    expect(host.more()).toBe(0);
    live()[0].fire(true);
    expect(host.more()).toBe(1);
  });

  it('stops watching while a page loads and watches afresh once it has', () => {
    host.loading.set(true);
    fixture.detectChanges();
    expect(live().length).toBe(0);
    expect(fixture.nativeElement.querySelector('nostos-loading-indicator')?.textContent).toContain(
      'Loading more notes',
    );

    host.loading.set(false);
    fixture.detectChanges();
    expect(live().length).toBe(1);
  });

  it('keeps a "Load more" fallback in the tab order', () => {
    const fallback = fixture.nativeElement.querySelector('.nostos-paging-status__fallback') as HTMLButtonElement;
    expect(fallback.textContent?.trim()).toBe('Load more');
    expect(fallback.tabIndex).toBe(0);
    fallback.click();
    expect(host.more()).toBe(1);
  });

  it('offers an inline "Try again" when a page fails, without asking again on its own', () => {
    host.failed.set(true);
    fixture.detectChanges();
    expect(live().length).toBe(0);
    const failed = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(failed.textContent).toContain('More notes could not be loaded.');
    (failed.querySelector('button') as HTMLButtonElement).click();
    expect(host.retried()).toBe(1);
  });

  it('draws nothing when there is no next page', () => {
    host.hasMore.set(false);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.nostos-paging-status__end')).toBeNull();
  });
});
