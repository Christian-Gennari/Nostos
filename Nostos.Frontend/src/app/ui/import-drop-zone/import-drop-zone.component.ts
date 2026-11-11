/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { APPLE_BOOKS_FOLDER, HighlightImportService } from '../../core/services/highlight-import.service';
import { ReadingHistoryImportService } from '../../core/services/reading-history-import.service';
import { CalibreImportService } from '../../core/services/calibre-import.service';
import { PortableArchiveHandoffService } from '../../core/services/portable-archive-handoff.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { FeedbackLinkService } from '../../core/services/feedback-link.service';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { BadgeComponent } from '../badge/badge.component';
import { ButtonComponent } from '../button/button.component';
import { routeImportFiles, type ImportProbeFile, type ImportRoute } from './import-file-route';

type HelpId =
  | 'kobo'
  | 'koreader'
  | 'kindle'
  | 'applebooks'
  | 'readwise'
  | 'goodreads'
  | 'storygraph'
  | 'archive'
  | 'calibre';

interface HelpStep {
  readonly text: string;
  /** A file or folder the step names, shown as code. */
  readonly code?: string;
}

interface HelpEntry {
  readonly id: HelpId;
  readonly name: string;
  /** The one line that says where to find the file. */
  readonly hint: string;
  readonly steps: readonly HelpStep[];
  readonly beta?: boolean;
}

interface ImportNotice {
  readonly message: string;
  /** What the server said, when it was asked and refused the file. */
  readonly serverMessage: string | null;
}

let nextInstanceId = 0;

const HIGHLIGHT_HELP: readonly HelpEntry[] = [
  {
    id: 'kobo',
    name: 'Kobo',
    hint: 'The hidden .kobo folder on the e-reader.',
    steps: [
      { text: 'Connect the e-reader by USB. It mounts like a drive.' },
      { text: 'Open the hidden .kobo folder at the top of the drive.' },
      { text: 'Choose KoboReader.sqlite. One file covers every book on the device.', code: 'KoboReader.sqlite' },
    ],
  },
  {
    id: 'koreader',
    name: 'KOReader',
    hint: 'A book’s .sdr folder: metadata.*.lua.',
    steps: [
      { text: 'Connect the e-reader or its SD card by USB.' },
      { text: 'Open the .sdr folder beside a book’s file.' },
      { text: 'Choose metadata.lua (some books name it metadata.epub.lua). Several books at once are fine.', code: 'metadata.lua' },
    ],
  },
  {
    id: 'kindle',
    name: 'Kindle',
    hint: 'My Clippings.txt in the documents folder.',
    steps: [
      { text: 'Connect the Kindle by USB.' },
      { text: 'Open the documents folder.' },
      { text: 'Choose My Clippings.txt. Bookmarks are left out, and a highlight edited on the device arrives once, as its latest version.', code: 'My Clippings.txt' },
    ],
  },
  {
    id: 'applebooks',
    name: 'Apple Books',
    hint: 'On a Mac: the folder Books keeps its databases in.',
    steps: [
      { text: 'On your Mac, quit Books.' },
      { text: 'Copy where Books keeps your highlights.' },
      { text: 'Press Choose the Books folder. In the window that opens, press Shift ⌘ G, paste, press Return, then Upload.' },
    ],
    beta: true,
  },
  {
    id: 'readwise',
    name: 'Readwise',
    hint: 'Export to CSV from Readwise.',
    steps: [
      { text: 'In Readwise, open Export and choose CSV.' },
      { text: 'Choose the downloaded file. Only highlights from books are imported.' },
    ],
  },
];

const HISTORY_HELP: readonly HelpEntry[] = [
  {
    id: 'goodreads',
    name: 'Goodreads',
    hint: 'My Books → Import and export.',
    steps: [
      { text: 'In Goodreads, open My Books → Import and export.' },
      { text: 'Choose Export Library and download the CSV.' },
      { text: 'Choose goodreads_library_export.csv.', code: 'goodreads_library_export.csv' },
    ],
  },
  {
    id: 'storygraph',
    name: 'StoryGraph',
    hint: 'Manage Account → Export StoryGraph Library.',
    steps: [
      { text: 'In StoryGraph, open Manage Account → Export StoryGraph Library.' },
      { text: 'Choose the CSV it gives you. Quarter stars are rounded to the nearest whole star.' },
    ],
  },
];

const ARCHIVE_HELP: readonly HelpEntry[] = [
  {
    id: 'archive',
    name: 'Nostos archive',
    hint: 'A .nostos archive exported from another Nostos.',
    steps: [
      { text: 'On the other Nostos, open Settings → Backups & data and export a library archive.' },
      { text: 'Drop the .nostos file here. Nostos opens the import and asks before anything is replaced.' },
    ],
  },
];

/**
 * Calibre is a folder the server reads, not a file dropped here (#1527), so
 * its help line carries the one action its steps need — the same shape as
 * Apple Books' "Choose the Books folder". The capability gate keeps it off a
 * host that registered no Calibre source (Cloud, until it accepts an upload).
 */
const CALIBRE_HELP: readonly HelpEntry[] = [
  {
    id: 'calibre',
    name: 'Calibre',
    hint: 'The Calibre library folder this server can see.',
    steps: [
      {
        text: 'Give the server your Calibre library, not a folder of loose books: mount it read-only at /external/calibre in Docker, or set Calibre__LibraryRoot without Docker, and restart. It is the folder that holds',
        code: 'metadata.db',
      },
      {
        text: 'Press Import from Calibre. Nostos reads the library and shows every book before anything is added; the library itself is never changed.',
      },
    ],
  },
];

/**
 * The one drop zone for every import file (issue #1526): highlights from an
 * e-reader or Readwise, a Goodreads or StoryGraph reading history, and a
 * `.nostos` archive. It replaces the row of "Import…" buttons: the server
 * already tells a highlight source apart by content, so asking first only
 * added a step and a chance to guess wrong.
 *
 * Detection is by extension and, for a CSV, its header row; anything else is
 * sent to the server, which stays the authority. Below the box, the sources
 * are help — one line each, expandable to the steps — and an unrecognised
 * file points at every one of them instead of stopping at an error.
 *
 * The same component is hosted by Settings → Import and by both import
 * dialogs, so the command palette's "Import highlights" opens it too.
 */
@Component({
  selector: 'app-import-drop-zone',
  standalone: true,
  imports: [NostosIconComponent, BadgeComponent, ButtonComponent],
  templateUrl: './import-drop-zone.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './import-drop-zone.component.css',
})
export class ImportDropZoneComponent {
  private readonly highlightImport = inject(HighlightImportService);
  private readonly readingHistoryImport = inject(ReadingHistoryImportService);
  private readonly calibreImport = inject(CalibreImportService);
  private readonly archiveHandoff = inject(PortableArchiveHandoffService);
  private readonly capabilities = inject(DeploymentCapabilitiesService);
  private readonly feedbackLink = inject(FeedbackLinkService);
  private readonly router = inject(Router);

  @ViewChild('fileInput')
  private fileInput?: ElementRef<HTMLInputElement>;

  readonly dragActive = signal(false);
  readonly routing = signal(false);
  readonly notice = signal<ImportNotice | null>(null);
  readonly archiveAvailable = signal(false);
  readonly calibreAvailable = signal(false);

  readonly highlightHelp = HIGHLIGHT_HELP;
  readonly historyHelp = HISTORY_HELP;
  readonly archiveHelp = ARCHIVE_HELP;
  readonly calibreHelp = CALIBRE_HELP;
  /** Calibre is a folder on the server, not a file: it is help, never a route. */
  readonly allHelp = computed<readonly HelpEntry[]>(() => [
    ...HIGHLIGHT_HELP,
    ...HISTORY_HELP,
    ...(this.archiveAvailable() ? ARCHIVE_HELP : []),
  ]);

  /** Apple Books is in beta until it has run on more Macs (issue #1357). */
  readonly appleBooksFeedbackUrl = computed(
    () => this.feedbackLink.url() ?? 'https://github.com/Christian-Gennari/Nostos/issues/1357',
  );
  readonly appleBooksFolder = APPLE_BOOKS_FOLDER;
  readonly folderCopied = signal<'idle' | 'copied' | 'failed'>('idle');

  private readonly expandedHelp = signal<ReadonlySet<HelpId>>(new Set());
  private readonly instanceId = `import-drop-zone-${nextInstanceId++}`;
  private dragDepth = 0;

  constructor() {
    // The archive flow exists on a SelfHosted host through library migration,
    // and on Cloud through a portable export. Until the server has answered,
    // the help line stays out rather than promising a flow that is not there.
    this.capabilities.get().subscribe({
      next: (capabilities) => {
        this.archiveAvailable.set(
          capabilities.supportsLibraryMigration === true || capabilities.deploymentMode === 'Cloud',
        );
        // A host that registered no Calibre source (Cloud today) stays dark.
        this.calibreAvailable.set(capabilities.supportsCalibreImport === true);
      },
      error: () => {
        this.archiveAvailable.set(false);
        this.calibreAvailable.set(false);
      },
    });
  }

  helpEntryId(id: HelpId): string {
    return `${this.instanceId}-help-${id}`;
  }

  helpPanelId(id: HelpId): string {
    return `${this.instanceId}-steps-${id}`;
  }

  isHelpOpen(id: HelpId): boolean {
    return this.expandedHelp().has(id);
  }

  toggleHelp(id: HelpId): void {
    this.expandedHelp.update((open) => {
      const next = new Set(open);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  /** Opens a help entry from the "supported files" list under a refusal. */
  revealHelp(id: HelpId): void {
    this.expandedHelp.update((open) => new Set([...open, id]));
    setTimeout(() =>
      document.getElementById(this.helpEntryId(id))?.scrollIntoView?.({ block: 'nearest' }),
    );
  }

  openPicker(): void {
    if (this.routing()) return;
    this.fileInput?.nativeElement.click();
  }

  onFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    // Choosing the same file again must still fire `change`.
    input.value = '';
    void this.accept(files);
  }

  onDragEnter(event: DragEvent): void {
    if (!this.hasFiles(event)) return;
    event.preventDefault();
    this.dragDepth++;
    this.dragActive.set(true);
  }

  onDragOver(event: DragEvent): void {
    if (!this.hasFiles(event)) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
  }

  onDragLeave(): void {
    this.dragDepth = Math.max(0, this.dragDepth - 1);
    if (this.dragDepth === 0) this.dragActive.set(false);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragDepth = 0;
    this.dragActive.set(false);
    void this.accept(Array.from(event.dataTransfer?.files ?? []));
  }

  /** Where the chosen files go. Nothing is written here: each flow previews first. */
  async accept(files: readonly File[]): Promise<void> {
    if (files.length === 0 || this.routing()) return;

    this.notice.set(null);
    this.routing.set(true);
    try {
      const route = await routeImportFiles(files, this.probe);
      this.follow(route);
    } finally {
      this.routing.set(false);
    }
  }

  openAppleBooks(): void {
    this.readingHistoryImport.close();
    this.highlightImport.open('applebooks');
  }

  async copyAppleBooksFolder(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.appleBooksFolder);
      this.folderCopied.set('copied');
    } catch {
      // Without clipboard access the location is shown to copy by hand.
      this.folderCopied.set('failed');
    }
  }

  openLibraryImport(): void {
    this.closeDialogs();
    void this.router.navigate(['/settings/library'], { queryParams: { action: 'import' } });
  }

  /** Calibre reads a folder on the server; its dialog is opened from the help line. */
  openCalibreImport(): void {
    this.closeDialogs();
    this.calibreImport.open();
  }

  private follow(route: ImportRoute): void {
    switch (route.kind) {
      case 'highlights':
        this.readingHistoryImport.close();
        this.highlightImport.openWithFiles(route.files);
        return;
      case 'reading-history':
        this.highlightImport.close();
        this.readingHistoryImport.openWithFile(route.file);
        return;
      case 'archive':
        this.closeDialogs();
        this.archiveHandoff.hand(route.file);
        void this.router.navigate(['/settings/library'], { queryParams: { action: 'import' } });
        return;
      case 'unknown':
        this.notice.set({ message: route.message, serverMessage: route.serverMessage });
        return;
    }
  }

  private closeDialogs(): void {
    this.highlightImport.close();
    this.readingHistoryImport.close();
  }

  /**
   * The fallback the issue asks for: a file whose name and first bytes do not
   * say what it is goes to the server. The reading-history reader is asked
   * first for a CSV (that is what a book list is), the highlight reader first
   * for everything else. Both previews only read.
   */
  private readonly probe: ImportProbeFile = async (file) => {
    // Both previews upload the file; a very large one should not go twice.
    if (file.size > 64 * 1024 * 1024) {
      return { flow: null, message: 'This file is too large for Nostos to check.' };
    }

    const csv = file.name.toLowerCase().endsWith('.csv');
    const order: readonly ('highlights' | 'reading-history')[] = csv
      ? ['reading-history', 'highlights']
      : ['highlights', 'reading-history'];
    let message: string | null = null;

    for (const flow of order) {
      try {
        if (flow === 'highlights') await firstValueFrom(this.highlightImport.preview(file));
        else await firstValueFrom(this.readingHistoryImport.preview(file));
        return { flow, message };
      } catch (error) {
        const detail = this.serverMessage(error);
        if (!message && detail) message = detail;
      }
    }
    return { flow: null, message };
  };

  private serverMessage(error: unknown): string | null {
    if (error instanceof HttpErrorResponse && typeof error.error?.error === 'string') {
      return error.error.error;
    }
    if (error instanceof HttpErrorResponse && error.status === 0) {
      return 'Nostos could not be reached. Check your connection and try again.';
    }
    return null;
  }

  private hasFiles(event: DragEvent): boolean {
    const types = event.dataTransfer?.types;
    return types ? Array.from(types).includes('Files') : true;
  }
}
