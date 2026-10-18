import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ThemeService } from '../../core/services/theme.service';
import { TypewriterSounds } from '../../ui/markdown-editor/typewriter-sounds';
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

  it('offers the app themes, and a pick changes the app theme (#1074)', () => {
    const appTheme = TestBed.inject(ThemeService);
    const options = [...root.querySelectorAll('[data-testid="ps-theme"] .tp-swatch')].map((el) => el.getAttribute('data-theme-option'));
    expect(options).toEqual(['system', 'light', 'sepia', 'mist', 'study', 'dark']);
    try {
      click('[data-theme-option="study"]');
      expect(appTheme.choice()).toBe('study');
      expect(root.querySelector('[data-theme-option="study"]')!.getAttribute('aria-checked')).toBe('true');
    } finally {
      appTheme.setChoice('system');
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

  describe('sounds (#1065)', () => {
    let previews: string[];

    beforeEach(() => {
      previews = [];
      vi.spyOn(TypewriterSounds.prototype, 'preview').mockImplementation(function (this: TypewriterSounds, sound) {
        previews.push(sound);
        return Promise.resolve();
      });
      prefs.update({ sounds: true });
      fixture.detectChanges();
    });

    afterEach(() => vi.restoreAllMocks());

    it('plays one key when the volume slider comes to rest, not while it moves', () => {
      const slider = root.querySelector('.ps-volume') as HTMLInputElement;
      slider.value = '0.3';
      slider.dispatchEvent(new Event('input'));
      expect(previews).toEqual([]);
      slider.dispatchEvent(new Event('change'));
      expect(prefs.preferences().soundVolume).toBe(0.3);
      expect(previews).toEqual(['key']);
    });

    it('offers the typewriter and the keyboard, and lets the writer hear a pick', () => {
      const options = [...root.querySelectorAll('[data-testid="ps-sound-pack"] [role="radio"]')].map((el) => el.textContent?.trim());
      expect(options).toEqual(['Typewriter', 'Keyboard']);
      (root.querySelectorAll('[data-testid="ps-sound-pack"] [role="radio"]')[1] as HTMLElement).click();
      fixture.detectChanges();
      expect(prefs.preferences().soundPack).toBe('keyboard');
      expect(previews).toEqual(['key']);
    });
  });

  it('steps back from the typeface list with Esc, and walks it with the arrows', () => {
    click('.ps-typeface');
    const current = root.querySelector('.ps-font[tabindex="0"]') as HTMLElement;
    expect(current.getAttribute('data-font-option')).toBe('newsreader');
    current.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();
    expect(prefs.preferences().font).toBe('crimson-pro');
    const esc = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    root.querySelector('.ps-font')!.dispatchEvent(esc);
    fixture.detectChanges();
    expect(esc.defaultPrevented).toBe(true);
    expect(root.querySelector('.ps-typeface')).toBeTruthy();
  });

  it('closes on a press outside it, but not inside', () => {
    root.querySelector('.tp-swatch')!.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(closed).toBe(0);
    document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    expect(closed).toBe(1);
  });
});
