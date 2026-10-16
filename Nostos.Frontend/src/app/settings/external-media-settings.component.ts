import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

import {
  ExternalMediaImportState,
  ExternalMediaItemStatus,
  ExternalMediaMissingBook,
  ExternalMediaScanItem,
  ExternalMediaScanSummary,
  ExternalMediaService,
  ExternalMediaStatus,
} from '../core/services/external-media.service';
import { formatBytes } from '../library-transfer/library-transfer.copy';
import { ButtonComponent } from '../ui/button/button.component';
import { DialogActionsComponent } from '../ui/dialog-actions/dialog-actions.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { NoticeComponent } from '../ui/notice/notice.component';
import { CheckboxDirective } from '../ui/check/check.directive';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';

type ReviewStage = 'review' | 'importing' | 'result';

/** One status group of the review list, loaded a page at a time. */
interface ItemPage {
  items: ExternalMediaScanItem[];
  total: number;
  loading: boolean;
}

const EMPTY_PAGE: ItemPage = { items: [], total: 0, loading: false };
const PAGE_SIZE = 200;
const POLL_MS = 1000;

/** The promise the whole feature rests on, said the same way everywhere. */
export const EXTERNAL_MEDIA_PROMISE =
  'Your original files stay where they are and are never modified or deleted by Nostos.';

export const EXTERNAL_MEDIA_GUIDE_URL =
  'https://docs.nostos.page/configure/external-media/';

/**
 * Settings → This server → External media (issue #843, SelfHosted only).
 *
 * Scan the folder the administrator mounted, review what was found, import
 * it, scan again later. The server does the work and keeps the state, so
 * this component only shows it: closing the dialog or the page mid-scan or
 * mid-import loses nothing, and reopening picks the progress back up.
 */
@Component({
  selector: 'app-external-media-settings',
  standalone: true,
  imports: [
    CheckboxDirective,
    LoadingIndicatorComponent,
    NoticeComponent,
    NgTemplateOutlet,
    ButtonComponent,
    DialogActionsComponent,
    IconButtonComponent,
    NostosIconComponent,
    ModalShell,
  ],
  templateUrl: './external-media-settings.component.html',
  styleUrl: './external-media-settings.component.css',
})
export class ExternalMediaSettingsComponent implements OnInit {
  private readonly api = inject(ExternalMediaService);
  private readonly destroyRef = inject(DestroyRef);

  readonly promise = EXTERNAL_MEDIA_PROMISE;
  readonly guideUrl = EXTERNAL_MEDIA_GUIDE_URL;

  readonly status = signal<ExternalMediaStatus | null>(null);
  readonly summary = signal<ExternalMediaScanSummary | null>(null);
  readonly importState = signal<ExternalMediaImportState | null>(null);
  readonly error = signal<string | null>(null);
  readonly starting = signal(false);

  readonly reviewOpen = signal(false);
  readonly stage = signal<ReviewStage>('review');
  readonly ready = signal<ItemPage>(EMPTY_PAGE);
  readonly review = signal<ItemPage>(EMPTY_PAGE);
  readonly unusable = signal<ItemPage>(EMPTY_PAGE);
  readonly missing = signal<ExternalMediaMissingBook[]>([]);
  readonly unsupportedSamples = signal<string[]>([]);

  /** Ready books the owner unticked, and books to check that they ticked. */
  readonly leftOut = signal<ReadonlySet<string>>(new Set());
  readonly added = signal<ReadonlySet<string>>(new Set());

  readonly scanning = computed(() => this.status()?.scan.status === 'scanning');
  readonly importing = computed(() => this.importState()?.status === 'importing');
  readonly selectedCount = computed(
    () => Math.max(this.ready().total - this.leftOut().size, 0) + this.added().size,
  );
  readonly canReview = computed(() => {
    const summary = this.summary();
    return !!summary && !this.scanning() && summary.discovered + summary.missingBooks > 0;
  });
  readonly importCounts = computed(() => {
    const outcomes = this.importState()?.outcomes ?? [];
    const count = (outcome: string) => outcomes.filter((o) => o.outcome === outcome).length;
    return {
      imported: count('imported'),
      alreadyLinked: count('already_linked'),
      notImported: count('failed') + count('skipped'),
    };
  });
  readonly importProblems = computed(() =>
    (this.importState()?.outcomes ?? []).filter((o) => o.outcome === 'failed' || o.outcome === 'skipped'),
  );

  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private wasImporting = false;

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => this.stopPolling());
    void this.refresh();
  }

  /** Reads everything the card shows, and keeps reading while work is running. */
  async refresh(): Promise<void> {
    try {
      const status = await firstValueFrom(this.api.status());
      this.status.set(status);
      // Until a folder is set up there is nothing to scan or import, and
      // the server answers those routes with 404.
      if (!status.configured) return;

      const [scan, importState] = await Promise.all([
        firstValueFrom(this.api.scan(undefined, 0, 1)),
        firstValueFrom(this.api.importState()),
      ]);
      this.summary.set(scan.summary);
      this.importState.set(importState);
      // Only a failed scan is reported from here; a message from an action
      // the owner just took stays until they act again.
      if (scan.scan.status === 'failed') this.error.set(scan.scan.error);

      const importing = importState.status === 'importing';
      if (this.wasImporting && !importing) {
        this.wasImporting = false;
        if (this.reviewOpen()) this.stage.set('result');
        // What was "ready" a moment ago is "already in Nostos" now.
        await this.startScan();
        return;
      }
      this.wasImporting = importing;
    } catch (failure) {
      this.error.set(this.describe(failure, 'The external media folder could not be checked.'));
    }

    this.schedulePoll();
  }

  async startScan(): Promise<void> {
    if (this.starting()) return;
    this.starting.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.api.startScan());
    } catch (failure) {
      this.error.set(this.describe(failure, 'The scan could not be started.'));
    } finally {
      this.starting.set(false);
    }
    await this.refresh();
  }

  async openReview(): Promise<void> {
    this.leftOut.set(new Set());
    this.added.set(new Set());
    this.stage.set(this.importing() ? 'importing' : 'review');
    this.reviewOpen.set(true);
    this.ready.set({ ...EMPTY_PAGE, loading: true });
    this.review.set({ ...EMPTY_PAGE, loading: true });
    this.unusable.set({ ...EMPTY_PAGE, loading: true });

    try {
      const [ready, review, unusable] = await Promise.all([
        firstValueFrom(this.api.scan('ready', 0, PAGE_SIZE)),
        firstValueFrom(this.api.scan('review', 0, PAGE_SIZE)),
        firstValueFrom(this.api.scan('unusable', 0, PAGE_SIZE)),
      ]);
      this.ready.set({ items: ready.items, total: ready.matchingItems, loading: false });
      this.review.set({ items: review.items, total: review.matchingItems, loading: false });
      this.unusable.set({ items: unusable.items, total: unusable.matchingItems, loading: false });
      this.missing.set(ready.missing);
      this.unsupportedSamples.set(ready.unsupportedSamples);
    } catch (failure) {
      this.error.set(this.describe(failure, 'The scan results could not be loaded.'));
      this.reviewOpen.set(false);
    }
  }

  async showMore(status: ExternalMediaItemStatus): Promise<void> {
    const page = this.pageFor(status);
    const current = page();
    if (current.loading || current.items.length >= current.total) return;

    page.set({ ...current, loading: true });
    try {
      const next = await firstValueFrom(this.api.scan(status, current.items.length, PAGE_SIZE));
      page.set({
        items: [...current.items, ...next.items],
        total: next.matchingItems,
        loading: false,
      });
    } catch {
      page.set({ ...current, loading: false });
    }
  }

  closeReview(): void {
    this.reviewOpen.set(false);
  }

  isSelected(item: ExternalMediaScanItem): boolean {
    return item.status === 'ready' ? !this.leftOut().has(item.key) : this.added().has(item.key);
  }

  toggle(item: ExternalMediaScanItem): void {
    const target = item.status === 'ready' ? this.leftOut : this.added;
    const next = new Set(target());
    if (!next.delete(item.key)) next.add(item.key);
    target.set(next);
  }

  async importSelected(): Promise<void> {
    if (this.selectedCount() === 0 || this.starting()) return;
    this.starting.set(true);
    this.error.set(null);
    try {
      const state = await firstValueFrom(
        this.api.startImport({
          allReady: true,
          except: [...this.leftOut()],
          keys: [...this.added()],
        }),
      );
      this.importState.set(state);
      this.wasImporting = true;
      this.stage.set('importing');
    } catch (failure) {
      this.error.set(this.describe(failure, 'The import could not be started.'));
    } finally {
      this.starting.set(false);
    }
    this.schedulePoll();
  }

  /** "Author · 12 files · 1.2 GB" */
  describeItem(item: ExternalMediaScanItem): string {
    const parts = [
      item.author,
      item.layout === 'tracks' ? `${item.fileCount} files` : this.format(item.location),
      formatBytes(item.totalBytes),
    ];
    return parts.filter((part) => !!part).join(' · ');
  }

  count(n: number, noun: string): string {
    return `${n} ${noun}${n === 1 ? '' : 's'}`;
  }

  private format(location: string): string {
    return location.slice(location.lastIndexOf('.') + 1).toUpperCase();
  }

  private pageFor(status: ExternalMediaItemStatus) {
    return status === 'ready' ? this.ready : status === 'review' ? this.review : this.unusable;
  }

  private schedulePoll(): void {
    this.stopPolling();
    if (!this.scanning() && !this.importing()) return;
    this.pollTimer = setTimeout(() => void this.refresh(), POLL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  private describe(failure: unknown, fallback: string): string {
    if (failure instanceof HttpErrorResponse && typeof failure.error?.message === 'string') {
      return failure.error.message;
    }
    return fallback;
  }
}
