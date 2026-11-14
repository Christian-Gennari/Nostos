import { Component, ElementRef, inject, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReaderOverlayFocus } from './reader-overlay-focus';
import { ReaderTimers } from './reader-timers';

@Component({
  selector: 'app-reader-overlay-focus-host',
  standalone: true,
  providers: [ReaderTimers, ReaderOverlayFocus],
  template: `
    <button #opener type="button">Notes</button>
    <div #panel><button #inside type="button">Close notes</button></div>
  `,
})
class ReaderOverlayFocusHost {
  readonly focus = inject(ReaderOverlayFocus);
  readonly opener = viewChild.required<ElementRef<HTMLButtonElement>>('opener');
  readonly inside = viewChild.required<ElementRef<HTMLButtonElement>>('inside');
  readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');
}

describe('ReaderOverlayFocus (#1607)', () => {
  let fixture: ComponentFixture<ReaderOverlayFocusHost>;
  let host: ReaderOverlayFocusHost;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ imports: [ReaderOverlayFocusHost] });
    fixture = TestBed.createComponent(ReaderOverlayFocusHost);
    fixture.detectChanges();
    host = fixture.componentInstance;
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('moves focus into the overlay and back to the control that opened it', () => {
    host.opener().nativeElement.focus();
    host.focus.remember();

    host.focus.focus(host.panel().nativeElement, '.inside, button');
    vi.advanceTimersByTime(0);
    expect(document.activeElement).toBe(host.inside().nativeElement);

    host.focus.restore();
    vi.advanceTimersByTime(0);
    expect(document.activeElement).toBe(host.opener().nativeElement);
  });

  it('does not restore focus once the reader is gone', () => {
    host.opener().nativeElement.focus();
    host.focus.remember();
    host.inside().nativeElement.focus();
    host.focus.restore();

    fixture.destroy();
    vi.advanceTimersByTime(0);

    // The timer was cancelled with the reader; the detached button never took
    // focus back from whatever the page now shows.
    expect(document.activeElement).not.toBe(host.opener().nativeElement);
  });

  it('forgets the return target when the overlay closes by another route', () => {
    host.opener().nativeElement.focus();
    host.focus.remember();
    host.focus.forget();
    host.inside().nativeElement.focus();

    host.focus.restore();
    vi.advanceTimersByTime(0);
    expect(document.activeElement).toBe(host.inside().nativeElement);
  });
});
