import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import type { StylosPairingApproved, StylosPendingPairing } from '../core/dtos/stylos.dtos';
import { StylosService, formatPairingCode, isCompletePairingCode } from '../core/services/stylos.service';
import { ToastService } from '../core/services/toast.service';
import { BrandLockupComponent } from '../ui/brand-lockup/brand-lockup.component';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { NoticeComponent } from '../ui/notice/notice.component';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';

/** What the page says when a code names no waiting device. */
export const NO_WAITING_DEVICE =
  'No device is waiting with that code. Check the six characters on the device’s screen.';

/** What the page says when a device that was waiting has gone. */
export const CODE_ENDED = 'That code has expired. Start pairing again from the device.';

/**
 * `/pair` (WP8): the page a Stylos device's QR opens, and where a code typed
 * from the device's screen is approved. The device shows a code and waits; the
 * person finds it here, sees which device it is, and approves or denies it.
 * Opening the QR link (`/pair?code=K7Q-4MZ`) looks the code up straight away.
 */
@Component({
  selector: 'app-pair-page',
  standalone: true,
  imports: [
    BrandLockupComponent,
    ButtonComponent,
    InputDirective,
    NostosDatePipe,
    NostosIconComponent,
    NoticeComponent,
    RouterLink,
  ],
  templateUrl: './pair.page.html',
  styleUrl: './pair.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PairPage implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly stylos = inject(StylosService);
  private readonly toast = inject(ToastService);

  readonly code = signal('');
  /** The device the code names, once found. */
  readonly device = signal<StylosPendingPairing | null>(null);
  /** The device that was approved on this page. */
  readonly approved = signal<StylosPairingApproved | null>(null);
  readonly denied = signal(false);
  readonly error = signal<string | null>(null);
  readonly busy = signal<'find' | 'approve' | 'deny' | null>(null);

  readonly canFind = computed(() => isCompletePairingCode(this.code()) && this.busy() === null);

  ngOnInit(): void {
    const prefill = this.route.snapshot.queryParamMap.get('code');
    if (!prefill) return;
    this.code.set(formatPairingCode(prefill));
    if (this.canFind()) this.find();
  }

  onCodeInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.code.set(formatPairingCode(input.value));
    // The form control shows the formatted code; a paste that completes it
    // should not need a second click.
    input.value = this.code();
    this.error.set(null);
    this.device.set(null);
    this.denied.set(false);
    if (this.canFind()) this.find();
  }

  find(): void {
    const code = this.code();
    if (!isCompletePairingCode(code) || this.busy()) return;
    this.busy.set('find');
    this.error.set(null);
    this.denied.set(false);
    this.stylos.findPending(code).subscribe({
      next: (device) => {
        this.busy.set(null);
        this.device.set(device);
      },
      error: (error: HttpErrorResponse) => {
        this.busy.set(null);
        this.device.set(null);
        this.error.set(
          error.status === 404
            ? NO_WAITING_DEVICE
            : 'Could not reach this server to look the code up. Try again.',
        );
      },
    });
  }

  approve(): void {
    const device = this.device();
    if (!device || this.busy()) return;
    this.busy.set('approve');
    this.error.set(null);
    this.stylos.approve(device.code).subscribe({
      next: (approved) => {
        this.busy.set(null);
        this.device.set(null);
        this.approved.set(approved);
        this.toast.success(`${approved.deviceName} can sync with Nostos`);
      },
      error: (error: HttpErrorResponse) => {
        this.busy.set(null);
        this.device.set(null);
        this.error.set(error.status === 404 ? CODE_ENDED : 'Could not approve that device. Try again.');
      },
    });
  }

  deny(): void {
    const device = this.device();
    if (!device || this.busy()) return;
    this.busy.set('deny');
    this.error.set(null);
    this.stylos.deny(device.code).subscribe({
      next: () => this.afterDeny(),
      // Gone already is the same answer as denied.
      error: (error: HttpErrorResponse) => {
        if (error.status === 404) {
          this.afterDeny();
          return;
        }
        this.busy.set(null);
        this.error.set('Could not deny that device. Try again.');
      },
    });
  }

  /** Back to the code field, for the next device. */
  startOver(): void {
    this.code.set('');
    this.device.set(null);
    this.approved.set(null);
    this.denied.set(false);
    this.error.set(null);
    this.busy.set(null);
  }

  private afterDeny(): void {
    const name = this.device()?.deviceName;
    this.busy.set(null);
    this.device.set(null);
    this.denied.set(true);
    if (name) this.toast.success(`${name} was not paired`);
  }
}
