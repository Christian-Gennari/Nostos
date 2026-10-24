import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';

import { ScrollLock, ScrollLockDirective, lockScrollWhile } from './scroll-lock.service';

const LOCKED = 'nostos-scroll-locked';

@Component({
  standalone: true,
  imports: [ScrollLockDirective],
  template: `<div [appScrollLock]="open()"></div>`,
})
class HostComponent {
  readonly open = signal(false);
}

@Component({ standalone: true, template: '' })
class FunctionHostComponent {
  readonly open = signal(false);
  constructor() {
    lockScrollWhile(() => this.open());
  }
}

describe('ScrollLock', () => {
  afterEach(() => document.documentElement.classList.remove(LOCKED));

  it('is counted: the page is released when the last hold is given back', () => {
    const lock = TestBed.inject(ScrollLock);
    const first = lock.acquire();
    const second = lock.acquire();
    expect(document.documentElement.classList.contains(LOCKED)).toBe(true);

    first();
    expect(document.documentElement.classList.contains(LOCKED)).toBe(true);
    second();
    expect(document.documentElement.classList.contains(LOCKED)).toBe(false);
    expect(lock.holds).toBe(0);
  });

  it('counts a release once, however often it is called', () => {
    const lock = TestBed.inject(ScrollLock);
    const first = lock.acquire();
    const second = lock.acquire();
    first();
    first();
    expect(lock.holds).toBe(1);
    second();
    expect(document.documentElement.classList.contains(LOCKED)).toBe(false);
  });

  it('follows [appScrollLock] and lets go when the host is destroyed', async () => {
    const fixture = TestBed.createComponent(HostComponent);
    await fixture.whenStable();
    expect(document.documentElement.classList.contains(LOCKED)).toBe(false);

    fixture.componentInstance.open.set(true);
    await fixture.whenStable();
    expect(document.documentElement.classList.contains(LOCKED)).toBe(true);

    fixture.destroy();
    expect(document.documentElement.classList.contains(LOCKED)).toBe(false);
  });

  it('lockScrollWhile holds while active and is released by closing', async () => {
    const fixture = TestBed.createComponent(FunctionHostComponent);
    fixture.componentInstance.open.set(true);
    await fixture.whenStable();
    expect(TestBed.inject(ScrollLock).holds).toBe(1);

    fixture.componentInstance.open.set(false);
    await fixture.whenStable();
    expect(TestBed.inject(ScrollLock).holds).toBe(0);
  });
});
