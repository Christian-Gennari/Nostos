import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { ButtonComponent } from '../ui/button/button.component';
import { SegmentedComponent } from '../ui/segmented/segmented.component';
import { SettingsPopoverComponent } from '../ui/settings-popover/settings-popover.component';
import { ThemePickerComponent } from '../ui/theme-picker/theme-picker.component';
import type { ScrollModeType } from 'ngx-extended-pdf-viewer';
import type { EpubReader } from './epub-reader/epub-reader.component';
import type { PdfReader } from './pdf-reader/pdf-reader.component';
import { EPUB_LINE_OPTIONS } from './epub-reader/epub-typography';

/**
 * The reader's view settings: the app-wide theme, and then only what the open
 * format can change — page mode, zoom, rotation and fits for a fixed-layout
 * PDF, typeface, size, line height and margins for a reflowable EPUB.
 *
 * It was a 200-line block of the shell's template, wired to whichever reader
 * happened to be mounted (issue #1607). The reader instances are inputs, so the
 * panel still reads the persisted state from the reader that owns it, while the
 * shell keeps only whether the popover is open and the Aa control it hangs from.
 */
@Component({
  selector: 'app-reader-view-settings',
  standalone: true,
  imports: [ButtonComponent, SegmentedComponent, SettingsPopoverComponent, ThemePickerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-view-settings.component.html',
  styleUrl: './reader-view-settings.component.css',
})
export class ReaderViewSettings {
  /** The open format; the panel renders nothing for audio. */
  readonly fileType = input<'pdf' | 'epub' | 'audio' | null>(null);
  /** The reflowable reader whose typography the Text group drives. */
  readonly epub = input<EpubReader | null>(null);
  /** The fixed-layout reader whose page controls the Page group drives. */
  readonly pdf = input<PdfReader | null>(null);
  /** The Aa control the popover hangs from and returns focus to. */
  readonly origin = input<HTMLElement | null>(null);
  /** The popover asked to close (Escape, a press outside, its close button). */
  readonly dismissed = output<void>();

  /** Line heights as segmented options; the label is the value at one decimal. */
  readonly lineHeightOptions = EPUB_LINE_OPTIONS.map((value) => ({ value, label: value.toFixed(1) }));

  /** The PDF page-colour choice, as the two values the segmented control emits. */
  readonly pageColourOptions = [
    { value: 'inverted', label: 'Inverted' },
    { value: 'printed', label: 'As printed' },
  ] as const;

  /** The PDF's page pixels follow the app theme it sits under. */
  readonly pageInverted = computed(() => this.pdf()?.pageInverted() ?? false);

  pdfZoomPresets(): { value: string | number; label: string }[] {
    return this.pdf()?.zoomPresets ?? [];
  }

  pdfZoomLabel(): string {
    return this.pdf()?.zoomLabel() ?? '';
  }

  /** The preset currently in effect, or null when the zoom is a free value. */
  pdfZoomPreset(): string | number | null {
    return this.pdfZoomPresets().find((preset) => this.isZoomPreset(preset.value))?.value ?? null;
  }

  pdfScrollMode(): ScrollModeType | null {
    return this.pdf()?.scrollMode() ?? null;
  }

  isZoomPreset(value: string | number): boolean {
    return this.pdf()?.isZoomPreset(value) ?? false;
  }

  setZoomPreset(value: string | number): void {
    this.pdf()?.setZoom(value);
  }

  pdfReadingModes(): { value: ScrollModeType; label: string }[] {
    return this.pdf()?.readingModes ?? [];
  }

  isScrollMode(mode: ScrollModeType): boolean {
    return this.pdf()?.isScrollMode(mode) ?? false;
  }

  setScrollMode(mode: ScrollModeType): void {
    this.pdf()?.setScrollMode(mode);
  }
}
