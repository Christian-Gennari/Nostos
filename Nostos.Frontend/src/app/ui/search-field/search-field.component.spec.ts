import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';

import { InputDirective } from '../form-control/form-control.directive';
import { SearchFieldComponent } from './search-field.component';

@Component({
  standalone: true,
  imports: [SearchFieldComponent, InputDirective, FormsModule],
  template: `
    <nostos-search-field class="bound" (cleared)="cleared = cleared + 1">
      <input appInput type="search" placeholder="Search books…" aria-label="Search books"
        [ngModel]="query()" (ngModelChange)="query.set($event)" />
    </nostos-search-field>
    <nostos-search-field class="plain" size="sm" [clearable]="false">
      <input appInput type="search" aria-label="Search this book" value="whale" />
    </nostos-search-field>
  `,
})
class HostComponent {
  readonly query = signal('');
  cleared = 0;
}

describe('SearchFieldComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let element: HTMLElement;

  const field = (cls: string) => element.querySelector(`.${cls}`) as HTMLElement;
  const input = (cls: string) => field(cls).querySelector('input') as HTMLInputElement;
  const clear = (cls: string) =>
    field(cls).querySelector<HTMLButtonElement>('.nostos-search-field__clear');

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

  it('frames the caller’s own input with a leading glass', () => {
    expect(field('bound').classList.contains('nostos-search-field')).toBe(true);
    expect(field('bound').querySelector('.nostos-search-field__icon')).toBeTruthy();
    expect(input('bound').type).toBe('search');
    expect(input('bound').getAttribute('aria-label')).toBe('Search books');
    expect(field('plain').classList.contains('nostos-search-field--sm')).toBe(true);
  });

  it('shows the clear button only once there is something to clear', async () => {
    expect(clear('bound')).toBeNull();

    input('bound').value = 'dune';
    input('bound').dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    expect(host.query()).toBe('dune');
    expect(clear('bound')?.getAttribute('aria-label')).toBe('Clear search');
  });

  it('notices a value the owner wrote without typing', async () => {
    host.query.set('written');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(input('bound').value).toBe('written');
    expect(clear('bound')).toBeTruthy();
  });

  it('clears the way typing would: the model empties, focus stays in the field', () => {
    input('bound').value = 'dune';
    input('bound').dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    clear('bound')!.click();
    fixture.detectChanges();

    expect(input('bound').value).toBe('');
    expect(host.query()).toBe('');
    expect(host.cleared).toBe(1);
    expect(document.activeElement).toBe(input('bound'));
    expect(clear('bound')).toBeNull();
  });

  it('draws no clear button where the caller turned it off', () => {
    expect(input('plain').value).toBe('whale');
    expect(clear('plain')).toBeNull();
  });
});
