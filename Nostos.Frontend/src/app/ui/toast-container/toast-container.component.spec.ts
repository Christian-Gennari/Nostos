import { TestBed } from '@angular/core/testing';

import { ToastService } from '../../core/services/toast.service';
import { ToastContainerComponent } from './toast-container.component';

describe('ToastContainerComponent', () => {
  let service: ToastService;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ToastContainerComponent],
    }).compileComponents();

    service = TestBed.inject(ToastService);
  });

  it('uses polite status semantics for success/info and an assertive alert for errors', () => {
    const fixture = TestBed.createComponent(ToastContainerComponent);
    const host = fixture.nativeElement as HTMLElement;

    service.success('Saved');
    service.info('Refresh started');
    service.error('Could not save');
    fixture.detectChanges();

    const statuses = [...host.querySelectorAll<HTMLElement>('[role="status"]')];
    expect(statuses).toHaveLength(2);
    expect(statuses.every((status) => status.getAttribute('aria-live') === 'polite')).toBe(true);

    const alert = host.querySelector<HTMLElement>('[role="alert"]');
    expect(alert).not.toBeNull();
    expect(alert?.textContent).toContain('Could not save');
    expect(alert?.getAttribute('aria-live')).toBe('assertive');
    expect(alert?.getAttribute('aria-atomic')).toBe('true');
  });

  it('uses the shared square-ended accent rail for every semantic toast', () => {
    const fixture = TestBed.createComponent(ToastContainerComponent);
    const host = fixture.nativeElement as HTMLElement;

    service.success('Saved');
    service.info('Refresh started');
    service.error('Could not save');
    fixture.detectChanges();

    const toasts = [...host.querySelectorAll<HTMLElement>('.toast')];
    expect(toasts).toHaveLength(3);
    expect(toasts.every((toast) => toast.classList.contains('nostos-accent-rail'))).toBe(true);
  });

  it('keeps the canonical dismiss control outside the spoken live region', () => {
    const fixture = TestBed.createComponent(ToastContainerComponent);
    const host = fixture.nativeElement as HTMLElement;

    service.info('Refresh started');
    fixture.detectChanges();

    const dismiss = host.querySelector<HTMLButtonElement>('.toast-dismiss');
    expect(dismiss).not.toBeNull();
    expect(dismiss?.classList.contains('icon-btn')).toBe(true);
    expect(dismiss?.getAttribute('aria-label')).toBe('Dismiss notification');
    expect(dismiss?.getAttribute('title')).toBe('Dismiss notification');
    expect(dismiss?.getAttribute('type')).toBe('button');
    expect(dismiss?.closest('[role="status"], [role="alert"]')).toBeNull();
    expect(dismiss?.tabIndex).toBe(0);
  });

  it('does not steal focus and dismisses through the native keyboard-accessible button', () => {
    const fixture = TestBed.createComponent(ToastContainerComponent);
    const host = fixture.nativeElement as HTMLElement;
    const existingFocus = document.createElement('button');
    existingFocus.type = 'button';
    document.body.append(existingFocus);

    try {
      existingFocus.focus();
      service.info('Refresh started');
      fixture.detectChanges();

      expect(document.activeElement).toBe(existingFocus);

      host.querySelector<HTMLButtonElement>('.toast-dismiss')?.click();
      fixture.detectChanges();

      expect(service.toasts()).toEqual([]);
    } finally {
      existingFocus.remove();
    }
  });
});
