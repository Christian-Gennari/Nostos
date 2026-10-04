import { Component, ElementRef, ViewChild, effect, inject, signal } from '@angular/core';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { LibraryImportFlowComponent } from '../library-transfer/components/library-import-flow.component';

@Component({
  selector: 'app-cloud-entry',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent, LibraryImportFlowComponent],
  templateUrl: './cloud-entry.component.html',
  styleUrl: './cloud-entry.component.css',
})
export class CloudEntryComponent {
  readonly entry = inject(CloudEntryService);

  /** True once the user picked "Import an existing Nostos library". */
  readonly sharedImportOpen = signal(false);

  @ViewChild('archiveInput')
  private archiveInput?: ElementRef<HTMLInputElement>;

  constructor() {
    effect(() => {
      const redirectUrl = this.entry.checkoutRedirect();
      if (redirectUrl) {
        this.navigateTo(redirectUrl);
      }
    });
  }

  navigateTo(url: string): void {
    globalThis.location.assign(url);
  }

  signIn(): void {
    this.navigateTo(this.entry.loginUrl());
  }

  async checkout(): Promise<void> {
    const url = await this.entry.beginCheckout(this.entry.selectedOffer()?.offerId ?? null);
    if (url) this.navigateTo(url);
  }

  async checkSubscription(): Promise<void> {
    await this.entry.checkSubscription();
  }

  async manageSubscription(): Promise<void> {
    const url = await this.entry.openBillingPortal();
    if (url) this.navigateTo(url);
  }

  /**
   * The "bring my existing library" choice. When the host advertises
   * migration the shared flow renders in place of the legacy picker; the
   * legacy whole-file path stays untouched otherwise (#680 slice B6).
   */
  openImport(): void {
    if (this.entry.supportsLibraryMigration()) {
      this.sharedImportOpen.set(true);
      return;
    }
    this.chooseImport();
  }

  backToChoices(): void {
    this.sharedImportOpen.set(false);
  }

  chooseImport(): void {
    this.archiveInput?.nativeElement.click();
  }

  async importSelected(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    await this.entry.importPortableArchive(file);
    input.value = '';
  }
}
