import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LibrarySelectionBar } from './library-selection-bar.component';
import type { Collection } from '../../core/dtos/collection.dtos';
import type { FlatTreeNode } from '../../ui/flat-tree/flat-tree.helper';
import { buildFlatTree } from '../../ui/flat-tree/flat-tree.helper';

@Component({
  standalone: true,
  imports: [LibrarySelectionBar],
  template: `
    <app-library-selection-bar
      [count]="count()"
      [collectionChoices]="choices()"
      [inCollection]="inCollection()"
      (closed)="closed.push(true)"
      (addToCollection)="added.push($event)"
      (removeFromCollection)="removed.push(true)"
      (markFinished)="finished.push(true)"
      (deleteRequested)="deleted.push(true)"
    />
  `,
})
class BarHost {
  readonly collections: Collection[] = [
    { id: 'c1', name: 'Classics', parentId: null },
    { id: 'c2', name: 'Poetry', parentId: 'c1' },
  ] as unknown as Collection[];
  readonly count = signal(2);
  readonly inCollection = signal(false);
  readonly choices = signal<FlatTreeNode<Collection>[]>(
    buildFlatTree(this.collections, new Set(['c1', 'c2']), true),
  );
  readonly closed: boolean[] = [];
  readonly added: string[] = [];
  readonly removed: boolean[] = [];
  readonly finished: boolean[] = [];
  readonly deleted: boolean[] = [];
}

describe('LibrarySelectionBar', () => {
  let fixture: ComponentFixture<BarHost>;
  let host: BarHost;
  const actions = () =>
    Array.from(fixture.nativeElement.querySelectorAll('.selection-action')) as HTMLButtonElement[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [BarHost] }).compileComponents();
    fixture = TestBed.createComponent(BarHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove()));

  it('reports how many books are ticked', () => {
    expect(fixture.nativeElement.querySelector('.selection-count')?.textContent?.trim()).toBe(
      '2 selected',
    );
  });

  it('disables every action when nothing is ticked', () => {
    host.count.set(0);
    fixture.detectChanges();

    expect(actions().every((action) => action.disabled)).toBe(true);
  });

  it('offers Remove from collection only while the shelf is one', () => {
    expect(actions().map((action) => action.getAttribute('aria-label'))).toEqual([
      'Add to collection…',
      'Mark finished',
      'Delete',
    ]);

    host.inCollection.set(true);
    fixture.detectChanges();

    expect(actions().map((action) => action.getAttribute('aria-label'))).toContain(
      'Remove from collection',
    );
  });

  it('reports the bulk actions instead of performing them', () => {
    host.inCollection.set(true);
    fixture.detectChanges();

    for (const action of actions()) {
      if (action.getAttribute('aria-label') !== 'Add to collection…') action.click();
    }

    expect(host.removed).toEqual([true]);
    expect(host.finished).toEqual([true]);
    expect(host.deleted).toEqual([true]);
  });

  it('lists every collection in tree order, indented by depth, and reports the chosen one', () => {
    const trigger = actions()[0];
    trigger.click();
    fixture.detectChanges();

    const items = Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item'));
    expect(items.map((item) => item.textContent?.trim())).toEqual(['Classics', 'Poetry']);
    expect(items[1].style.paddingLeft).toBe('1.65rem');

    items[1].click();
    fixture.detectChanges();

    expect(host.added).toEqual(['c2']);
  });

  it('says so when there is no collection to add to', () => {
    host.choices.set([]);
    fixture.detectChanges();

    actions()[0].click();
    fixture.detectChanges();

    const items = Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item'));
    expect(items.map((item) => item.textContent?.trim())).toEqual(['No collections yet']);
    // The menu primitive marks a disabled row with `aria-disabled` (CdkMenuItem),
    // not the native property.
    expect(items[0].getAttribute('aria-disabled')).toBe('true');
  });

  it('reports the close control', () => {
    (fixture.nativeElement.querySelector('.selection-close') as HTMLButtonElement).click();

    expect(host.closed).toEqual([true]);
  });
});
