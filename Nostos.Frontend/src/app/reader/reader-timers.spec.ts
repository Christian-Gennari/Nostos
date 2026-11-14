import { Component, inject } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReaderTimers } from './reader-timers';

/**
 * The reader's deferred work is one registry (issue #1607): leaving the reader
 * must cancel every focus, scroll and notice callback it started, instead of
 * letting one run against a view that is gone.
 */
@Component({ selector: 'app-reader-timers-host', standalone: true, template: '', providers: [ReaderTimers] })
class ReaderTimersHost {
  readonly timers = inject(ReaderTimers);
}

describe('ReaderTimers', () => {
  let fixture: ComponentFixture<ReaderTimersHost>;
  let timers: ReaderTimers;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ imports: [ReaderTimersHost] });
    fixture = TestBed.createComponent(ReaderTimersHost);
    timers = fixture.componentInstance.timers;
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('runs a callback after its delay and forgets the handle', () => {
    const run = vi.fn();
    timers.after(50, run);

    expect(timers.size).toBe(1);
    vi.advanceTimersByTime(49);
    expect(run).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(run).toHaveBeenCalledTimes(1);
    expect(timers.size).toBe(0);
  });

  it('cancels a superseded callback and leaves the others waiting', () => {
    const first = vi.fn();
    const second = vi.fn();
    const handle = timers.after(50, first);
    timers.after(50, second);

    timers.clear(handle);
    vi.advanceTimersByTime(50);

    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledTimes(1);
    expect(timers.size).toBe(0);
  });

  it('clears everything still waiting when the reader is destroyed', () => {
    const focus = vi.fn();
    const scroll = vi.fn();
    timers.after(0, focus);
    timers.after(100, scroll);
    expect(timers.size).toBe(2);

    fixture.destroy();

    vi.advanceTimersByTime(1000);
    expect(focus).not.toHaveBeenCalled();
    expect(scroll).not.toHaveBeenCalled();
    expect(timers.size).toBe(0);
  });

  it('ignores clearing a handle that is already gone', () => {
    const run = vi.fn();
    const handle = timers.after(10, run);
    vi.advanceTimersByTime(10);
    expect(run).toHaveBeenCalledTimes(1);

    expect(() => timers.clear(handle)).not.toThrow();
    expect(() => timers.clear(null)).not.toThrow();
    expect(timers.size).toBe(0);
  });
});
