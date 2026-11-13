import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ThemeService } from '../../core/services/theme.service';
import { ThemeSettingsComponent } from './theme-settings.component';

describe('ThemeSettingsComponent', () => {
  let fixture: ComponentFixture<ThemeSettingsComponent>;
  let component: ThemeSettingsComponent;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [ThemeSettingsComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ThemeSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => localStorage.clear());

  function cards(): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.theme-card'));
  }

  it('offers Follow system and every theme as one radio group', () => {
    expect(cards().map((card) => card.dataset['themeOption'])).toEqual([
      'system',
      'light',
      'sepia',
      'mist',
      'study',
      'dark',
    ]);
    expect(cards().filter((card) => card.getAttribute('aria-checked') === 'true')).toHaveLength(1);
    expect(fixture.nativeElement.querySelector('.theme-grid')?.getAttribute('role')).toBe(
      'radiogroup',
    );
  });

  it('sets the choice through the theme service and marks the card', () => {
    const themes = TestBed.inject(ThemeService);

    cards()[2].click();
    fixture.detectChanges();

    expect(themes.choice()).toBe('sepia');
    expect(cards()[2].classList.contains('is-active')).toBe(true);
  });

  it('moves the choice and the focus with the arrow keys', () => {
    const themes = TestBed.inject(ThemeService);
    const first = cards()[0];
    first.focus();

    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();

    expect(themes.choice()).toBe('light');
    expect(document.activeElement).toBe(cards()[1]);
  });

  it('offers the day and night themes only while following the system', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="theme-slots"]')).not.toBeNull();

    component.setTheme('light');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="theme-slots"]')).toBeNull();
  });
});
