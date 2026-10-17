import { AfterViewInit, Component, ElementRef, inject, output } from '@angular/core';

import { DropdownComponent, DropdownOption } from '../../ui/dropdown/dropdown.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { SwitchComponent } from '../../ui/switch/switch.component';
import {
  FONT_SIZE_RANGE,
  LINE_SPACING_RANGE,
  STUDIO_FONTS,
  STUDIO_THEMES,
  StudioPreferencesService,
  StudioTheme,
  STANDARD_TEXT_WIDTH,
  TEXT_WIDTH_RANGE,
} from './studio-preferences.service';

/**
 * The Studio's page settings (issue #1041): a panel beside the page that
 * changes it live. It does not dim or blur the page, so a new face or measure
 * can be judged on the writing itself.
 */
@Component({
  selector: 'app-studio-page-settings',
  standalone: true,
  imports: [DropdownComponent, IconButtonComponent, SwitchComponent],
  templateUrl: './studio-page-settings.component.html',
  styleUrl: './studio-page-settings.component.css',
})
export class StudioPageSettingsComponent implements AfterViewInit {
  readonly prefs = inject(StudioPreferencesService);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly closed = output<void>();

  readonly themes = STUDIO_THEMES;
  readonly fontOptions: readonly DropdownOption[] = STUDIO_FONTS.map((f) => ({
    value: f.value,
    label: `${f.label} · ${f.group.toLowerCase()}`,
  }));
  readonly fontSize = FONT_SIZE_RANGE;
  readonly lineSpacing = LINE_SPACING_RANGE;
  readonly textWidth = TEXT_WIDTH_RANGE;

  /** Focus moves into the panel, onto the theme now chosen. */
  ngAfterViewInit(): void {
    queueMicrotask(() =>
      (this.host.nativeElement.querySelector('.ps-theme.active') as HTMLElement | null)?.focus(),
    );
  }

  setTheme(theme: StudioTheme): void {
    this.prefs.update({ theme });
  }

  /** Arrow keys move between swatches, as in any radio group. */
  themeKey(event: KeyboardEvent, index: number): void {
    const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const next = (index + step + this.themes.length) % this.themes.length;
    this.setTheme(this.themes[next].value);
    queueMicrotask(() =>
      (this.host.nativeElement.querySelector(`[data-theme-option="${this.themes[next].value}"]`) as HTMLElement | null)?.focus(),
    );
  }

  numberFrom(event: Event): number {
    return Number((event.target as HTMLInputElement).value);
  }

  checkedFrom(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  readonly standardWidth = STANDARD_TEXT_WIDTH;

  textWidthLabel(): string {
    const width = this.prefs.preferences().textWidth;
    return width === null ? 'Standard' : `${width} characters`;
  }
}
