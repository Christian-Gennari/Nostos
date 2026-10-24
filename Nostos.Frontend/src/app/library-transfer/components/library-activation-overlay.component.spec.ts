import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LibraryActivationOverlayComponent } from './library-activation-overlay.component';

describe('LibraryActivationOverlayComponent', () => {
  let fixture: ComponentFixture<LibraryActivationOverlayComponent>;

  function create(): void {
    TestBed.configureTestingModule({ imports: [LibraryActivationOverlayComponent] });
    fixture = TestBed.createComponent(LibraryActivationOverlayComponent);
    fixture.detectChanges();
  }

  function overlay(): HTMLElement | null {
    return fixture.nativeElement.querySelector('[data-testid="library-activation-overlay"]');
  }

  afterEach(() => TestBed.resetTestingModule());

  it('blocks the palette and assistant shortcuts while it covers the app, and releases them after', () => {
    create();
    const seen = vi.fn();
    document.addEventListener('keydown', seen);
    const chord = (key: string) => {
      const event = new KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true, cancelable: true });
      document.body.dispatchEvent(event);
      return event;
    };

    fixture.componentRef.setInput('state', 'in-progress');
    fixture.detectChanges();
    TestBed.flushEffects?.();
    expect(chord('k').defaultPrevented).toBe(true);
    expect(chord('j').defaultPrevented).toBe(true);
    expect(seen).not.toHaveBeenCalled();

    fixture.componentRef.setInput('state', 'idle');
    fixture.detectChanges();
    TestBed.flushEffects?.();
    chord('k');
    expect(seen).toHaveBeenCalledTimes(1);
    document.removeEventListener('keydown', seen);
  });

  it('renders nothing while activation is idle or completed', () => {
    create();

    expect(overlay()).toBeNull();

    fixture.componentRef.setInput('state', 'completed');
    fixture.detectChanges();
    expect(overlay()).toBeNull();
  });

  it('covers the app with a non-dismissible boundary while activation runs', () => {
    create();
    fixture.componentRef.setInput('state', 'in-progress');
    fixture.componentRef.setInput('phase', 'Preparing');
    fixture.detectChanges();

    const boundary = overlay() as HTMLElement;
    expect(boundary).toBeTruthy();
    expect(boundary.getAttribute('role')).toBe('alert');
    expect(boundary.getAttribute('aria-busy')).toBe('true');
    expect(boundary.textContent).toContain('Switching libraries…');
    expect(
      fixture.nativeElement.querySelector('[data-testid="library-activation-overlay-phase"]')
        ?.textContent,
    ).toContain('Preparing your imported library…');

    // No dismiss affordance exists: no buttons, no close handlers, and Escape
    // leaves the boundary in place.
    expect(boundary.querySelectorAll('button')).toHaveLength(0);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(overlay()).toBeTruthy();
    expect(document.querySelectorAll('.cdk-focus-trap-anchor').length).toBeGreaterThanOrEqual(2);
  });
});
