import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CollectionPickerComponent } from './collection-picker.component';
import { Collection } from '../../core/dtos/collection.dtos';

const collections: Collection[] = [
  { id: 'root', name: 'Root', parentId: null },
  { id: 'child', name: 'Child', parentId: 'root' },
  { id: 'other', name: 'Other', parentId: null },
];

describe('CollectionPickerComponent', () => {
  let fixture: ComponentFixture<CollectionPickerComponent>;
  let component: CollectionPickerComponent;

  function emitSpy() {
    const spy = vi.fn();
    component.selectedIdsChange.subscribe(spy);
    return spy;
  }

  function optionButtons(): HTMLLabelElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.option'));
  }

  /** The row's checkbox (issue #1204: the shared appCheckbox). */
  function optionByName(name: string): HTMLInputElement {
    return optionButtons()
      .find((b) => b.textContent?.trim() === name)!
      .querySelector('input[type=checkbox]') as HTMLInputElement;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CollectionPickerComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(CollectionPickerComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('collections', collections);
    fixture.componentRef.setInput('selectedIds', []);
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('renders one toggle per collection, including nested ones', () => {
    // Alphabetical within each level, children after their parent — the shared
    // buildFlatTree semantics the sidebar also uses, not a local ordering.
    const names = optionButtons().map((b) => b.textContent?.trim());
    expect(names).toEqual(['Other', 'Root', 'Child']);
  });

  it('marks nested rows with their tree depth so indentation is data-driven', () => {
    const child = optionByName('Child').closest('.option') as HTMLElement;
    expect(child.style.getPropertyValue('--depth')).toBe('1');
  });

  it('selecting a SECOND collection emits BOTH — the bug this replaces', () => {
    // A single-choice <select> could only ever emit one id here, silently
    // replacing the first collection. A set must keep both.
    fixture.componentRef.setInput('selectedIds', ['root']);
    fixture.detectChanges();
    const spy = emitSpy();

    optionByName('Other').click();

    expect(spy).toHaveBeenCalledWith(['root', 'other']);
  });

  it('unselecting one collection emits the remainder, not an empty set', () => {
    fixture.componentRef.setInput('selectedIds', ['root', 'other']);
    fixture.detectChanges();
    const spy = emitSpy();

    optionByName('Other').click();

    expect(spy).toHaveBeenCalledWith(['root']);
  });

  it('unselecting the LAST collection emits an empty set (previously impossible)', () => {
    fixture.componentRef.setInput('selectedIds', ['root']);
    fixture.detectChanges();
    const spy = emitSpy();

    optionByName('Root').click();

    expect(spy).toHaveBeenCalledWith([]);
  });

  it('exposes membership as a checked checkbox for assistive tech', () => {
    fixture.componentRef.setInput('selectedIds', ['root']);
    fixture.detectChanges();

    expect(optionByName('Root').checked).toBe(true);
    expect(optionByName('Root').classList).toContain('nostos-check');
    expect(optionByName('Other').checked).toBe(false);
  });

  it('renders a chip per membership and removes just that one', () => {
    fixture.componentRef.setInput('selectedIds', ['root', 'other']);
    fixture.detectChanges();

    const chips = fixture.nativeElement.querySelectorAll('.chip');
    expect(chips.length).toBe(2);
    const spy = emitSpy();

    const removeButton = fixture.nativeElement.querySelectorAll('.topic-chip-remove')[0];
    removeButton.click();

    expect(spy).toHaveBeenCalledWith(['other']);
  });

  it('clear all emits an empty set', () => {
    fixture.componentRef.setInput('selectedIds', ['root', 'other']);
    fixture.detectChanges();
    const spy = emitSpy();

    fixture.nativeElement.querySelector('.picker-clear').click();

    expect(spy).toHaveBeenCalledWith([]);
  });

  it('ignores stale ids that no longer resolve to a collection', () => {
    fixture.componentRef.setInput('selectedIds', ['gone']);
    fixture.detectChanges();

    // Stale ids must not crash the chip row...
    expect(fixture.nativeElement.querySelectorAll('.chip').length).toBe(0);
    // ...but they are still reported back verbatim while toggling, so the parent
    // keeps the server's truth until it saves.
    const spy = emitSpy();
    optionByName('Root').click();
    expect(spy).toHaveBeenCalledWith(['gone', 'root']);
  });

  it('does not mutate the array it was handed', () => {
    const original = ['root'];
    fixture.componentRef.setInput('selectedIds', original);
    fixture.detectChanges();

    optionByName('Other').click();

    expect(original).toEqual(['root']);
  });
});
