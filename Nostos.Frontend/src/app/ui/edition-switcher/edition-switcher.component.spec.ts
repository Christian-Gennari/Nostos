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
  template: `<nostos-edition-switcher [options]="options()" (switched)="opened.push($event)" />`,
})
class HostComponent {
  readonly options = signal<EditionSwitcherOption[]>([
    option({ id: 'a', format: 'Audiobook', concept: 'audiobook', progress: 37 }),
    option({ id: 'e', current: true, progress: 12, detail: '2nd edition', description: 'EPUB (2nd edition), 12% read' }),
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
    const trigger = () => host.querySelector('nav .es-trigger') as HTMLButtonElement;
    const openMenu = () => {
      trigger().click();
      f.detectChanges();
      return Array.from(document.querySelectorAll('.nostos-menu-item')) as HTMLButtonElement[];
    };
    const label = (item: Element) => item.querySelector('.nostos-menu-item__label')?.textContent?.trim();
    return { f, host, trigger, openMenu, label };
  }

  it('is a labelled nav holding one pill that names the current edition', async () => {
    const { host, trigger } = await setup();

    expect(host.querySelector('nav')?.getAttribute('aria-label')).toBe('Switch edition');
    expect(trigger().querySelector('.es-value')?.textContent?.trim()).toBe('EPUB · 2nd edition');
    expect(trigger().getAttribute('aria-label')).toBe('Edition: EPUB · 2nd edition. Switch edition');
    expect(trigger().title).toBe('EPUB (2nd edition), 12% read');
  });

  it('lists every edition with its status, the current one checked and aria-current', async () => {
    const { openMenu, label } = await setup();
    const items = openMenu();

    expect(items.map(label)).toEqual(['Audiobook', 'EPUB · 2nd edition', 'PDF']);
    expect(items.map((i) => i.querySelector('.nostos-menu-item__hint')?.textContent?.trim()))
      .toEqual(['37% read', '12% read', 'Finished']);
    expect(items.map((i) => i.getAttribute('aria-current'))).toEqual([null, 'page', null]);
  });

  it('says "Not started" rather than 0%', async () => {
    const { f, openMenu } = await setup();
    f.componentInstance.options.set([option({ id: 'a', format: 'Audiobook' }), option({ id: 'e', current: true })]);
    f.detectChanges();

    expect(openMenu()[0].querySelector('.nostos-menu-item__hint')?.textContent?.trim()).toBe('Not started');
  });

  it('emits another edition on choice and nothing for the current one', async () => {
    const { f, openMenu } = await setup();

    openMenu()[1].click();
    expect(f.componentInstance.opened).toEqual([]);

    openMenu()[2].click();
    expect(f.componentInstance.opened).toEqual(['p']);
  });

  it('stays one pill however many editions there are', async () => {
    const { f, host } = await setup();
    f.componentInstance.options.set(
      Array.from({ length: 8 }, (_, i) => option({ id: `e${i}`, current: i === 3, detail: `${i + 1}` })),
    );
    f.detectChanges();

    expect(host.querySelectorAll('button').length).toBe(1);
  });

  it('renders nothing when no option is current', async () => {
    const { f, host } = await setup();
    f.componentInstance.options.set([option({ id: 'a' })]);
    f.detectChanges();

    expect(host.querySelector('nav')).toBeNull();
  });
});
