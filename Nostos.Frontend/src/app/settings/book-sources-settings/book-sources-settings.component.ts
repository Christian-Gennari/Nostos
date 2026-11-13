import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';

import { ProviderSettingsItem } from '../../core/dtos/provider.dtos';
import { ProviderSettingsService } from '../../core/services/provider-settings.service';
import { ToastService } from '../../core/services/toast.service';
import { BadgeComponent } from '../../ui/badge/badge.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { SwitchComponent } from '../../ui/switch/switch.component';

/**
 * Every user-visible string for the "Book sources" card (issue #774), in one
 * place so a wording change is a single edit.
 */
const BOOK_PROVIDER_COPY = {
  intro:
    'Choose which free book and audiobook sources Nostos searches when you add a book. Sources you turn off stay out of Add Book and cannot be used until you turn them back on.',
  loading: 'Loading book sources…',
  loadFailed: 'Could not load the book sources.',
  loadFailedHelp: 'The server did not answer the request for the provider list.',
  empty: 'No book sources are available on this server.',
  saveFailed: 'Could not save this source. Your previous choice is still in effect.',
} as const;

/**
 * Settings → Library → Book sources: the discovery providers Nostos searches
 * (issue #774).
 *
 * Loaded lazily the first time the Library tab opens, because a settings page
 * should not ask the server about sources nobody has looked at; a failed load
 * retries on the next open. A toggle is applied optimistically and reverted on
 * failure, because a native checkbox stays where the click put it unless the
 * bound value actually changes.
 */
@Component({
  selector: 'app-book-sources-settings',
  standalone: true,
  imports: [
    BadgeComponent,
    EmptyStateComponent,
    LoadingIndicatorComponent,
    SwitchComponent,
  ],
  templateUrl: './book-sources-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './book-sources-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BookSourcesSettingsComponent {
  private readonly providerSettingsService = inject(ProviderSettingsService);
  private readonly toast = inject(ToastService);

  /** True while the Library section is the open one. */
  readonly active = input(false);

  /** The card's copy, exposed so the template reads one source. */
  readonly copyProviders = BOOK_PROVIDER_COPY;

  /** Every registered source with its effective choice, disabled ones included. */
  readonly providerSettings = signal<ProviderSettingsItem[]>([]);
  readonly providerSettingsLoading = signal(false);
  readonly providerSettingsError = signal(false);
  readonly providerSettingsSaveFailed = signal(false);
  readonly providerSavingIds = signal<ReadonlySet<string>>(new Set<string>());
  private providerSettingsLoaded = false;

  constructor() {
    effect(() => {
      // `untracked`: the load reads and writes its own loading/error signals.
      // Tracking them here would make the effect depend on what it writes and
      // re-trigger itself (endlessly, on a failing load).
      if (this.active()) untracked(() => this.loadProviderSettings());
    });
  }

  loadProviderSettings(): void {
    if (this.providerSettingsLoading()) return;
    if (this.providerSettingsLoaded && !this.providerSettingsError()) return;

    this.providerSettingsLoading.set(true);
    this.providerSettingsError.set(false);
    this.providerSettingsService.list().subscribe({
      next: (response) => {
        this.providerSettings.set(response.providers);
        this.providerSettingsLoaded = true;
        this.providerSettingsLoading.set(false);
      },
      error: () => {
        this.providerSettingsError.set(true);
        this.providerSettingsLoading.set(false);
      },
    });
  }

  setProviderEnabled(provider: ProviderSettingsItem, event: Event): void {
    if (this.providerSavingIds().has(provider.id)) return;

    const previousEnabled = provider.enabled;
    const checked = (event.target as HTMLInputElement).checked;

    this.providerSettings.set(
      this.withProviderEnabled(this.providerSettings(), provider.id, checked),
    );
    this.setProviderSaving(provider.id, true);
    this.providerSettingsSaveFailed.set(false);

    this.providerSettingsService.setEnabled(provider.id, checked).subscribe({
      next: (updated) => {
        this.setProviderSaving(provider.id, false);
        // The server's item is the truth; replacing it also undoes any drift.
        this.providerSettings.update((list) =>
          list.map((item) => (item.id === updated.id ? updated : item)),
        );
      },
      error: () => {
        this.setProviderSaving(provider.id, false);
        // Revert only this row: another provider's concurrent save may have
        // succeeded since this one started.
        this.providerSettings.update((list) =>
          this.withProviderEnabled(list, provider.id, previousEnabled),
        );
        this.providerSettingsSaveFailed.set(true);
        this.toast.error(BOOK_PROVIDER_COPY.saveFailed);
      },
    });
  }

  private withProviderEnabled(
    list: readonly ProviderSettingsItem[],
    providerId: string,
    enabled: boolean,
  ): ProviderSettingsItem[] {
    return list.map((item) => (item.id === providerId ? { ...item, enabled } : item));
  }

  private setProviderSaving(providerId: string, saving: boolean): void {
    const next = new Set(this.providerSavingIds());
    if (saving) next.add(providerId);
    else next.delete(providerId);
    this.providerSavingIds.set(next);
  }
}
