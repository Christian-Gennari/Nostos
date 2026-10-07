import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ViewToggleComponent, type ViewToggleOption } from './view-toggle.component';

/**
 * The host reproduces the two real call sites: the Library's `control-group` and
 * the Brain's `control-group view-mode-control`, both of which carry the class
 * hooks their own stylesheets and specs select on.
 *
 * `value` is a signal owned by the host, because the component is CONTROLLED —
 * asserting on the emitted value alone would not catch a component that also
 * mutated its own selection.
 */
@Component({
  standalone: true,
  imports: [ViewToggleComponent],
  template: `
    <nostos-view-toggle
      class="control-group"
      aria-label="Book view"
      [options]="libraryOptions"
      [value]="libraryView()"
      (valueChange)="libraryView.set($event)"
    />
    <nostos-view-toggle
      class="control-group view-mode-control"
      aria-label="Brain view"
      [options]="brainOptions"
      [value]="brainView()"
      (valueChange)="brainView.set($event)"
    />
  `,
})
class HostComponent {
  readonly libraryOptions = [
    { value: 'list', icon: 'list-bullets', label: 'List view' },
    { value: 'grid', icon: 'squares-four', label: 'Grid view' },
  ] satisfies readonly ViewToggleOption[];

  readonly brainOptions = [
    { value: 'list', icon: 'list-bullets', label: 'Topic view' },
    { value: 'map', icon: 'map-trifold', label: 'Map view' },
  ] satisfies readonly ViewToggleOption[];

  readonly libraryView = signal('grid');
  readonly brainView = signal('list');
}

function host() {
  const f = TestBed.createComponent(HostComponent);
  return f;
}

function toggles(f: ReturnType<typeof host>, index = 0): HTMLButtonElement[] {
  const groups = f.nativeElement.querySelectorAll('nostos-view-toggle');
  return Array.from(groups[index].querySelectorAll('.vt-opt')) as HTMLButtonElement[];
}

describe('ViewToggleComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [HostComponent] }));

  it('renders one pressed-state button per option, inside a labelled group', async () => {
    const f = host();
    await f.whenStable();

    const group = f.nativeElement.querySelector('.control-group') as HTMLElement;
    // The group carries the host classes the surfaces already select on, and the
    // label comes from the call site, not from the component.
    expect(group.getAttribute('role')).toBe('group');
    expect(group.getAttribute('aria-label')).toBe('Book view');
    expect(group.classList.contains('control-group')).toBe(true);

    const opts = toggles(f);
    expect(opts.length).toBe(2);
    expect(opts[0].getAttribute('aria-label')).toBe('List view');
    expect(opts[1].getAttribute('aria-label')).toBe('Grid view');
    // Exactly one option reports itself pressed, and it tracks the bound value.
    expect(opts[0].getAttribute('aria-pressed')).toBe('false');
    expect(opts[1].getAttribute('aria-pressed')).toBe('true');
  });

  it('emits the chosen value and lets the surface own the selection', async () => {
    const f = host();
    await f.whenStable();

    toggles(f)[0].click();
    f.detectChanges();

    expect(f.componentInstance.libraryView()).toBe('list');
    expect(toggles(f)[0].getAttribute('aria-pressed')).toBe('true');
    expect(toggles(f)[1].getAttribute('aria-pressed')).toBe('false');
  });

  it('does not emit when the already selected option is clicked', async () => {
    const f = host();
    await f.whenStable();
    const emitted: string[] = [];
    f.componentInstance.libraryView.set('grid');

    const group = f.debugElement.children[0].componentInstance as ViewToggleComponent;
    group.valueChange.subscribe((value) => emitted.push(value));

    toggles(f)[1].click();
    f.detectChanges();

    expect(emitted).toEqual([]);
  });

  it('drives the sliding thumb from --vt-index rather than a measured width', async () => {
    const f = host();
    await f.whenStable();

    // The thumb is the host's ::before, and its POSITION is a computed-style fact:
    // the runner here has no stylesheet resolution, so getComputedStyle on a
    // pseudo-element answers nothing. What this asserts is the INPUT the CSS reads
    // (--vt-index, the option ordinal), which is the part that can be wrong in TS.
    const group = f.nativeElement.querySelector('nostos-view-toggle') as HTMLElement;
    expect(group.style.getPropertyValue('--vt-index')).toBe('1');

    toggles(f)[0].click();
    f.detectChanges();

    expect(group.style.getPropertyValue('--vt-index')).toBe('0');
  });

  it('leaves the track with exactly its two buttons as element children', async () => {
    const f = host();
    await f.whenStable();

    // Guard for a real regression: the thumb was a <span> child for one revision,
    // and that broke `.vt-opt:first-child` — a structural selector the Brain's own
    // specs use. Nothing but options may live inside the track.
    const group = f.nativeElement.querySelector('nostos-view-toggle') as HTMLElement;
    const children = Array.from(group.children);
    expect(children.length).toBe(2);
    expect(children.every((child) => child.tagName === 'BUTTON')).toBe(true);
    expect(group.querySelectorAll('.vt-thumb').length).toBe(0);
  });

  it('gives the track a single tab stop and moves it with the arrow keys', async () => {
    const f = host();
    await f.whenStable();

    // Roving tabindex: the selected option is the tab stop, the other is skipped.
    const before = toggles(f);
    expect(before[1].getAttribute('tabindex')).toBe('0');
    expect(before[0].getAttribute('tabindex')).toBe('-1');

    before[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    f.detectChanges();

    const after = toggles(f);
    expect(f.componentInstance.libraryView()).toBe('list');
    expect(after[0].getAttribute('aria-pressed')).toBe('true');
    expect(after[0].getAttribute('tabindex')).toBe('0');
    // Focus followed the selection, so the next arrow key lands on the new option.
    expect(document.activeElement).toBe(after[0]);
  });

  it('wraps at the ends and supports Home and End', async () => {
    const f = host();
    await f.whenStable();

    toggles(f)[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    f.detectChanges();
    expect(f.componentInstance.libraryView()).toBe('list');

    toggles(f)[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    f.detectChanges();
    expect(f.componentInstance.libraryView()).toBe('grid');

    toggles(f)[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
    f.detectChanges();
    expect(f.componentInstance.libraryView()).toBe('list');
  });

  it('keeps one tab stop when the bound value matches no option', async () => {
    const f = host();
    await f.whenStable();

    // A stale persisted value (a view mode that no longer exists) must not make
    // the control unreachable by keyboard: the first option becomes the stop.
    f.componentInstance.libraryView.set('timeline');
    f.detectChanges();

    const opts = toggles(f);
    expect(opts[0].getAttribute('tabindex')).toBe('0');
    expect(opts[1].getAttribute('tabindex')).toBe('-1');
    expect(opts.every((o) => o.getAttribute('aria-pressed') === 'false')).toBe(true);
  });

  it('renders each option glyph through the icon registry at the measured weight step', async () => {
    const f = host();
    await f.whenStable();

    const opts = toggles(f);
    const selected = opts[1].querySelector('svg')!;
    const unselected = opts[0].querySelector('svg')!;

    // Real Phosphor assets, and the light→regular step between states.
    expect(selected.getAttribute('viewBox')).toBe('0 0 256 256');
    expect(selected.getAttribute('width')).toBe('18');
    expect(unselected.getAttribute('width')).toBe('18');
    expect(selected.innerHTML).not.toBe(unselected.innerHTML);

    // The Brain's second option is the map glyph, not the Library's grid glyph.
    const brain = toggles(f, 1);
    expect(brain[1].getAttribute('aria-label')).toBe('Map view');
    expect(brain[1].querySelector('svg')!.innerHTML).not.toBe(selected.innerHTML);
  });

  it('renders every glyph in both surfaces at one shared box', async () => {
    const f = host();
    await f.whenStable();

    // One box, everywhere: the control has no per-option size any more, so no
    // glyph can be enlarged to chase another glyph's ink. The Library's grid glyph
    // shipped at 20px for exactly that reason and it was reverted on review — at
    // the shared 18px its ink already matches the Brain's `map-trifold`.
    const boxes = [0, 1].flatMap((group) =>
      toggles(f, group).map((o) => {
        const svg = o.querySelector('svg')!;
        return `${svg.getAttribute('width')}x${svg.getAttribute('height')}`;
      })
    );
    expect(boxes).toEqual(['18x18', '18x18', '18x18', '18x18']);
  });

  it('does not carry the ordinary button recipe', async () => {
    const f = host();
    await f.whenStable();

    const opts = toggles(f);
    expect(opts.every((o) => !o.classList.contains('nostos-button'))).toBe(true);
    expect(opts.every((o) => !o.hasAttribute('ng-reflect-icon'))).toBe(true);
  });
});
