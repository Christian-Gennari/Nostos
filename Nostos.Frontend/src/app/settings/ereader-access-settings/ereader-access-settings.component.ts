import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';

import { ManagedOpdsAccess, OpdsInfo } from '../../core/dtos/opds.dtos';
import { OpdsService } from '../../core/services/opds.service';
import { ToastService } from '../../core/services/toast.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { ConfirmModal } from '../../ui/confirm-modal/confirm-modal.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { NoticeComponent } from '../../ui/notice/notice.component';

/** How long the copy button stays on "Copied" before it offers to copy again. */
const COPIED_FEEDBACK_MS = 2_500;

/**
 * Settings → Library → E-reader access (issue #187).
 *
 * One product capability with two deployment-specific connection models: a
 * private-network catalogue address on SelfHosted, or managed Cloud reader
 * credentials. The capability gates arrive as inputs so the parent stays the
 * one place that decides which surface this deployment has, and nothing asks
 * the owner APIs until that has been settled.
 */
@Component({
  selector: 'app-ereader-access-settings',
  standalone: true,
  imports: [
    ButtonComponent,
    ConfirmModal,
    EmptyStateComponent,
    LoadingIndicatorComponent,
    NostosIconComponent,
    NoticeComponent,
  ],
  templateUrl: './ereader-access-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './ereader-access-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EreaderAccessSettingsComponent implements OnInit {
  private readonly opdsService = inject(OpdsService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  /** Cloud's managed reader credentials, instead of a private-network address. */
  readonly managed = input(false);

  /** A SelfHosted server whose address readers on the same network can reach. */
  readonly privateNetwork = input(false);

  /** E-reader access (issue #187): null until the server has answered. */
  readonly opds = signal<OpdsInfo | null>(null);

  /** True when the info request failed, so the card never presents a guess as fact. */
  readonly opdsFailed = signal(false);

  /** Hosted credential state. Plaintext password, when present, is one-time response material only. */
  readonly managedOpds = signal<ManagedOpdsAccess | null>(null);
  readonly managedOpdsFailed = signal(false);
  readonly managedOpdsBusy = signal<'enable' | 'rotate' | 'revoke' | null>(null);
  readonly pendingOpdsRevoke = signal(false);
  readonly pendingOpdsRotate = signal(false);

  readonly copied = signal(false);
  private copiedTimeout: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => this.clearCopiedTimeout());
    effect(() => {
      if (this.managed()) untracked(() => this.loadManagedOpdsAccess());
    });
  }

  ngOnInit(): void {
    this.loadOpdsInfo();
  }

  /** "Try again" after the e-reader access details failed to load. */
  retryEreaderAccess(): void {
    this.loadOpdsInfo();
    if (this.managed()) this.loadManagedOpdsAccess();
  }

  loadOpdsInfo(): void {
    this.opdsService.getInfo().subscribe({
      next: (info) => {
        this.opds.set(info);
        this.opdsFailed.set(false);
      },
      error: () => {
        this.opds.set(null);
        this.opdsFailed.set(true);
      },
    });
  }

  loadManagedOpdsAccess(): void {
    this.opdsService.getManagedAccess().subscribe({
      next: (access) => {
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
      },
      error: () => {
        this.managedOpds.set(null);
        this.managedOpdsFailed.set(true);
      },
    });
  }

  enableManagedOpds(): void {
    if (this.managedOpdsBusy() !== null) return;
    this.managedOpdsBusy.set('enable');
    this.opdsService.enableManagedAccess().subscribe({
      next: (access) => {
        this.managedOpdsBusy.set(null);
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
        this.toast.success('E-reader access enabled. Save the generated password now.');
      },
      error: () => {
        this.managedOpdsBusy.set(null);
        this.toast.error('Could not enable e-reader access');
      },
    });
  }

  rotateManagedOpdsPassword(): void {
    if (this.managedOpdsBusy() !== null) return;
    this.managedOpdsBusy.set('rotate');
    this.opdsService.rotateManagedPassword().subscribe({
      next: (access) => {
        this.managedOpdsBusy.set(null);
        this.pendingOpdsRotate.set(false);
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
        this.toast.success('E-reader password regenerated. The previous password no longer works.');
      },
      error: () => {
        this.managedOpdsBusy.set(null);
        this.toast.error('Could not regenerate the e-reader password');
      },
    });
  }

  requestManagedOpdsRevoke(): void {
    if (this.managedOpdsBusy() !== null) return;
    this.pendingOpdsRevoke.set(true);
  }

  cancelManagedOpdsRevoke(): void {
    if (this.managedOpdsBusy() === 'revoke') return;
    this.pendingOpdsRevoke.set(false);
  }

  /** Regenerating disconnects every reader on the old password, so it asks first. */
  requestManagedOpdsRotate(): void {
    if (this.managedOpdsBusy() !== null) return;
    this.pendingOpdsRotate.set(true);
  }

  cancelManagedOpdsRotate(): void {
    if (this.managedOpdsBusy() === 'rotate') return;
    this.pendingOpdsRotate.set(false);
  }

  confirmManagedOpdsRevoke(): void {
    if (!this.pendingOpdsRevoke() || this.managedOpdsBusy() !== null) return;
    this.managedOpdsBusy.set('revoke');
    this.opdsService.revokeManagedAccess().subscribe({
      next: (access) => {
        this.managedOpdsBusy.set(null);
        this.pendingOpdsRevoke.set(false);
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
        this.toast.success('E-reader access disabled');
      },
      error: () => {
        this.managedOpdsBusy.set(null);
        this.toast.error('Could not disable e-reader access');
      },
    });
  }

  /**
   * Copies the catalogue address. The clipboard API is only available in a
   * secure context (https, or localhost), so a failure is reported rather than
   * swallowed — the address on screen stays selectable as the fallback.
   */
  copyCatalogUrl(): void {
    const url = this.opds()?.catalogUrl;
    if (!url || !navigator.clipboard) {
      this.toast.error('Copying is not available here — select the address and copy it.');
      return;
    }

    navigator.clipboard.writeText(url).then(
      () => {
        this.markCopied();
        this.toast.success('Catalogue address copied');
      },
      () => {
        this.toast.error('Could not copy automatically — select the address and copy it.');
      },
    );
  }

  copyManagedOpdsConnectionDetails(): void {
    const info = this.opds();
    const access = this.managedOpds();
    if (
      !info?.catalogUrl ||
      !access?.enabled ||
      !access.username ||
      !access.password ||
      !navigator.clipboard
    ) {
      this.toast.error('Connection details are not available to copy');
      return;
    }

    const details = [
      `Catalogue: ${info.catalogUrl}`,
      `Username: ${access.username}`,
      `Password: ${access.password}`,
    ].join('\n');

    navigator.clipboard.writeText(details).then(
      () => {
        this.markCopied();
        this.toast.success('E-reader connection details copied');
      },
      () => this.toast.error('Could not copy the connection details automatically'),
    );
  }

  private markCopied(): void {
    this.copied.set(true);
    this.clearCopiedTimeout();
    this.copiedTimeout = setTimeout(() => {
      this.copied.set(false);
      this.copiedTimeout = null;
    }, COPIED_FEEDBACK_MS);
  }

  private clearCopiedTimeout(): void {
    if (this.copiedTimeout === null) return;
    clearTimeout(this.copiedTimeout);
    this.copiedTimeout = null;
  }
}
