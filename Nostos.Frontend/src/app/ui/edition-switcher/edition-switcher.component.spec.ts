import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { EditionSwitcherComponent, type EditionSwitcherOption } from './edition-switcher.component';

function option(overrides: Partial<EditionSwitcherOption> & { id: string }): EditionSwitcherOption {
  return {
    format: 'EPUB',
    concept: 'ebook',
    detail: null,
    current: false,
    progress: 0,
    finished: false,
    description: 'EPUB, not started',
    ...overrides,
  };
}

@Component({
  standalone: true,
  imports: [EditionSwitcherComponent],
  template: `
    <nostos-edition-switcher label="Switch edition" [options]="options()" (switched)="opened.push($event)" />
  `,
})
class HostComponent {
  readonly options = signal<EditionSwitcherOption[]>([
    option({ id: 'a', format: 'Audiobook', concept: 'audiobook', progress: 37, description: 'Audiobook, 37% read' }),
    option({ id: 'e', current: true, progress: 12, detail: '2nd edition' }),
    option({ id: 'p', format: 'PDF', concept: 'pdf', finished: true, progress: 100 }),
  ]);
  readonly opened: string[] = [];
}

describe('EditionSwitcherComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [HostComponent] }));

  async function setup() {
    const f = TestBed.createComponent(HostComponent);
    await f.whenStable();
    f.detectChanges();
    const host = f.nativeElement as HTMLElement;
    const nav = () => host.querySelector('nav') as HTMLElement;
    const options = () => Array.from(host.querySelectorAll('.es-option')) as HTMLElement[];
    const text = (el: Element, selector: string) => el.querySelector(selector)?.textContent?.trim() ?? null;
    return { f, host, nav, options, text };
  }

  it('is a labelled nav of buttons with the current edition as plain text', async () => {
    const { nav, options } = await setup();

    expect(nav().getAttribute('aria-label')).toBe('Switch edition');
    expect(options().map((o) => o.tagName)).toEqual(['BUTTON', 'SPAN', 'BUTTON']);
    expect(options().map((o) => o.getAttribute('aria-current'))).toEqual([null, 'page', null]);
    // Nothing in the row uses roving tabindex: Tab walks the editions.
    expect(options().every((o) => !o.hasAttribute('tabindex'))).toBe(true);
  });

  it('marks the current edition with a check as well as its border', async () => {
    const { options } = await setup();

    expect(options()[1].querySelector('.es-check')).not.toBeNull();
    expect(options()[0].querySelector('.es-check')).toBeNull();
  });

  it('shows status for every edition, the current one included', async () => {
    const { options, text } = await setup();

    expect(options().map((o) => text(o, '.es-status'))).toEqual(['37% read', '12% read', 'Finished']);
    expect(text(options()[1], '.es-detail')).toBe('2nd edition');
  });

  it('says "Not started" rather than 0%', async () => {
    const { f, options, text } = await setup();
    f.componentInstance.options.set([option({ id: 'a', format: 'Audiobook' }), option({ id: 'e', current: true })]);
    f.detectChanges();

    expect(text(options()[0], '.es-status')).toBe('Not started');
  });

  it('emits the edition id on click and names the edition in the tooltip', async () => {
    const { f, options } = await setup();

    expect(options()[0].title).toBe('Switch to Audiobook, 37% read');
    options()[0].click();
    expect(f.componentInstance.opened).toEqual(['a']);
  });

  it('collapses past three editions into the current one plus a menu, never wrapping', async () => {
    const { f, host, options } = await setup();
    f.componentInstance.options.set([
      option({ id: 'a', format: 'Audiobook' }),
      option({ id: 'e', current: true }),
      option({ id: 'p', format: 'PDF' }),
      option({ id: 'x', format: 'Physical', detail: '1990' }),
    ]);
    f.detectChanges();

    expect(options().map((o) => o.getAttribute('aria-current'))).toEqual(['page']);
    const more = host.querySelector('.es-more') as HTMLButtonElement;
    expect(more.textContent?.trim()).toBe('3 more');
    expect(more.getAttribute('aria-label')).toBe('3 more editions');

    more.click();
    f.detectChanges();
    const items = Array.from(document.querySelectorAll('.nostos-menu-item')) as HTMLButtonElement[];
    expect(items.map((i) => i.querySelector('.nostos-menu-item__label')?.textContent?.trim()))
      .toEqual(['Audiobook', 'PDF', 'Physical · 1990']);

    items[2].click();
    expect(f.componentInstance.opened).toEqual(['x']);
  });
});
