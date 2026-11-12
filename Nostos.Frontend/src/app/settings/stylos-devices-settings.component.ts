import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';

import type { StylosDevice } from '../core/dtos/stylos.dtos';
import { StylosService } from '../core/services/stylos.service';
import { ToastService } from '../core/services/toast.service';
import { ButtonComponent } from '../ui/button/button.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';

/**
 * Settings → Devices (WP8): the Stylos writing devices paired with this
 * account. Each device's token is stored only as a hash; revoking deletes the
 * device row, so its next request is refused and it must pair again.
 */
@Component({
  selector: 'app-stylos-devices-settings',
  standalone: true,
  imports: [
    ButtonComponent,
    ConfirmModal,
    EmptyStateComponent,
    IconButtonComponent,
    LoadingIndicatorComponent,
    NostosDatePipe,
    RouterLink,
  ],
  templateUrl: './stylos-devices-settings.component.html',
  styleUrl: './stylos-devices-settings.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StylosDevicesSettingsComponent implements OnInit {
  private readonly api = inject(StylosService);
  private readonly toast = inject(ToastService);

  readonly devices = signal<StylosDevice[] | null>(null);
  readonly loadFailed = signal(false);
  readonly pendingRevoke = signal<StylosDevice | null>(null);
  readonly revoking = signal(false);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loadFailed.set(false);
    this.api.devices().subscribe({
      next: (page) => this.devices.set(page.devices),
      error: () => this.loadFailed.set(true),
    });
  }

  requestRevoke(device: StylosDevice): void {
    if (this.revoking()) return;
    this.pendingRevoke.set(device);
  }

  cancelRevoke(): void {
    if (this.revoking()) return;
    this.pendingRevoke.set(null);
  }

  confirmRevoke(): void {
    const device = this.pendingRevoke();
    if (!device || this.revoking()) return;
    this.revoking.set(true);
    this.api.revoke(device.id).subscribe({
      next: () => this.afterRevoke(device),
      error: (error: HttpErrorResponse) => {
        // Already gone is what was asked for.
        if (error.status === 404) {
          this.afterRevoke(device);
          return;
        }
        this.revoking.set(false);
        this.toast.error(`Could not revoke ${device.name}`);
      },
    });
  }

  private afterRevoke(device: StylosDevice): void {
    this.revoking.set(false);
    this.pendingRevoke.set(null);
    this.devices.update((devices) => (devices ?? []).filter((d) => d.id !== device.id));
    this.toast.success(`${device.name} can no longer sync`);
  }
}
