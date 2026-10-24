import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ModalShell } from './modal-shell.component';

describe('ModalShell', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<ModalShell>>;
  let shell: ModalShell;

  const card = () => fixture.nativeElement.querySelector('.modal-card') as HTMLElement;
  const backdrop = () => fixture.nativeElement.querySelector('.modal-backdrop') as HTMLElement;
  const head = () => fixture.nativeElement.querySelector('.modal-head') as HTMLElement;
  const dispatchPointer = (
    element: HTMLElement,
    type: 'pointerdown' | 'pointerup' | 'pointercancel',
    pointerId = 1,
  ) => {
    const event = new Event(type, { bubbles: true }) as PointerEvent;
    Object.defineProperty(event, 'pointerId', { value: pointerId });
    element.dispatchEvent(event);
  };

  const pressBackdrop = (pointerId = 1) => {
    dispatchPointer(backdrop(), 'pointerdown', pointerId);
    dispatchPointer(backdrop(), 'pointerup', pointerId);
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ModalShell] }).compileComponents();

    fixture = TestBed.createComponent(ModalShell);
    shell = fixture.componentInstance;
    // isOpen is required; provide it before the first change detection.
    fixture.componentRef.setInput('isOpen', true);
    await fixture.whenStable();
  });

  it('renders nothing at all while closed', async () => {
    fixture.componentRef.setInput('isOpen', false);
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('.modal-backdrop')).toBeNull();
    expect(fixture.nativeElement.querySelector('.modal-card')).toBeNull();
  });

  it('exposes the dialog role and aria-modal', () => {
    expect(card().getAttribute('role')).toBe('dialog');
    expect(card().getAttribute('aria-modal')).toBe('true');
  });

  it('forwards the caller\u2019s aria labelling, and omits it when the caller has none', () => {
    expect(card().getAttribute('aria-labelledby')).toBeNull();
    expect(card().getAttribute('aria-describedby')).toBeNull();

    fixture.componentRef.setInput('ariaLabelledBy', 'confirm-dialog-title');
    fixture.componentRef.setInput('ariaDescribedBy', 'confirm-dialog-description');
    fixture.detectChanges();

    expect(card().getAttribute('aria-labelledby')).toBe('confirm-dialog-title');
    expect(card().getAttribute('aria-describedby')).toBe('confirm-dialog-description');
  });

  it('uses the alertdialog role for a destructive question', () => {
    fixture.componentRef.setInput('dialogRole', 'alertdialog');
    fixture.detectChanges();

    expect(card().getAttribute('role')).toBe('alertdialog');
  });

  it('applies the caller\u2019s card class and its stacking layer', () => {
    fixture.componentRef.setInput('cardClass', 'confirm-modal-card');
    fixture.componentRef.setInput('layer', 120);
    fixture.detectChanges();

    expect(card().classList).toContain('confirm-modal-card');
    expect(backdrop().style.zIndex).toBe('120');
  });

  it('marks a sheet as such, and leaves a dialog a centred card', () => {
    expect(card().classList).toContain('is-sheet');
    expect(backdrop().classList).toContain('is-sheet');

    fixture.componentRef.setInput('variant', 'dialog');
    fixture.detectChanges();

    expect(card().classList).not.toContain('is-sheet');
    expect(backdrop().classList).not.toContain('is-sheet');
  });

  it('binds the caller\u2019s own width and height as custom properties', () => {
    fixture.componentRef.setInput('maxWidth', '560px');
    fixture.componentRef.setInput('maxHeight', 'min(640px, calc(100vh - 2.5rem))');
    fixture.detectChanges();

    expect(card().style.getPropertyValue('--modal-max-width')).toBe('560px');
    expect(card().style.getPropertyValue('--modal-max-height')).toBe(
      'min(640px, calc(100vh - 2.5rem))',
    );
  });

  it('freezes the page behind it while open and lets go on close', async () => {
    expect(document.documentElement.classList.contains('nostos-scroll-locked')).toBe(true);

    fixture.componentRef.setInput('isOpen', false);
    await fixture.whenStable();
    expect(document.documentElement.classList.contains('nostos-scroll-locked')).toBe(false);
  });

  it('shows the header divider unless the caller opts out', () => {
    expect(head().classList).toContain('is-divided');

    fixture.componentRef.setInput('headDivider', false);
    fixture.detectChanges();

    expect(head().classList).not.toContain('is-divided');
  });

  it('emits closed when a pointer press starts and ends on the backdrop', () => {
    const closed = vi.fn();
    shell.closed.subscribe(closed);

    pressBackdrop();

    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('ignores a backdrop click when the caller forbids dismissal', () => {
    // A form dialog passes false: a stray thumb near the edge must not discard
    // a form the user has been filling in.
    const closed = vi.fn();
    shell.closed.subscribe(closed);
    fixture.componentRef.setInput('dismissOnBackdrop', false);
    fixture.detectChanges();

    pressBackdrop();

    expect(closed).not.toHaveBeenCalled();
  });

  it('does not close when the pointer press starts and ends on the card itself', () => {
    const closed = vi.fn();
    shell.closed.subscribe(closed);

    dispatchPointer(card(), 'pointerdown');
    dispatchPointer(card(), 'pointerup');

    expect(closed).not.toHaveBeenCalled();
  });

  it('does not close when a pointer drag starts inside the card and releases on the backdrop', () => {
    const closed = vi.fn();
    shell.closed.subscribe(closed);

    dispatchPointer(card(), 'pointerdown');
    dispatchPointer(backdrop(), 'pointerup');

    expect(closed).not.toHaveBeenCalled();
  });

  it('emits closed on Escape while open, and not while closed', async () => {
    const closed = vi.fn();
    shell.closed.subscribe(closed);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(closed).toHaveBeenCalledTimes(1);

    fixture.componentRef.setInput('isOpen', false);
    await fixture.whenStable();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('locks every dismissal path while the caller is busy', () => {
    const closed = vi.fn();
    shell.closed.subscribe(closed);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    pressBackdrop();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(closed).not.toHaveBeenCalled();
  });

  it('unlocks again once the caller reports it is no longer busy', () => {
    const closed = vi.fn();
    shell.closed.subscribe(closed);

    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    pressBackdrop();
    expect(closed).not.toHaveBeenCalled();

    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
    pressBackdrop();

    expect(closed).toHaveBeenCalledTimes(1);
  });
});

/**
 * The regions are the reason the shell exists, so they are asserted through a
 * host that projects into them the way the real callers do.
 */
@Component({
  standalone: true,
  imports: [ModalShell],
  template: `
    <app-modal-shell [isOpen]="true">
      <header class="host-head" shellHeader>Heading</header>
      <div class="host-tabs" shellTabs>Tabs</div>
      <p class="host-body">Body</p>
      <div class="host-actions" shellActions>Actions</div>
    </app-modal-shell>
  `,
})
class ProjectionHost {}

describe('ModalShell projection', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<ProjectionHost>>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ProjectionHost] }).compileComponents();
    fixture = TestBed.createComponent(ProjectionHost);
    await fixture.whenStable();
  });

  it('puts the header, tabs, body and actions in their own regions', () => {
    const el = fixture.nativeElement;

    expect(el.querySelector('.modal-head .host-head')).toBeTruthy();
    expect(el.querySelector('.modal-card > .host-tabs')).toBeTruthy();
    expect(el.querySelector('.modal-scroll .host-body')).toBeTruthy();
    expect(el.querySelector('.modal-card > .host-actions')).toBeTruthy();
  });

  it('scrolls ONLY the body — header, tabs and actions stay put', () => {
    // A long form must not carry its save button or its close control off the
    // screen; that is the whole point of the shell.
    const scroller = fixture.nativeElement.querySelector('.modal-scroll');

    expect(scroller.querySelector('.host-body')).toBeTruthy();
    expect(scroller.querySelector('.host-head')).toBeNull();
    expect(scroller.querySelector('.host-tabs')).toBeNull();
    expect(scroller.querySelector('.host-actions')).toBeNull();
  });
});
