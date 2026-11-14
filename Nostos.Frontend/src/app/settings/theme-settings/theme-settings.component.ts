import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
} from '@angular/core';

import {
  DAY_THEMES,
  NIGHT_THEMES,
  THEMES,
  ThemeChoice,
  ThemeName,
  ThemeService,
  themeOption,
} from '../../core/services/theme.service';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { SegmentedComponent, SegmentedOption } from '../../ui/segmented/segmented.component';

/**
 * Settings → General → Colour theme (issue #1074).
 *
 * Follow system and the five themes, each card showing its theme's real paper,
 * ink and accent without wearing it. The choice belongs to this device, so the
 * card talks only to `ThemeService`.
 */
@Component({
  selector: 'app-theme-settings',
  standalone: true,
  imports: [NostosIconComponent, SegmentedComponent],
  templateUrl: './theme-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './theme-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ThemeSettingsComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly themeService = inject(ThemeService);

  readonly themeChoice = this.themeService.choice;
  readonly themeCards: readonly { value: ThemeChoice; label: string }[] = [
    { value: 'system', label: 'Follow system' },
    ...THEMES.map((t) => ({ value: t.value, label: t.label })),
  ];
  readonly dayThemeOptions: readonly SegmentedOption<ThemeName>[] = DAY_THEMES.map((t) => ({
    value: t.value,
    label: t.label,
  }));
  readonly nightThemeOptions: readonly SegmentedOption<ThemeName>[] = NIGHT_THEMES.map((t) => ({
    value: t.value,
    label: t.label,
  }));
  readonly dayTheme = this.themeService.dayTheme;
  readonly nightTheme = this.themeService.nightTheme;
  readonly dayThemeOption = computed(() => themeOption(this.dayTheme()));
  readonly nightThemeOption = computed(() => themeOption(this.nightTheme()));

  setTheme(choice: ThemeChoice): void {
    this.themeService.setChoice(choice);
  }

  setDayTheme(name: ThemeName): void {
    this.themeService.setDayTheme(name);
  }

  setNightTheme(name: ThemeName): void {
    this.themeService.setNightTheme(name);
  }

  /** A theme card's swatch colours (not for Follow system, which shows day and night). */
  swatchOf(choice: ThemeChoice) {
    return themeOption(choice as ThemeName).swatch;
  }

  /** Arrow keys move between the theme cards and pick, as in any radio group. */
  themeKey(event: KeyboardEvent, index: number): void {
    const step =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0;
    if (!step) return;
    event.preventDefault();
    const next = (index + step + this.themeCards.length) % this.themeCards.length;
    this.setTheme(this.themeCards[next].value);
    (this.host.nativeElement.querySelectorAll('.theme-card')[next] as HTMLElement | undefined)?.focus();
  }
}
