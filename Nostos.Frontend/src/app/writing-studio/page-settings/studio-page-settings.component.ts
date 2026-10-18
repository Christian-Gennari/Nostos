import {
  AfterViewInit,
  Component,
  ElementRef,
  Injector,
  OnDestroy,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import { ThemeService } from '../../core/services/theme.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { SegmentedComponent, SegmentedOption } from '../../ui/segmented/segmented.component';
import { SwitchComponent } from '../../ui/switch/switch.component';
import { CheckboxDirective } from '../../ui/check/check.directive';
import { RangeDirective } from '../../ui/range/range.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { SoundPack, TypewriterSound, TypewriterSounds } from '../../ui/markdown-editor/typewriter-sounds';
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
  imports: [ButtonComponent, IconButtonComponent, SegmentedComponent, SwitchComponent, CheckboxDirective, RangeDirective, NostosIconComponent],
  templateUrl: './studio-page-settings.component.html',
  styleUrl: './studio-page-settings.component.css',
  host: {
    '[style.--ps-top.px]': 'top()',
    '(window:resize)': 'viewport.update((n) => n + 1)',
    '(keydown.escape)': 'escape($event)',
  },
})
export class StudioPageSettingsComponent implements AfterViewInit, OnDestroy {
  readonly prefs = inject(StudioPreferencesService);
  private readonly appTheme = inject(ThemeService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly injector = inject(Injector);

  /** The Aa button: the popover hangs under it, and a click on it is its own toggle. */
  readonly anchor = input<HTMLElement | null>(null);
  readonly closed = output<void>();

  readonly view = signal<'main' | 'fonts'>('main');

  readonly themes = STUDIO_THEMES;
  readonly fontGroups = [...new Set(STUDIO_FONTS.map((f) => f.group))].map((group) => ({
    group,
    fonts: STUDIO_FONTS.filter((f) => f.group === group),
  }));
  readonly sizeRange = FONT_SIZE_RANGE;

  readonly following = computed(() => this.prefs.theme() === 'match');

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

  readonly soundPackOptions: readonly SegmentedOption<SoundPack>[] = [
    { value: 'typewriter', label: 'Typewriter' },
    { value: 'keyboard', label: 'Keyboard' },
  ];

  /** Plays a sample from the settings themselves; made on the first preview. */
  private soundPreview: TypewriterSounds | null = null;

  /** Bumped on resize, so the popover follows its button when the header reflows. */
  readonly viewport = signal(0);

  /** Under the anchor; the stylesheet's own fallback when there is none. */
  readonly top = computed(() => {
    this.viewport();
    const rect = this.anchor()?.getBoundingClientRect();
    return rect ? Math.round(rect.bottom + 6) : null;
  });

  /** A press outside the popover (and outside its own button) closes it. */
  private readonly onPointerDown = (event: PointerEvent) => {
    const target = event.target as Node | null;
    if (!target || this.host.nativeElement.contains(target) || this.anchor()?.contains(target)) return;
    this.closed.emit();
  };

  ngAfterViewInit(): void {
    document.addEventListener('pointerdown', this.onPointerDown, true);
    // Focus moves into the popover, onto the theme now chosen (or the first).
    queueMicrotask(() => this.focusTheme());
  }

  ngOnDestroy(): void {
    document.removeEventListener('pointerdown', this.onPointerDown, true);
    this.soundPreview?.dispose();
  }

  setSoundPack(soundPack: SoundPack): void {
    this.prefs.update({ soundPack });
    this.previewSound('key');
  }

  setMarginBell(marginBell: boolean): void {
    this.prefs.update({ marginBell });
    if (marginBell) this.previewSound('bell');
  }

  /**
   * One key at the current level and pack, so the level can be set without
   * leaving the panel. Called when the slider comes to rest (release, or each
   * arrow-key step), not on every step of a drag.
   */
  previewSound(sound: TypewriterSound = 'key'): void {
    const prefs = this.prefs.preferences();
    this.soundPreview ??= new TypewriterSounds();
    this.soundPreview.setPack(prefs.soundPack);
    void this.soundPreview.preview(sound, prefs.soundVolume);
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
    const mode = this.appTheme.theme();
    this.prefs.update({ theme: this.themes.find((t) => t.mode === mode)!.value });
  }

  /** In the typeface list Esc steps back; on the main view it is the Studio's to close. */
  escape(event: Event): void {
    if (this.view() !== 'fonts') return;
    event.preventDefault();
    this.backToMain();
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

  /** The typeface list is one radio group: arrows move and pick, one tab stop. */
  fontKey(event: KeyboardEvent, font: StudioFont): void {
    const step = event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1 : event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const index = STUDIO_FONTS.indexOf(font);
    const next = STUDIO_FONTS[(index + step + STUDIO_FONTS.length) % STUDIO_FONTS.length];
    this.prefs.update({ font: next.value });
    queueMicrotask(() =>
      (this.host.nativeElement.querySelector(`[data-font-option="${next.value}"]`) as HTMLElement | null)?.focus(),
    );
  }

  openFonts(): void {
    this.view.set('fonts');
    // The button pressed is gone with the view: focus must land in the list,
    // once it exists, or Esc would reach the Studio and close everything.
    this.focusAfterRender('.ps-font[aria-checked="true"]');
  }

  pickFont(font: StudioFont): void {
    this.prefs.update({ font: font.value });
    this.backToMain();
  }

  backToMain(): void {
    this.view.set('main');
    this.focusAfterRender('.ps-typeface');
  }

  checkedFrom(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  numberFrom(event: Event): number {
    return Number((event.target as HTMLInputElement).value);
  }

  private focusAfterRender(selector: string): void {
    afterNextRender(() => (this.host.nativeElement.querySelector(selector) as HTMLElement | null)?.focus(), {
      injector: this.injector,
    });
  }

  private focusTheme(): void {
    const root = this.host.nativeElement;
    ((root.querySelector('.ps-theme[aria-checked="true"]') ?? root.querySelector('.ps-theme')) as HTMLElement | null)?.focus();
  }
}
