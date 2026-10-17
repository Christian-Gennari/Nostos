import { AfterViewInit, Component, ElementRef, OnDestroy, computed, inject, input, output, signal } from '@angular/core';

import { ButtonComponent } from '../../ui/button/button.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { SegmentedComponent, SegmentedOption } from '../../ui/segmented/segmented.component';
import { SwitchComponent } from '../../ui/switch/switch.component';
import {
  FONT_SIZE_RANGE,
  LINE_SPACING_PRESETS,
  STUDIO_FONTS,
  STUDIO_THEMES,
  StudioFont,
  StudioPreferencesService,
  StudioThemeOption,
  TEXT_WIDTH_PRESETS,
  nearestLineSpacing,
  nearestTextWidth,
} from './studio-preferences.service';

/** The segmented control needs a value per option; null is the standard measure. */
const STANDARD_WIDTH_VALUE = 'standard';

/**
 * The Studio's page settings (issues #1041, #1060): a small popover under the
 * Aa button (a bottom sheet on a phone). It does not dim or cover the page,
 * so a theme, face or measure is judged on the writing itself.
 */
@Component({
  selector: 'app-studio-page-settings',
  standalone: true,
  imports: [ButtonComponent, IconButtonComponent, SegmentedComponent, SwitchComponent],
  templateUrl: './studio-page-settings.component.html',
  styleUrl: './studio-page-settings.component.css',
  host: { '[style.--ps-top.px]': 'top()' },
})
export class StudioPageSettingsComponent implements AfterViewInit, OnDestroy {
  readonly prefs = inject(StudioPreferencesService);
  private readonly host = inject(ElementRef<HTMLElement>);

  /** The Aa button: the popover hangs under it, and a click on it is its own toggle. */
  readonly anchor = input<HTMLElement | null>(null);
  readonly closed = output<void>();

  readonly view = signal<'main' | 'fonts'>('main');

  readonly themes = STUDIO_THEMES;
  readonly fonts = STUDIO_FONTS;
  readonly fontGroups = [...new Set(STUDIO_FONTS.map((f) => f.group))];
  readonly sizeRange = FONT_SIZE_RANGE;

  readonly following = computed(() => this.prefs.theme() === 'match');
  readonly currentFont = computed(
    () => STUDIO_FONTS.find((f) => f.value === this.prefs.preferences().font) ?? STUDIO_FONTS[3],
  );

  readonly spacingOptions: readonly SegmentedOption<number>[] = LINE_SPACING_PRESETS.map((p) => ({ value: p.value, label: p.label }));
  readonly spacing = computed(() => nearestLineSpacing(this.prefs.preferences().lineSpacing));

  readonly widthOptions: readonly SegmentedOption<string>[] = TEXT_WIDTH_PRESETS.map((p) => ({
    value: p.value === null ? STANDARD_WIDTH_VALUE : String(p.value),
    label: p.label,
  }));
  readonly width = computed(() => {
    const nearest = nearestTextWidth(this.prefs.preferences().textWidth);
    return nearest === null ? STANDARD_WIDTH_VALUE : String(nearest);
  });

  /** Under the anchor; the stylesheet's own fallback when there is none. */
  readonly top = computed(() => {
    const rect = this.anchor()?.getBoundingClientRect();
    return rect ? Math.round(rect.bottom + 6) : null;
  });

  /** A press outside the popover (and outside its own button) closes it. */
  private readonly onPointerDown = (event: PointerEvent) => {
    const target = event.target as Node | null;
    if (!target || this.host.nativeElement.contains(target) || this.anchor()?.contains(target)) return;
    // A dropdown or menu the panel opened lives in an overlay outside it.
    if ((target as Element).closest?.('.cdk-overlay-container')) return;
    this.closed.emit();
  };

  ngAfterViewInit(): void {
    document.addEventListener('pointerdown', this.onPointerDown, true);
    // Focus moves into the popover, onto the theme now chosen (or the first).
    queueMicrotask(() => this.focusTheme());
  }

  ngOnDestroy(): void {
    document.removeEventListener('pointerdown', this.onPointerDown, true);
  }

  setTheme(theme: StudioThemeOption['value']): void {
    this.prefs.update({ theme });
  }

  /** Following the app; leaving it keeps the look the page has now. */
  setFollow(follow: boolean): void {
    if (follow) {
      this.prefs.update({ theme: 'match' });
      return;
    }
    const dark = document.documentElement.getAttribute('data-theme') === 'dark';
    this.prefs.update({ theme: dark ? 'mist' : 'paper' });
  }

  /** Arrow keys move between swatches, as in any radio group. */
  themeKey(event: KeyboardEvent, index: number): void {
    const step =
      event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const next = this.themes[(index + step + this.themes.length) % this.themes.length];
    this.setTheme(next.value);
    queueMicrotask(() => this.focusTheme());
  }

  stepSize(delta: number): void {
    this.prefs.update({ fontSize: this.prefs.preferences().fontSize + delta });
  }

  setWidth(value: string): void {
    this.prefs.update({ textWidth: value === STANDARD_WIDTH_VALUE ? null : Number(value) });
  }

  openFonts(): void {
    this.view.set('fonts');
    queueMicrotask(() => (this.host.nativeElement.querySelector('.ps-font[aria-checked="true"]') as HTMLElement | null)?.focus());
  }

  pickFont(font: StudioFont): void {
    this.prefs.update({ font: font.value });
    this.backToMain();
  }

  backToMain(): void {
    this.view.set('main');
    queueMicrotask(() => (this.host.nativeElement.querySelector('.ps-typeface') as HTMLElement | null)?.focus());
  }

  fontsIn(group: StudioFont['group']): StudioFont[] {
    return STUDIO_FONTS.filter((f) => f.group === group);
  }

  checkedFrom(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  numberFrom(event: Event): number {
    return Number((event.target as HTMLInputElement).value);
  }

  private focusTheme(): void {
    const root = this.host.nativeElement;
    ((root.querySelector('.ps-theme[aria-checked="true"]') ?? root.querySelector('.ps-theme')) as HTMLElement | null)?.focus();
  }
}
