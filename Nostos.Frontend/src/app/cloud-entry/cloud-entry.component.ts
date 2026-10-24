import { Component, effect, inject, ChangeDetectionStrategy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { BrandLockupComponent } from '../ui/brand-lockup/brand-lockup.component';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { CloudSignInComponent } from './cloud-sign-in.component';

@Component({
  selector: 'app-cloud-entry',
  standalone: true,
  imports: [
    LoadingIndicatorComponent,
    ButtonComponent,
    NostosIconComponent,
    BrandLockupComponent,
    DatePipe,
    CloudSignInComponent,
  ],
  templateUrl: './cloud-entry.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
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
