import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { IconButtonComponent } from './icon-button.component';

/**
 * The host template exercises the ATTRIBUTE SELECTOR, which is the whole point of
 * this component: the usage site is a plain `<button>` with extra attributes, and
 * natives like `disabled` must keep working with no forwarding code.
 *
 * The glyph is now a Nostos NAME (a string), not an icon data object from the old
 * library — that is the whole point of the migration: the call site does not know
 * which icon family is behind it.
 */
@Component({
  standalone: true,
  imports: [IconButtonComponent],
  template: `
    <button appIconButton icon="pencil-simple" aria-label="Edit book" (click)="clicks = clicks + 1"></button>
    <button appIconButton icon="trash" size="xs" tone="danger" aria-label="Delete"></button>
    <button appIconButton icon="x" size="xxs" aria-label="Jump"></button>
    <button appIconButton icon="pencil-simple" [disabled]="disabled()" (click)="clicks = clicks + 1"></button>
    <button appIconButton icon="pencil-simple" [class.active]="true" aria-label="TOC"></button>
    <button appIconButton icon="pencil-simple" [pressed]="pressed()"></button>
    <button appIconButton icon="pencil-simple" class="desktop-only zen-toggle" aria-label="Extra"></button>
    <button appIconButton icon="pencil-simple" [class.overflow-toggle]="toggleClass()" aria-label="Cond"></button>
    <button appIconButton icon="brain" weight="light" aria-label="Light glyph"></button>
  `,
})
class HostComponent {
  clicks = 0;
  /**
   * Signals, not plain fields: the component uses
   * ChangeDetectionStrategy.OnPush, so mutating a plain property would not
   * re-render and the test would fail for a reason unrelated to the component.
   * Using signals here also matches how every real call site feeds these inputs.
   */
  readonly disabled = signal(false);
  readonly pressed = signal<boolean | null>(null);
  readonly toggleClass = signal(true);
}

function buttons(f: ReturnType<typeof TestBed.createComponent<HostComponent>>) {
  return Array.from(f.nativeElement.querySelectorAll('button')) as HTMLButtonElement[];
}

describe('IconButtonComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [HostComponent] }));

  it('renders the host as the native <button>, not a wrapper element', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const all = buttons(f);
    expect(all.length).toBe(9);
    // Every host is a real button; there is no custom element in between.
    expect(f.nativeElement.querySelector('app-icon-button')).toBeNull();
  });

  it('keeps the icon-btn class on the host so existing CSS and specs still match', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    for (const b of buttons(f)) expect(b.classList.contains('icon-btn')).toBe(true);
  });

  it('renders the Phosphor glyph inside the button', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const glyph = buttons(f)[0].querySelector('nostos-icon');
    expect(glyph).toBeTruthy();
    // A real svg with Phosphor's 256-unit grid, not just an empty host.
    const svg = glyph!.querySelector('svg');
    expect(svg?.getAttribute('viewBox')).toBe('0 0 256 256');
    // No trace of the previous icon library anywhere in the DOM.
    expect(f.nativeElement.querySelector('lucide-icon')).toBeNull();
  });

  it('sizes the glyph independently of the button box', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    // Default glyph is 16px inside a 32px box: the two rungs stay separate.
    const svg = buttons(f)[0].querySelector('svg');
    expect(svg?.getAttribute('width')).toBe('16');
    expect(svg?.getAttribute('height')).toBe('16');
  });

  it('renders the requested weight rather than always the regular glyph', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const light = buttons(f)[8].querySelector('svg')!.innerHTML;
    // The light weight is a DIFFERENT drawing, not a stroke tweak.
    expect(light).not.toBe(buttons(f)[7].querySelector('svg')!.innerHTML);
  });

  it('puts aria-label on the host, and omits it when empty', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const all = buttons(f);
    expect(all[0].getAttribute('aria-label')).toBe('Edit book');
    expect(all[3].getAttribute('aria-label')).toBeNull();
    // The bounds moved because a conditional-class button was appended; index 3
    // is still the disabled one, which carries no label.
    expect(all[2].getAttribute('aria-label')).toBe('Jump');
  });

  it('keeps the button glyph decorative so the button label is the only announcement', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const glyph = buttons(f)[0].querySelector('nostos-icon')!;
    expect(glyph.getAttribute('aria-hidden')).toBe('true');
    expect(glyph.getAttribute('role')).toBeNull();
  });

  it('applies the measured size rungs as host classes', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const all = buttons(f);
    expect(all[1].classList.contains('icon-btn--xs')).toBe(true);
    expect(all[2].classList.contains('icon-btn--xxs')).toBe(true);
    // md is the default and needs no class: the base rule already is 32px.
    expect(all[0].classList.contains('icon-btn--md')).toBe(false);
  });

  it('applies the danger tone as a host class', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    expect(buttons(f)[1].classList.contains('icon-btn--danger')).toBe(true);
    expect(buttons(f)[0].classList.contains('icon-btn--danger')).toBe(false);
  });

  it('keeps native disabled behaviour with no forwarding code', async () => {
    const f = TestBed.createComponent(HostComponent);
    f.componentInstance.disabled.set(true);
    await f.whenStable();
    const b = buttons(f)[3];
    expect(b.disabled).toBe(true);
    b.click();
    expect(f.componentInstance.clicks).toBe(0); // a disabled button does not fire
  });

  it('lets native click pass through', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    buttons(f)[0].click();
    expect(f.componentInstance.clicks).toBe(1);
  });

  it('keeps a native [class.active] from the call site (the surface styles it)', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    expect(buttons(f)[4].classList.contains('active')).toBe(true);
  });

  it('omits aria-pressed entirely by default, and emits it only when set', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    // Default null: an action button must NOT claim to be a toggle.
    expect(buttons(f)[5].getAttribute('aria-pressed')).toBeNull();
    f.componentInstance.pressed.set(true);
    await f.whenStable();
    expect(buttons(f)[5].getAttribute('aria-pressed')).toBe('true');
  });

  it('keeps a conditional [class.x] binding from the call site', async () => {
    // The reader and studio drive their segments with [class.active] and
    // [class.overflow-toggle]; a class host binding that clobbered those would
    // silently break the toggle visuals.
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const b = buttons(f)[7];
    expect(b.classList.contains('overflow-toggle')).toBe(true);
    expect(b.classList.contains('icon-btn')).toBe(true);
  });

  it('does not require an ariaLabel input - native aria-label passes through', async () => {
    // Regression guard: a `[attr.aria-label]` host binding overrides a static
    // aria-label with null, which silently removed the accessible name from
    // Library's edit/delete buttons. The input is gone for that reason.
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    expect(buttons(f)[0].getAttribute('aria-label')).toBe('Edit book');
    expect(buttons(f)[6].getAttribute('aria-label')).toBe('Extra');
  });

  it('leaves one-off utility classes untouched on the host', async () => {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    const b = buttons(f)[6];
    expect(b.classList.contains('desktop-only')).toBe(true);
    expect(b.classList.contains('zen-toggle')).toBe(true);
    expect(b.classList.contains('icon-btn')).toBe(true);
  });

  it('puts the overlay surface on the host only when asked, with the pressed state as aria', async () => {
    @Component({
      standalone: true,
      imports: [IconButtonComponent],
      template: `
        <button appIconButton icon="heart" surface="overlay" [pressed]="true" aria-label="Favorite"></button>
        <button appIconButton icon="trash" aria-label="Delete"></button>
      `,
    })
    class OverlayHost {}
    const f = TestBed.createComponent(OverlayHost);
    await f.whenStable();
    const [overlay, plain] = Array.from(f.nativeElement.querySelectorAll('button')) as HTMLButtonElement[];
    expect(overlay.classList.contains('icon-btn--overlay')).toBe(true);
    expect(overlay.getAttribute('aria-pressed')).toBe('true');
    expect(plain.classList.contains('icon-btn--overlay')).toBe(false);
  });
});
