import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';

import { ToastService } from '../../core/services/toast.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { DialogActionsComponent } from '../../ui/dialog-actions/dialog-actions.component';
import { DropdownComponent, type DropdownOption } from '../../ui/dropdown/dropdown.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { ModalShell } from '../../ui/modal-shell/modal-shell.component';
import { NoticeComponent } from '../../ui/notice/notice.component';
import { SegmentedComponent, type SegmentedOption } from '../../ui/segmented/segmented.component';
import { CITATION_STYLES, citationStyle } from '../citations/citation-styles';
import { STUDIO_FONTS, StudioPreferencesService } from '../page-settings/studio-preferences.service';
import { exportFilename } from './export-filename';
import { EXPORT_FORMATS } from './export-formats';
import type { ExportDocument, PageSetup, PaperSize } from './export-model';
import { type ExportTreeItem, ancestorFolders } from './export-order';
import { EXPORT_LINE_SPACINGS, EXPORT_MARGINS, pageSetupFromStudio } from './export-page-setup';
import { type ExportSource, ExportService, exportBlockedReason, orderChapters } from './export.service';

/**
 * The export dialog (issue #1164), opened from the Studio's one "Export…"
 * menu item. It exports the open writing or a folder it sits in, as one
 * document: format, page setup (saved with that writing or folder when the
 * file is made), chapter order for a folder (for this export only), the file
 * name, and what the writer should know before downloading.
 */
@Component({
  selector: 'app-export-dialog',
  standalone: true,
  imports: [
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
    IconButtonComponent,
    NostosIconComponent,
    DropdownComponent,
    SegmentedComponent,
    InputDirective,
    NoticeComponent,
    CdkDropList,
    CdkDrag,
    CdkDragHandle,
  ],
  templateUrl: './export-dialog.component.html',
  styleUrl: './export-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExportDialogComponent {
  private readonly exporter = inject(ExportService);
  private readonly prefs = inject(StudioPreferencesService);
  private readonly toast = inject(ToastService);

  /** The Studio's tree. */
  readonly items = input.required<readonly ExportTreeItem[]>();
  /** The open writing. */
  readonly documentId = input.required<string>();
  /** The open writing's Markdown as it is now, ahead of its autosave. */
  readonly currentMarkdown = input<string | null>(null);
  readonly closed = output<void>();

  readonly formats = EXPORT_FORMATS;
  readonly formatOptions: DropdownOption[] = EXPORT_FORMATS.map((f) => ({ value: f.id, label: f.label }));
  readonly paperOptions: SegmentedOption<PaperSize>[] = [
    { value: 'a4', label: 'A4' },
    { value: 'letter', label: 'Letter' },
  ];
  readonly marginOptions: SegmentedOption<number>[] = EXPORT_MARGINS.map((m) => ({ value: m.value, label: m.label }));
  readonly spacingOptions: SegmentedOption<number>[] = EXPORT_LINE_SPACINGS.map((s) => ({ value: s.value, label: s.label }));
  readonly fontOptions: DropdownOption[] = STUDIO_FONTS.map((f) => ({ value: f.value, label: f.label }));
  readonly styleOptions: DropdownOption[] = CITATION_STYLES.map((s) => ({ value: s.id, label: s.label }));

  /** The open writing, then each folder it sits in, nearest first. */
  readonly targets = computed(() => {
    const items = this.items();
    const doc = items.find((item) => item.id === this.documentId());
    return [...(doc ? [doc] : []), ...ancestorFolders(items, this.documentId())];
  });
  readonly targetOptions = computed<DropdownOption[]>(() =>
    this.targets().map((item) => ({
      value: item.id,
      label: item.type === 'Folder' ? `Folder: ${item.name}` : `This writing: ${item.name}`,
      icon: item.type === 'Folder' ? 'folder' : 'file-text',
    })),
  );
  readonly targetId = signal<string>('');

  readonly formatId = signal(EXPORT_FORMATS[0].id);
  readonly setup = signal<PageSetup>(pageSetupFromStudio(this.prefs.preferences(), navigator.language));
  /** Only the style changes the words; paper, margins and the rest are laid out by the format. */
  private readonly styleId = computed(() => this.setup().citationStyle);
  readonly source = signal<ExportSource | null>(null);
  readonly loadError = signal<string | null>(null);
  /** This export's chapter order (a folder); null is tree order. */
  readonly order = signal<string[] | null>(null);
  readonly doc = signal<ExportDocument | null>(null);
  readonly resolving = signal(false);
  readonly resolveError = signal<string | null>(null);
  readonly downloading = signal(false);
  private readonly filenameEdited = signal(false);
  readonly filenameBase = signal('');

  readonly blockedReason = computed(() => {
    const source = this.source();
    return source ? exportBlockedReason(source) : null;
  });
  readonly chapters = computed(() => {
    const source = this.source();
    if (!source || source.type !== 'Folder') return [];
    return orderChapters(source.chapters, this.order()).filter((chapter) => /\S/.test(chapter.markdown));
  });
  readonly reordered = computed(() => this.order() !== null);
  readonly format = computed(() => this.formats.find((f) => f.id === this.formatId()) ?? this.formats[0]);
  readonly filename = computed(() => exportFilename(this.filenameBase(), this.format().extension));
  readonly canDownload = computed(() => !!this.doc() && !this.resolving() && !this.blockedReason() && !this.downloading());
  readonly downloadReason = computed(() => {
    if (this.loadError()) return this.loadError();
    if (!this.source()) return 'Gathering the writing…';
    if (this.blockedReason()) return this.blockedReason();
    if (this.resolveError()) return this.resolveError();
    if (this.resolving() || !this.doc()) return 'Writing the citations…';
    return null;
  });

  private loadRun = 0;
  private resolveAbort: AbortController | null = null;
  private resolveTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.resolveAbort?.abort();
      if (this.resolveTimer) clearTimeout(this.resolveTimer);
    });

    // Opens on the writing itself.
    effect(() => {
      const id = this.documentId();
      untracked(() => this.targetId.set(id));
    });

    // A target: its writings, its saved page setup, its title as the file name.
    effect(() => {
      const id = this.targetId();
      if (!id) return;
      untracked(() => void this.loadTarget(id));
    });

    // The citations follow the style and the chapter order; nothing else changes the words.
    effect(() => {
      const source = this.source();
      const style = this.styleId();
      const order = this.order();
      untracked(() => this.scheduleResolve(source, style, order));
    });
  }

  private async loadTarget(id: string): Promise<void> {
    const run = ++this.loadRun;
    this.source.set(null);
    this.doc.set(null);
    this.order.set(null);
    this.loadError.set(null);
    const target = this.items().find((item) => item.id === id);
    if (!this.filenameEdited()) this.filenameBase.set(target?.name ?? '');
    const studio = pageSetupFromStudio(this.prefs.preferences(), navigator.language);
    const overrides = new Map<string, string>();
    const current = this.currentMarkdown();
    if (current !== null) overrides.set(this.documentId(), current);
    try {
      const [saved, source] = await Promise.all([this.exporter.savedSetup(id, studio), this.exporter.load(this.items(), id, overrides)]);
      if (run !== this.loadRun) return;
      this.setup.set(saved ?? studio);
      this.source.set(source);
    } catch {
      if (run !== this.loadRun) return;
      this.loadError.set('The writing could not be loaded. Check your connection and try again.');
    }
  }

  private scheduleResolve(source: ExportSource | null, style: string, order: string[] | null): void {
    this.resolveAbort?.abort();
    if (this.resolveTimer) clearTimeout(this.resolveTimer);
    this.resolveError.set(null);
    if (!source || exportBlockedReason(source)) {
      this.resolving.set(false);
      this.doc.set(null);
      return;
    }
    this.resolving.set(true);
    this.resolveTimer = setTimeout(() => {
      this.resolveTimer = null;
      const abort = new AbortController();
      this.resolveAbort = abort;
      this.exporter
        .resolve(source, { ...this.setup(), citationStyle: citationStyle(style).id }, order, abort.signal)
        .then((doc) => {
          if (abort.signal.aborted) return;
          this.doc.set(doc);
          this.resolving.set(false);
        })
        .catch((error: unknown) => {
          if (abort.signal.aborted) return;
          console.error('Export could not be resolved', error);
          this.resolveError.set('The citations could not be written. Try again, or choose another style.');
          this.resolving.set(false);
        });
    }, 150);
  }

  chooseTarget(id: string): void {
    if (id !== this.targetId()) this.targetId.set(id);
  }

  updateSetup(change: Partial<PageSetup>): void {
    this.setup.update((setup) => ({ ...setup, ...change }));
  }

  setCitationStyle(id: string): void {
    this.updateSetup({ citationStyle: citationStyle(id).id });
  }

  setFilename(value: string): void {
    this.filenameEdited.set(true);
    this.filenameBase.set(value);
  }

  dropChapter(event: CdkDragDrop<unknown>): void {
    this.moveChapter(event.previousIndex, event.currentIndex);
  }

  moveChapter(from: number, to: number): void {
    const ids = this.chapters().map((chapter) => chapter.id);
    if (to < 0 || to >= ids.length || from === to) return;
    moveItemInArray(ids, from, to);
    this.order.set(ids);
  }

  resetOrder(): void {
    this.order.set(null);
  }

  warningCount = computed(() => this.doc()?.warnings.length ?? 0);

  async download(): Promise<void> {
    const doc = this.doc();
    if (!doc || !this.canDownload()) return;
    this.downloading.set(true);
    const setup = this.setup();
    try {
      await this.exporter.download({ ...doc, setup }, this.format(), this.filename());
      // Remembered for the next export of the same writing or folder.
      this.exporter.saveSetup(this.targetId(), setup).catch(() => this.toast.show('The page setup could not be saved for next time.', 'error'));
      this.closed.emit();
    } catch (error) {
      console.error('Export failed', error);
      this.toast.show('The file could not be made. Try again.', 'error');
    } finally {
      this.downloading.set(false);
    }
  }

  cancel(): void {
    if (!this.downloading()) this.closed.emit();
  }
}
