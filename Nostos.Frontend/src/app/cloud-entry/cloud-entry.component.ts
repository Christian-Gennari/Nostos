import { Component, effect, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { CloudEntryService } from '../core/services/cloud-entry.service';

@Component({
  selector: 'app-cloud-entry',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent, DatePipe],
  templateUrl: './cloud-entry.component.html',
  styleUrl: './cloud-entry.component.css',
})
export class CloudEntryComponent {
  readonly entry = inject(CloudEntryService);
  private readonly router = inject(Router);

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

  async cancelDeletion(): Promise<void> {
    await this.entry.cancelAccountDeletion();
  }

  async checkDeletionStatus(): Promise<void> {
    await this.entry.retryAccountDeletionStatus();
  }

  openImport(): void {
    this.entry.openManageLibraryFromFirstRun();
    void this.router.navigate(['/settings/library'], {
      queryParams: { action: 'import', source: 'first-run' },
    });
  }
}
