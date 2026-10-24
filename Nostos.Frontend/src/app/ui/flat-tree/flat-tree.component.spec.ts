import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CdkDragMove } from '@angular/cdk/drag-drop';

import { FlatTreeComponent } from './flat-tree.component';

const items = [
  { id: 'a', name: 'Alpha', parentId: null },
  { id: 'b', name: 'Beta', parentId: null },
  { id: 'c', name: 'Child', parentId: 'a' },
  { id: 'd', name: 'Grandchild', parentId: 'c' },
];

describe('FlatTreeComponent', () => {
  let component: FlatTreeComponent;
  let fixture: ComponentFixture<FlatTreeComponent>;

  function expandAlpha(): void {
    const aRow = fixture.nativeElement.querySelector('[data-node-id="a"]') as HTMLElement;
    (aRow.querySelector('.toggle-btn') as HTMLButtonElement).click();
    fixture.detectChanges();
    // Also expand Child so Grandchild is visible; rows: a, c, d, b.
    const cRow = fixture.nativeElement.querySelector('[data-node-id="c"]') as HTMLElement;
    (cRow.querySelector('.toggle-btn') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function mockRowRect(selector: string, top: number, bottom: number): void {
    const el = fixture.nativeElement.querySelector(selector) as HTMLElement;
    vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({
      top,
      bottom,
      left: 0,
      right: 0,
      width: 0,
      height: bottom - top,
      x: 0,
      y: top,
      toJSON: () => ({}),
    } as unknown as DOMRect);
  }

  function dragMove(y: number): void {
    component.onDragMoved({ pointerPosition: { x: 0, y } } as unknown as CdkDragMove);
  }

  function dragNode(index: number): void {
    component.onDragStarted(component.treeNodes()[index]);
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FlatTreeComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(FlatTreeComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('items', items);
    fixture.componentRef.setInput('treatAllAsFolders', true);
    fixture.detectChanges();
  });

  it('hovering a folder row emits an inside drop zone (no above/below)', () => {
    dragNode(1); // Beta
    dragMove(0); // pointer over the first row (Alpha)

    expect(component.dropIndicator()).toEqual({ nodeId: 'a', zone: 'inside' });
  });

  it('below all rows emits the root drop zone', () => {
    expandAlpha(); // exposes Child (parentId 'a'), which is draggable to root
    dragNode(1); // Child
    dragMove(500); // below every row

    expect(component.dropIndicator()).toEqual({ nodeId: '__root__', zone: 'inside' });
  });

  it('dropping inside a folder emits nodeMoved with that folder as parent', () => {
    const moved = vi.fn();
    component.nodeMoved.subscribe(moved);

    dragNode(1); // Beta
    dragMove(0); // over Alpha
    component.onDragEnded();

    expect(moved).toHaveBeenCalledWith({ item: items[1], newParentId: 'a' });
  });

  it('root drop emits nodeMoved with a null parent', () => {
    const moved = vi.fn();
    component.nodeMoved.subscribe(moved);

    expandAlpha();
    dragNode(1); // Child
    dragMove(500);
    component.onDragEnded();

    expect(moved).toHaveBeenCalledWith({ item: items[2], newParentId: null });
  });

  it('cycle guard rejects dropping an ancestor into its own descendant', () => {
    const moved = vi.fn();
    component.nodeMoved.subscribe(moved);

    expandAlpha(); // rows: Alpha, Child, Grandchild, Beta
    mockRowRect('[data-node-id="a"]', 0, 50);
    mockRowRect('[data-node-id="c"]', 100, 150);
    mockRowRect('[data-node-id="d"]', 200, 250);
    mockRowRect('[data-node-id="b"]', 300, 350);

    dragNode(1); // Child
    dragMove(225); // over Grandchild, which is a descendant of Child

    expect(component.dropIndicator()).toBeNull();
    component.onDragEnded();
    expect(moved).not.toHaveBeenCalled();
  });

  it('no-op guard rejects dropping a folder into its current parent', () => {
    const moved = vi.fn();
    component.nodeMoved.subscribe(moved);

    expandAlpha(); // rows: Alpha, Child, Grandchild, Beta
    mockRowRect('[data-node-id="a"]', 0, 50);
    mockRowRect('[data-node-id="c"]', 100, 150);
    mockRowRect('[data-node-id="d"]', 200, 250);
    mockRowRect('[data-node-id="b"]', 300, 350);

    dragNode(1); // Child (already inside Alpha)
    dragMove(25); // over Alpha

    expect(component.dropIndicator()).toBeNull();
    component.onDragEnded();
    expect(moved).not.toHaveBeenCalled();
  });

  it('only ever emits inside or root drop zones across many pointer positions', () => {
    expandAlpha(); // rows: Alpha, Child, Grandchild, Beta
    mockRowRect('[data-node-id="a"]', 0, 50);
    mockRowRect('[data-node-id="c"]', 100, 150);
    mockRowRect('[data-node-id="d"]', 200, 250);
    mockRowRect('[data-node-id="b"]', 300, 350);

    const zones = new Set<string>();

    dragNode(3); // Beta
    for (const y of [10, 120, 220]) {
      dragMove(y);
      const indicator = component.dropIndicator();
      if (indicator) zones.add(indicator.zone);
    }
    component.onDragEnded();

    dragNode(1); // Child
    dragMove(400); // below all rows -> root zone
    const rootIndicator = component.dropIndicator();
    if (rootIndicator) zones.add(rootIndicator.zone);

    expect(zones.size).toBeGreaterThan(0);
    expect([...zones].every((z) => z === 'inside')).toBe(true);
  });

  it('keyboard Enter still selects the node', () => {
    const selected = vi.fn();
    component.nodeSelected.subscribe(selected);

    const aRow = fixture.nativeElement.querySelector('[data-node-id="a"]') as HTMLElement;
    aRow.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));

    expect(selected).toHaveBeenCalledWith(expect.objectContaining({ id: 'a' }));
  });

  describe('while a row is being renamed', () => {
    let input: HTMLInputElement;
    let selected: ReturnType<typeof vi.fn<(node: unknown) => void>>;
    let saved: ReturnType<typeof vi.fn<(rename: unknown) => void>>;

    beforeEach(() => {
      selected = vi.fn<(node: unknown) => void>();
      saved = vi.fn<(rename: unknown) => void>();
      component.nodeSelected.subscribe((node) => selected(node));
      component.nodeRenameSaved.subscribe((rename) => saved(rename));
      fixture.componentRef.setInput('editingId', 'a');
      fixture.detectChanges();
      input = fixture.nativeElement.querySelector('.inline-rename-input');
    });

    const press = (key: string) => {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      input.dispatchEvent(event);
      return event;
    };

    it('lets Space type a space instead of selecting the row', () => {
      expect(press(' ').defaultPrevented).toBe(false);
      expect(selected).not.toHaveBeenCalled();
    });

    const typeName = (value: string) => {
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };

    it('saves on Enter without also selecting the row', () => {
      typeName('New name');
      press('Enter');
      expect(saved).toHaveBeenCalledWith({ id: 'a', newName: 'New name' });
      expect(selected).not.toHaveBeenCalled();
    });

    it('saves when the field is left with a changed name', () => {
      typeName('  Left behind  ');
      input.dispatchEvent(new FocusEvent('blur'));
      expect(saved).toHaveBeenCalledWith({ id: 'a', newName: 'Left behind' });
    });

    it('cancels, without saving, when the field is left unchanged or empty', () => {
      const cancelled = vi.fn();
      component.nodeRenameCancelled.subscribe(() => cancelled());
      typeName('');
      input.dispatchEvent(new FocusEvent('blur'));
      expect(saved).not.toHaveBeenCalled();
      expect(cancelled).toHaveBeenCalledTimes(1);
    });

    it('cancels on Escape', () => {
      const cancelled = vi.fn();
      component.nodeRenameCancelled.subscribe(() => cancelled());
      typeName('Changed');
      press('Escape');
      expect(cancelled).toHaveBeenCalledTimes(1);
      expect(saved).not.toHaveBeenCalled();
    });

    it('says under the field why an empty name cannot be saved', () => {
      typeName('');
      press('Enter');
      fixture.detectChanges();
      const error = fixture.nativeElement.querySelector('.inline-rename-error') as HTMLElement;
      expect(error.textContent).toBe('A name is required.');
      expect(input.getAttribute('aria-invalid')).toBe('true');
      expect(saved).not.toHaveBeenCalled();
    });

    it('refuses a sibling’s name when the owner asks for unique names', () => {
      fixture.componentRef.setInput('rejectDuplicateNames', true);
      fixture.detectChanges();
      const sibling = component.items().find((item) => item.id !== 'a' && (item.parentId ?? null) === null)!;
      typeName(sibling.name.toUpperCase());
      press('Enter');
      fixture.detectChanges();
      expect(saved).not.toHaveBeenCalled();
      expect(fixture.nativeElement.querySelector('.inline-rename-error')?.textContent).toContain('is already used here');
    });

    it('shows the owner’s refusal under the field', () => {
      fixture.componentRef.setInput('renameError', 'A collection with that name already exists here.');
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.inline-rename-error')?.textContent).toBe(
        'A collection with that name already exists here.',
      );
    });

    it('offers no Save or Cancel buttons', () => {
      const row = fixture.nativeElement.querySelector('[data-node-id="a"]') as HTMLElement;
      expect(row.querySelector('button[aria-label="Save name"]')).toBeNull();
      expect(row.querySelector('button[aria-label="Cancel rename"]')).toBeNull();
    });

    it('leaves the arrow keys to the caret', () => {
      expect(press('ArrowRight').defaultPrevented).toBe(false);
      expect(press('ArrowLeft').defaultPrevented).toBe(false);
    });
  });
});
