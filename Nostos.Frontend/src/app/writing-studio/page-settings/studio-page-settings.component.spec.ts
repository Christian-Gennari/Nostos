import { ComponentFixture, TestBed } from '@angular/core/testing';

import { StudioPageSettingsComponent } from './studio-page-settings.component';
import { DEFAULT_STUDIO_PREFERENCES, StudioPreferencesService } from './studio-preferences.service';

describe('StudioPageSettingsComponent (#1060)', () => {
  let fixture: ComponentFixture<StudioPageSettingsComponent>;
  let root: HTMLElement;
  let prefs: StudioPreferencesService;
  let closed: number;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({ imports: [StudioPageSettingsComponent] }).compileComponents();
    prefs = TestBed.inject(StudioPreferencesService);
    prefs.reset();
    fixture = TestBed.createComponent(StudioPageSettingsComponent);
    closed = 0;
    fixture.componentInstance.closed.subscribe(() => closed++);
    fixture.detectChanges();
    root = fixture.nativeElement;
  });

  afterEach(() => fixture.destroy());

  const click = (selector: string) => {
    (root.querySelector(selector) as HTMLElement).click();
    fixture.detectChanges();
  };

  it('offers four themes, follows the app by default and leaves it on a pick', () => {
    const options = [...root.querySelectorAll('.ps-theme')].map((el) => el.getAttribute('data-theme-option'));
    expect(options).toEqual(['paper', 'sepia', 'mist', 'study']);
    const follow = root.querySelector('.ps-follow input') as HTMLInputElement;
    expect(follow.checked).toBe(true);

    click('[data-theme-option="study"]');
    expect(prefs.theme()).toBe('study');
    expect((root.querySelector('.ps-follow input') as HTMLInputElement).checked).toBe(false);

    click('.ps-follow input');
    expect(prefs.theme()).toBe('match');
  });

  it('leaving Follow app keeps a page of the same lightness', () => {
    document.documentElement.setAttribute('data-theme', 'dark');
    try {
      click('.ps-follow input');
      expect(prefs.theme()).toBe('mist');
    } finally {
      document.documentElement.removeAttribute('data-theme');
    }
  });

  it('steps the size within its range', () => {
    const [smaller, larger] = [...root.querySelectorAll('.ps-step')] as HTMLButtonElement[];
    larger.click();
    larger.click();
    fixture.detectChanges();
    expect(prefs.preferences().fontSize).toBe(DEFAULT_STUDIO_PREFERENCES.fontSize + 2);
    prefs.update({ fontSize: 14 });
    fixture.detectChanges();
    expect(smaller.disabled).toBe(true);
  });

  it('sets spacing and line length from the three-way choices', () => {
    const spacing = [...root.querySelectorAll('[data-testid="ps-spacing"] .seg-opt')] as HTMLButtonElement[];
    spacing[2].click();
    fixture.detectChanges();
    expect(prefs.preferences().lineSpacing).toBe(2.2);

    const width = [...root.querySelectorAll('[data-testid="ps-width"] .seg-opt')] as HTMLButtonElement[];
    width[0].click();
    fixture.detectChanges();
    expect(prefs.preferences().textWidth).toBe(66);
    width[1].click();
    fixture.detectChanges();
    expect(prefs.preferences().textWidth).toBeNull();
  });

  it('picks a typeface from a list set in each face, then comes back', () => {
    click('.ps-typeface');
    const garamond = root.querySelector('[data-font-option="eb-garamond"]') as HTMLElement;
    expect(garamond.style.fontFamily).toContain('Studio EB Garamond');
    garamond.click();
    fixture.detectChanges();
    expect(prefs.preferences().font).toBe('eb-garamond');
    expect(root.querySelector('.ps-typeface-name')?.textContent).toContain('EB Garamond');
  });

  it('shows the volume only while sounds are on', () => {
    expect(root.querySelector('.ps-volume')).toBeNull();
    prefs.update({ sounds: true });
    fixture.detectChanges();
    expect(root.querySelector('.ps-volume')).toBeTruthy();
  });

  it('closes on a press outside it, but not inside', () => {
    root.querySelector('.ps-theme')!.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(closed).toBe(0);
    document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(closed).toBe(1);
  });
});
