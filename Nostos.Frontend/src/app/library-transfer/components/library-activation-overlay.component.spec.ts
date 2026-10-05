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
