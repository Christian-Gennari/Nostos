import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';

import { CloudSession } from '../../core/dtos/cloud-auth.dtos';
import { CloudAccountDeletionStatus } from '../../core/dtos/cloud-account-deletion.dtos';
import { CloudAuthService } from '../../core/services/cloud-auth.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { CloudAccountDeletionSettingsComponent } from '../cloud-account-deletion-settings.component';

/**
 * Settings → Account: the Nostos Cloud sign-in, the account-site links, and the
 * danger zone.
 *
 * The section exists only on Cloud; the identity comes from the BFF session and
 * signing out goes back through it.
 */
@Component({
  selector: 'app-cloud-account-settings',
  standalone: true,
  imports: [ButtonComponent, CloudAccountDeletionSettingsComponent],
  templateUrl: './cloud-account-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './cloud-account-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CloudAccountSettingsComponent implements OnInit {
  private readonly cloudAuth = inject(CloudAuthService);

  /** The account site, or null when this host has none. */
  readonly accountManagementUrl = input<string | null>(null);

  /** The Cloud host advertises account deletion. */
  readonly supportsAccountDeletion = input(false);

  /** Deletion was started: the page shows where it is up to. */
  readonly deletionPending = output<CloudAccountDeletionStatus>();

  readonly cloudSession = signal<CloudSession | null>(null);

  ngOnInit(): void {
    this.cloudAuth.getSession().subscribe({
      next: (session) => this.cloudSession.set(session),
      error: () => this.cloudSession.set(null),
    });
  }

  signOut(): void {
    void this.cloudAuth.logout();
  }
}
