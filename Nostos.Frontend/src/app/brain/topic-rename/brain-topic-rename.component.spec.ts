import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BrainTopicRenameComponent } from './brain-topic-rename.component';

@Component({
  standalone: true,
  imports: [BrainTopicRenameComponent],
  template: `
    <app-brain-topic-rename
      [variant]="variant()"
      name="Alpha"
      [value]="value()"
      (valueChange)="value.set($event)"
      [error]="error()"
      [busy]="busy()"
      (committed)="committed = committed + 1"
      (cancelled)="cancelled = cancelled + 1"
    />
  `,
})
class HostComponent {
  variant = signal<'row' | 'title'>('row');
  value = signal('Alpha');
  error = signal<string | null>(null);
  busy = signal(false);
  committed = 0;
  cancelled = 0;
}

describe('BrainTopicRenameComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let element: HTMLElement;

  const input = () => element.querySelector<HTMLInputElement>('.inline-rename-input')!;
  const button = (name: string) =>
    element.querySelector<HTMLButtonElement>(`button[aria-label="${name}"]`)!;

  beforeEach(async () => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    element = fixture.nativeElement;
    document.body.appendChild(element);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  afterEach(() => element.remove());

  it('opens with the current name in the field, focused and selected', () => {
    expect(input().value).toBe('Alpha');
    expect(input().getAttribute('aria-label')).toBe('Rename Alpha');
    expect(document.activeElement).toBe(input());
    expect(input().selectionStart).toBe(0);
    expect(input().selectionEnd).toBe('Alpha'.length);
  });

  it('reports the text as it is typed', () => {
    input().value = 'Beta';
    input().dispatchEvent(new Event('input'));

    expect(host.value()).toBe('Beta');
  });

  it('saves on Enter and from the save button', () => {
    input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    button('Save name').click();

    expect(host.committed).toBe(2);
  });

  it('cancels on Escape and from the cancel button', () => {
    input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    button('Cancel rename').click();

    expect(host.cancelled).toBe(2);
  });

  it('keeps Enter and Escape from reaching the row or page around it', () => {
    let leaked = 0;
    element.addEventListener('keydown', () => leaked++);

    input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(leaked).toBe(0);
  });

  it('locks the field and both buttons while a save is running', () => {
    host.busy.set(true);
    fixture.detectChanges();

    expect(input().disabled).toBe(true);
    expect(button('Save name').disabled).toBe(true);
    expect(button('Cancel rename').disabled).toBe(true);
  });

  it('shows a refusal under the field and ties it to the field', () => {
    host.error.set('A topic name is required.');
    fixture.detectChanges();

    const message = element.querySelector('.inline-rename-error')!;
    expect(message.textContent).toBe('A topic name is required.');
    expect(message.getAttribute('role')).toBe('alert');
    expect(input().getAttribute('aria-invalid')).toBe('true');
    expect(input().getAttribute('aria-describedby')).toBe(message.id);
  });

  it('marks the title variant so the Brain can style it as the heading', () => {
    expect(element.querySelector('.header-rename')).toBeNull();

    host.variant.set('title');
    fixture.detectChanges();

    expect(element.querySelector('.header-rename .inline-rename-input')).toBeTruthy();
  });
});
