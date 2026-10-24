import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { EmptyStateComponent } from './empty-state.component';

@Component({
  standalone: true,
  imports: [EmptyStateComponent],
  template: `
    <nostos-empty-state class="plain" concept="note" message="No notes yet." detail="Highlight text to add one.">
      <button type="button" class="projected">Add a note</button>
    </nostos-empty-state>
    <nostos-empty-state
      class="failed"
      compact
      error
      message="Topics could not be loaded."
      (retry)="retried.set(retried() + 1)"
    />
    <nostos-empty-state class="final" error [retryable]="false" message="Reload the page to try again." />
  `,
})
class EmptyStateHarnessComponent {
  readonly retried = signal(0);
}

describe('EmptyStateComponent', () => {
  let fixture: ComponentFixture<EmptyStateHarnessComponent>;
  const el = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [EmptyStateHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(EmptyStateHarnessComponent);
    fixture.detectChanges();
  });

  it('shows the glyph, the message, the secondary line and projected actions as a status', () => {
    const plain = el('.plain');
    expect(plain.getAttribute('role')).toBe('status');
    expect(plain.querySelector('nostos-icon')).not.toBeNull();
    expect(plain.querySelector('.nostos-empty-state__message')?.textContent).toContain('No notes yet.');
    expect(plain.querySelector('.nostos-empty-state__detail')?.textContent).toContain('Highlight text');
    expect(plain.querySelector('.nostos-empty-state__actions .projected')).not.toBeNull();
    expect(plain.textContent).not.toContain('Try again');
  });

  it('turns a failed load into an alert with one "Try again"', () => {
    const failed = el('.failed');
    expect(failed.getAttribute('role')).toBe('alert');
    expect(failed.classList).toContain('is-compact');
    expect(failed.querySelector('nostos-icon')).toBeNull();
    const retry = failed.querySelector('button') as HTMLButtonElement;
    expect(retry.textContent?.trim()).toBe('Try again');
    retry.click();
    expect(fixture.componentInstance.retried()).toBe(1);
  });

  it('leaves the retry out when there is nothing to retry', () => {
    const final = el('.final');
    expect(final.getAttribute('role')).toBe('alert');
    expect(final.querySelector('button')).toBeNull();
  });
});
