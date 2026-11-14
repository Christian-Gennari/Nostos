/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  input,
  model,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, Subscription } from 'rxjs';

import { EBOOK_CATEGORY_LABEL, EBOOK_CATEGORY_LABEL_PLURAL } from '../core/book-format';
import { formatFileSize } from '../core/format';
import {
  ProviderDiscoverySourceStatus,
  ProviderItem,
  ProviderSummary,
} from '../core/dtos/provider.dtos';
import { ProvidersService } from '../core/services/providers.service';
import { BadgeComponent } from '../ui/badge/badge.component';
import { BookCoverComponent } from '../ui/book-cover/book-cover.component';
import { ButtonComponent } from '../ui/button/button.component';
import { RadioDirective } from '../ui/check/check.directive';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { SearchFieldComponent } from '../ui/search-field/search-field.component';
import { SegmentedComponent } from '../ui/segmented/segmented.component';

export type SourceKind = 'all' | 'ebook' | 'audiobook';

/** What the shell needs from a catalogue pick: the item and the asset to fetch. */
export interface SourceSelection {
  item: ProviderItem;
  detail: ProviderItem | null;
  assetId: string | null;
  providerName: string;
  /** The asset's name and size, as the review summary says it. */
  assetLabel: string;
}

/**
 * Catalogue discovery (#167): one search across the free sources, one ordered
 * result list, and the detail of the chosen edition (formats, rights, size).
 *
 * It owns the search, its cancellation and the slow-catalogue timer, so nothing
 * about discovery survives the picker being closed. The shell keeps the step
 * flow and the chosen item: `selectionChange` is how the pick it must review
 * and import gets out of here.
 */
@Component({
  selector: 'app-add-book-source-picker',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    BadgeComponent,
    BookCoverComponent,
    ButtonComponent,
    InputDirective,
    LoadingIndicatorComponent,
    NostosIconComponent,
    RadioDirective,
    SearchFieldComponent,
    SegmentedComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './add-book-source-picker.component.html',
  styleUrl: './add-book-source-picker.component.css',
})
export class AddBookSourcePickerComponent {
  /**
   * How long a search may wait before the waiting state says a catalogue is
   * being slow (#658). Below the server's 4 s aggregate budget, so the notice
   * only appears when a source is actually dragging the search out.
   */
  static readonly SOURCE_SLOW_AFTER_MS = 2500;

  private readonly providers = inject(ProvidersService);
  private readonly destroyRef = inject(DestroyRef);

  /** The query is the shell's: the prefill sets it before this view exists. */
  readonly query = model('');
  /** True when the shell opened discovery with a title already in hand. */
  readonly searchOnInit = input(false);

  readonly selectionChange = output<SourceSelection | null>();
  /** Why the chosen edition's detail could not be loaded, for the shell's notice. */
  readonly detailErrorChange = output<string | null>();

  readonly kind = signal<SourceKind>('all');
  readonly sourceKinds = [
    { value: 'all', label: 'All' },
    { value: 'ebook', label: EBOOK_CATEGORY_LABEL_PLURAL },
    { value: 'audiobook', label: 'Audiobooks' },
  ] as const;

  readonly providerList = signal<ProviderSummary[]>([]);
  readonly providersLoading = signal(false);
  readonly providersError = signal<string | null>(null);

  readonly results = signal<ProviderItem[]>([]);
  readonly statuses = signal<ProviderDiscoverySourceStatus[]>([]);
  readonly hasMore = signal(false);
  readonly searching = signal(false);
  readonly searched = signal(false);
  readonly searchError = signal<string | null>(null);
  /** True once a search has waited long enough that a slow catalogue is likely (#658). */
  readonly slow = signal(false);

  /** Narrow layouts show results and the selected book as two views (#641). */
  readonly detailOpen = signal(false);

  readonly selectedItem = signal<ProviderItem | null>(null);
  readonly selectedDetail = signal<ProviderItem | null>(null);
  readonly selectedAssetId = signal<string | null>(null);
  readonly detailError = signal<string | null>(null);

  readonly selectedSourceItem = computed(() => this.selectedDetail() ?? this.selectedItem());
  readonly failures = computed(() => this.statuses().filter((source) => !source.succeeded));
  readonly notices = computed(() => this.statuses().filter((source) => source.succeeded && !!source.notice));
  readonly failureNames = computed(() =>
    this.failures()
      .map((source) => source.displayName)
      .join(', '),
  );
  readonly allSourcesFailed = computed(
    () =>
      this.searched() &&
      this.statuses().length > 0 &&
      this.statuses().every((source) => !source.succeeded),
  );

  /** The asset that will actually be imported, named in the collapsed summary. */
  readonly selectedAssetLabel = computed(() => {
    const item = this.selectedSourceItem();
    if (!item) return '';

    const id = this.selectedAssetId();
    const asset =
      item.assets.find((a) => a.id === id) ??
      item.assets.find((a) => a.isPreferred) ??
      item.assets[0];
    if (!asset) return '';

    const size = this.formatBytes(asset.sizeBytes);
    return size ? `${asset.label} · ${size}` : asset.label;
  });

  readonly selectedAsset = computed(() => {
    const item = this.selectedSourceItem();
    if (!item) return null;

    const selectedId = this.selectedAssetId();
    return (
      item.assets.find((asset) => asset.id === selectedId) ??
      item.assets.find((asset) => asset.isPreferred) ??
      item.assets[0] ??
      null
    );
  });

  readonly ebookFormatFamilies = computed(() => {
    const item = this.selectedSourceItem();
    if (!item || item.mediaKind !== 'ebook') return [] as Array<'EPUB' | 'PDF'>;

    const families = item.assets
      .map((asset) => this.assetFormatFamily(asset.sourceFormat))
      .filter((family): family is 'EPUB' | 'PDF' => family !== null);

    return [...new Set(families)];
  });

  readonly selectedFormatFamily = computed(() => {
    const asset = this.selectedAsset();
    return asset ? this.assetFormatFamily(asset.sourceFormat) : null;
  });

  /**
   * The catalogue's rights text, minus the full stop it ends with: the line
   * finishes with the attribution in parentheses, so the source's own terminal
   * punctuation would sit mid-sentence.
   */
  readonly rightsText = computed(() => {
    const statement = this.selectedSourceItem()?.rightsStatement ?? '';
    return statement.trim().replace(/\.+$/, '');
  });

  /**
   * The catalogue(s) the current search is querying (#658), mirroring the
   * server's eligibility rule (ProviderDiscoveryService.IsEligible).
   */
  readonly searchingSources = computed(() => {
    const kind = this.kind();
    return this.providerList().filter((provider) => {
      const capabilities = provider.capabilities.map((name) => name.toLowerCase());
      if (!capabilities.includes('search')) return false;
      const ebook = capabilities.includes('ebookacquisition');
      const audiobook = capabilities.includes('audiobookacquisition');
      if (kind === 'ebook') return ebook;
      if (kind === 'audiobook') return audiobook;
      return ebook || audiobook;
    });
  });

  /**
   * The footer's next action. Choosing a result only seeds the Review Book
   * step, so this is never labelled "Import".
   */
  readonly primaryAction = computed<{ label: string; enabled: boolean }>(() => {
    const item = this.selectedItem();
    if (!item) return { label: 'Select a book', enabled: false };
    if (this.detailError()) return { label: 'Use this book', enabled: false };
    const detail = this.selectedDetail();
    if (!detail) return { label: 'Loading edition…', enabled: false };
    return { label: 'Use this book', enabled: detail.assets.length > 0 };
  });

  private readonly resultsPane = viewChild<ElementRef<HTMLElement>>('sourceResultsPane');
  private resultsScrollTop = 0;
  private slowTimer: ReturnType<typeof setTimeout> | null = null;
  private searchVersion = 0;
  private searchSub: Subscription | null = null;
  private detailSub: Subscription | null = null;
  /** The query the in-flight answer belongs to; typing away invalidates it. */
  private readonly activeQuery = signal('');

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.cancelRequests();
      this.clearSlowTimer();
    });
  }

  ngOnInit(): void {
    this.loadProviders();
    if (this.searchOnInit() && this.query().trim().length >= 2) this.searchSource();
  }

  /** The shell focuses the search field when discovery opens. */
  focusQuery(): void {
    this.queryInput()?.nativeElement?.focus();
  }

  private readonly queryInput = viewChild<ElementRef<HTMLInputElement>>('sourceQueryInput');

  onQueryInput(value: string): void {
    this.query.set(value);
    // A response for a query the user has already typed away from must not
    // replace what is on screen, even before the next search starts.
    this.activeQuery.set(value.trim());
  }

  searchSource(query = this.query()): void {
    const trimmed = query.trim();
    if (trimmed.length < 2) return;

    const kind = this.kind();
    const version = ++this.searchVersion;
    this.activeQuery.set(trimmed);
    // Abort, not just ignore, the search this one replaces.
    this.searchSub?.unsubscribe();
    this.searching.set(true);
    this.searchError.set(null);
    this.clearSelection();
    this.detailOpen.set(false);
    this.resultsScrollTop = 0;
    this.clearSlowTimer();
    this.slowTimer = setTimeout(() => {
      if (this.searchVersion === version && this.searching()) {
        this.slow.set(true);
      }
    }, AddBookSourcePickerComponent.SOURCE_SLOW_AFTER_MS);

    this.searchSub = this.providers
      .searchAll(trimmed, kind === 'all' ? undefined : kind)
      .pipe(
        finalize(() => {
          if (this.searchVersion === version) {
            this.searching.set(false);
            this.clearSlowTimer();
          }
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (result) => {
          if (!this.isCurrent(version, kind, trimmed)) return;

          this.results.set(result.items);
          this.statuses.set(result.sources);
          this.hasMore.set(result.hasMore);
          this.searched.set(true);
        },
        error: (error) => {
          if (!this.isCurrent(version, kind, trimmed)) return;

          this.results.set([]);
          this.statuses.set([]);
          this.searched.set(true);
          this.searchError.set(this.describeError(error, 'Free sources could not be searched right now.'));
        },
      });
  }

  setKind(kind: SourceKind, query = this.query()): void {
    if (kind === this.kind()) return;

    this.kind.set(kind);
    // A search for the previous filter is obsolete whether or not a new one starts.
    this.cancelRequests();
    this.clearSelection();
    this.detailOpen.set(false);

    if (query.trim().length >= 2) this.searchSource(query);
  }

  selectItem(item: ProviderItem): void {
    this.selectedItem.set(item);
    this.selectedDetail.set(null);
    this.selectedAssetId.set(null);
    this.setDetailError(null);
    this.emitSelection();

    // Detail and acquisition are routed by the result's own provider. Capturing
    // both halves of the identity prevents identical external ids from two
    // sources from colliding when responses arrive out of order.
    const selectedKey = this.sourceItemKey(item);
    this.openDetail();

    this.detailSub?.unsubscribe();
    this.detailSub = this.providers
      .item(item.providerId, item.externalId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          const current = this.selectedItem();
          if (!current || this.sourceItemKey(current) !== selectedKey) return;
          if (this.sourceItemKey(detail) !== selectedKey) return;

          this.selectedDetail.set(detail);
          const preferredDetail = detail.assets.find((a) => a.isPreferred) ?? detail.assets[0];
          this.selectedAssetId.set(preferredDetail?.id ?? null);
          this.emitSelection();
        },
        error: () => {
          const current = this.selectedItem();
          if (!current || this.sourceItemKey(current) !== selectedKey) return;

          this.setDetailError(
            "That book's details could not be loaded, so it cannot be imported right now.",
          );
        },
      });
  }

  setAsset(assetId: string): void {
    this.selectedAssetId.set(assetId);
    this.emitSelection();
  }

  selectFormatFamily(family: 'EPUB' | 'PDF'): void {
    const item = this.selectedSourceItem();
    if (!item) return;

    const candidates = item.assets.filter(
      (asset) => this.assetFormatFamily(asset.sourceFormat) === family,
    );
    const selected = candidates.find((asset) => asset.isPreferred) ?? candidates[0];
    if (selected) this.setAsset(selected.id);
  }

  assetsForSelectedFamily(): ProviderItem['assets'] {
    const item = this.selectedSourceItem();
    const family = this.selectedFormatFamily();
    if (!item || !family) return [];

    return item.assets.filter((asset) => this.assetFormatFamily(asset.sourceFormat) === family);
  }

  /** Narrow layouts: leave the list for the selected book, keeping its scroll. */
  backToSourceResults(): void {
    this.detailOpen.set(false);
    setTimeout(() => {
      const pane = this.resultsPane()?.nativeElement;
      if (pane) pane.scrollTop = this.resultsScrollTop;
    }, 0);
  }

  sourceItemKey(item: Pick<ProviderItem, 'providerId' | 'externalId'>): string {
    return `${encodeURIComponent(item.providerId)}::${encodeURIComponent(item.externalId)}`;
  }

  isSelectedSourceItem(item: ProviderItem): boolean {
    const selected = this.selectedItem();
    return !!selected && this.sourceItemKey(selected) === this.sourceItemKey(item);
  }

  providerName(providerId: string): string {
    return (
      this.providerList().find((provider) => provider.id === providerId)?.displayName ??
      this.statuses().find((source) => source.providerId === providerId)?.displayName ??
      providerId
    );
  }

  mediaKindLabel(item: ProviderItem): string {
    return item.mediaKind === 'audiobook' ? 'Audiobook' : EBOOK_CATEGORY_LABEL;
  }

  /**
   * The facts that decide between two recordings of the same work: who reads
   * it, how long it is and how many sections it has. Empty for an ebook, so a
   * Gutenberg row renders exactly as it did.
   */
  sourceFacts(item: ProviderItem): string {
    return [
      item.narrator ? `Narrated by ${item.narrator}` : null,
      item.duration,
      item.partCount && item.partCount > 1 ? `${item.partCount} sections` : null,
    ]
      .filter((part): part is string => !!part)
      .join(' · ');
  }

  /** Why a source contributed nothing, in two or three words (#658). */
  sourceFailureReason(source: ProviderDiscoverySourceStatus): string {
    return source.errorCode === 'provider_timeout' ? 'timed out' : 'unavailable';
  }

  assetFormatFamily(sourceFormat: string | null): 'EPUB' | 'PDF' | null {
    const format = sourceFormat?.toLowerCase();
    if (!format) return null;
    if (format === 'application/epub+zip' || format.startsWith('epub')) return 'EPUB';
    if (format === 'application/pdf' || format.startsWith('pdf')) return 'PDF';
    return null;
  }

  formatBytes(bytes: number | null): string {
    return bytes && bytes > 0 ? formatFileSize(bytes) : '';
  }

  private loadProviders(): void {
    this.providersLoading.set(true);
    this.providersError.set(null);

    this.providers
      .list()
      .pipe(
        finalize(() => this.providersLoading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (list) => {
          this.providerList.set(list);
          if (list.length === 0) {
            this.providersError.set('No book sources are enabled. Turn them on in Settings.');
          }
        },
        error: () => this.providersError.set('Could not load the available sources.'),
      });
  }

  private isCurrent(version: number, kind: SourceKind, query: string): boolean {
    return (
      this.searchVersion === version &&
      this.kind() === kind &&
      this.activeQuery() === query
    );
  }

  private emitSelection(): void {
    const item = this.selectedItem();
    if (!item) {
      this.selectionChange.emit(null);
      return;
    }
    this.selectionChange.emit({
      item,
      detail: this.selectedDetail(),
      assetId: this.selectedAssetId(),
      providerName: this.providerName(item.providerId),
      assetLabel: this.selectedAssetLabel(),
    });
  }

  private setDetailError(message: string | null): void {
    this.detailError.set(message);
    this.detailErrorChange.emit(message);
  }

  private clearSelection(): void {
    this.detailSub?.unsubscribe();
    this.detailSub = null;
    this.selectedItem.set(null);
    this.selectedDetail.set(null);
    this.selectedAssetId.set(null);
    this.setDetailError(null);
    this.selectionChange.emit(null);
  }

  private openDetail(): void {
    const pane = this.resultsPane()?.nativeElement;
    if (pane) this.resultsScrollTop = pane.scrollTop;
    this.detailOpen.set(true);
  }

  private cancelRequests(): void {
    this.searchSub?.unsubscribe();
    this.searchSub = null;
    this.detailSub?.unsubscribe();
    this.detailSub = null;
    this.searching.set(false);
    this.clearSlowTimer();
  }

  private clearSlowTimer(): void {
    if (this.slowTimer !== null) clearTimeout(this.slowTimer);
    this.slowTimer = null;
    this.slow.set(false);
  }

  private describeError(error: unknown, fallback: string): string {
    const body = (error as { error?: { detail?: string; title?: string } })?.error;
    return body?.detail || body?.title || fallback;
  }
}
