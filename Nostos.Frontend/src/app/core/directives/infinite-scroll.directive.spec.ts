import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { InfiniteScrollDirective, canObserveIntersections } from './infinite-scroll.directive';

class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = [];
  static reset(): void {
    FakeIntersectionObserver.instances = [];
  }

  readonly observed: Element[] = [];
  disconnected = false;

  constructor(
    readonly callback: IntersectionObserverCallback,
    readonly options: IntersectionObserverInit,
  ) {
    FakeIntersectionObserver.instances.push(this);
  }

  observe(element: Element): void {
    this.observed.push(element);
  }

  unobserve(): void {}

  disconnect(): void {
    this.disconnected = true;
  }

  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }

  intersect(isIntersecting: boolean): void {
    this.callback(
      [{ isIntersecting } as IntersectionObserverEntry],
      this as unknown as IntersectionObserver,
    );
  }
}

@Component({
  standalone: true,
  imports: [InfiniteScrollDirective],
  template: `<div
    class="sentinel"
    appInfiniteScroll
    [rootMargin]="margin()"
    [paused]="paused()"
    (reached)="reached = reached + 1"
  ></div>`,
})
class Host {
  readonly paused = signal(false);
  readonly margin = signal('100px');
  reached = 0;
}

describe('InfiniteScrollDirective', () => {
  let fixture: ComponentFixture<Host>;

  async function setup(): Promise<void> {
    FakeIntersectionObserver.reset();
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
  }

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('watches the list end with the margin it was given', async () => {
    await setup();
    fixture.componentInstance.margin.set('200px');
    fixture.detectChanges();

    const observer = FakeIntersectionObserver.instances.at(-1)!;
    expect(observer.options).toEqual({ root: null, rootMargin: '200px', threshold: 0 });
    expect(observer.observed).toEqual([fixture.nativeElement.querySelector('.sentinel')]);
  });

  it('asks for the next page when the end comes into view', async () => {
    await setup();
    const observer = FakeIntersectionObserver.instances[0];

    observer.intersect(false);
    expect(fixture.componentInstance.reached).toBe(0);

    observer.intersect(true);
    expect(fixture.componentInstance.reached).toBe(1);

    observer.intersect(true);
    expect(fixture.componentInstance.reached).toBe(2);
  });

  it('watches nothing while a page is loading or the last one failed', async () => {
    await setup();
    fixture.componentInstance.paused.set(true);
    fixture.detectChanges();

    // The observer for the undefined state is disconnected, and no new one is made.
    expect(FakeIntersectionObserver.instances).toHaveLength(1);
    expect(FakeIntersectionObserver.instances[0].disconnected).toBe(true);
  });

  it('watches afresh as soon as the pause lifts, so an end already in view pages again', async () => {
    await setup();
    fixture.componentInstance.paused.set(true);
    fixture.detectChanges();
    fixture.componentInstance.paused.set(false);
    fixture.detectChanges();

    expect(FakeIntersectionObserver.instances).toHaveLength(2);
    expect(FakeIntersectionObserver.instances[1].disconnected).toBe(false);

    FakeIntersectionObserver.instances[1].intersect(true);
    expect(fixture.componentInstance.reached).toBe(1);
  });

  it('stops watching when the list goes away', async () => {
    await setup();

    fixture.destroy();

    expect(FakeIntersectionObserver.instances[0].disconnected).toBe(true);
  });

  it('knows whether this browser can watch intersections at all', async () => {
    await setup();
    expect(canObserveIntersections()).toBe(true);

    vi.unstubAllGlobals();
    expect(canObserveIntersections()).toBe(false);
  });
});
