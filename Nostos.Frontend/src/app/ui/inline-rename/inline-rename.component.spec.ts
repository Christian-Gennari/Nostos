import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { InlineRenameComponent } from './inline-rename.component';

@Component({
  standalone: true,
  imports: [InlineRenameComponent],
  template: `
    @if (open()) {
      <nostos-inline-rename
        [variant]="variant()"
        name="Alpha"
        [validate]="validate"
        [error]="error()"
        [busy]="busy()"
        (valueChange)="typed.set($event)"
        (committed)="committed.push($event)"
        (cancelled)="cancelled = cancelled + 1"
      />
    }
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly variant = signal<'row' | 'title'>('row');
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  readonly typed = signal('');
  readonly validate = (name: string) => (name.toLowerCase() === 'beta' ? '“Beta” is already used here.' : null);
  committed: string[] = [];
  cancelled = 0;
}

describe('InlineRenameComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let element: HTMLElement;

  const input = () => element.querySelector<HTMLInputElement>('.inline-rename-input')!;
  const error = () => element.querySelector<HTMLElement>('.inline-rename-error');
  const type = (value: string) => {
    input().value = value;
    input().dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const key = (name: string) => {
    input().dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }));
    fixture.detectChanges();
  };
  const blur = () => {
    input().dispatchEvent(new FocusEvent('blur'));
    fixture.detectChanges();
  };

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

  it('has no Save or Cancel buttons', () => {
    expect(element.querySelector('button')).toBeNull();
  });

  it('reports the text as it is typed', () => {
    type('Gamma');
    expect(host.typed()).toBe('Gamma');
  });

  it('saves the trimmed name on Enter', () => {
    type('  Gamma  ');
    key('Enter');
    expect(host.committed).toEqual(['Gamma']);
  });

  it('cancels on Escape', () => {
    type('Gamma');
    key('Escape');
    expect(host.cancelled).toBe(1);
    expect(host.committed).toEqual([]);
  });

  it('closes without saving when Enter finds the name unchanged', () => {
    key('Enter');
    expect(host.committed).toEqual([]);
    expect(host.cancelled).toBe(1);
  });

  it('saves on blur when the name changed and can be used', () => {
    type('Gamma');
    blur();
    expect(host.committed).toEqual(['Gamma']);
  });

  it('closes on blur, saving nothing, when the name is unchanged or empty', () => {
    blur();
    expect(host.cancelled).toBe(1);

    host.open.set(false);
    fixture.detectChanges();
    host.open.set(true);
    fixture.detectChanges();
    type('   ');
    blur();
    expect(host.committed).toEqual([]);
    expect(host.cancelled).toBe(2);
  });

  it('says under the field that an empty name cannot be saved, and keeps it open', () => {
    type('');
    key('Enter');
    expect(error()?.textContent).toBe('A name is required.');
    expect(error()?.getAttribute('role')).toBe('alert');
    expect(input().getAttribute('aria-invalid')).toBe('true');
    expect(input().getAttribute('aria-describedby')).toBe(error()?.id);
    expect(host.committed).toEqual([]);
    expect(host.cancelled).toBe(0);

    type('G');
    expect(error()).toBeNull();
  });

  it('refuses a name the owner’s check refuses, on Enter and on blur', () => {
    type('beta');
    key('Enter');
    expect(error()?.textContent).toBe('“Beta” is already used here.');
    blur();
    expect(host.committed).toEqual([]);
    expect(host.cancelled).toBe(0);
  });

  it('shows the owner’s refusal until the text changes, and again on the next save', () => {
    type('Gamma');
    key('Enter');
    host.error.set('Could not rename it. Try again.');
    fixture.detectChanges();
    expect(error()?.textContent).toBe('Could not rename it. Try again.');

    type('Gammas');
    expect(error()).toBeNull();

    key('Enter');
    expect(error()?.textContent).toBe('Could not rename it. Try again.');
    expect(host.committed).toEqual(['Gamma', 'Gammas']);
  });

  it('does not send the same text twice when the field is left after Enter', () => {
    type('Gamma');
    key('Enter');
    blur();
    expect(host.committed).toEqual(['Gamma']);
  });

  it('holds the text while a save runs, and ignores keys and blur', () => {
    type('Gamma');
    host.busy.set(true);
    fixture.detectChanges();
    expect(input().readOnly).toBe(true);
    expect(input().getAttribute('aria-busy')).toBe('true');
    key('Enter');
    blur();
    expect(host.committed).toEqual([]);
  });

  it('keeps Enter and Escape from reaching the row or page around it', () => {
    let leaked = 0;
    element.addEventListener('keydown', () => leaked++);
    key('Enter');
    key('Escape');
    expect(leaked).toBe(0);
  });

  it('keeps a click in the field from selecting the row under it', () => {
    let leaked = 0;
    element.addEventListener('click', () => leaked++);
    input().click();
    expect(leaked).toBe(0);
  });

  it('takes the heading’s place in the title variant', () => {
    host.variant.set('title');
    fixture.detectChanges();
    expect(element.querySelector('nostos-inline-rename')?.classList.contains('inline-rename--title')).toBe(true);
  });
});
