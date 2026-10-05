/**
 * Whole-application interaction boundary while the server replaces the
 * library (slice B8, review-748).
 *
 * Activation is root-scoped and continues after leaving Settings, so the flow
 * dialog alone cannot protect the rest of the app. This overlay is mounted by
 * the application shell and blocks pointer and keyboard interaction until the
 * server owns the cutover finishes; it has no dismiss affordance by design.
 * On completion the controller reloads the page into the new generation.
 */

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';

import {
  HostActivationState,
} from '../models/library-transfer.models';
import { MigrationActivationPhase } from '../models/migration-http.dtos';
import { activationPhaseMessage } from '../library-transfer.copy';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';

@Component({
  selector: 'app-library-activation-overlay',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [A11yModule, NostosIconComponent],
  templateUrl: './library-activation-overlay.component.html',
  styleUrl: './library-activation-overlay.component.css',
})
export class LibraryActivationOverlayComponent {
  readonly state = input<HostActivationState>('idle');
  readonly phase = input<MigrationActivationPhase | null>(null);

  readonly visible = computed(() => this.state() === 'in-progress');
  readonly phaseMessage = computed(() => activationPhaseMessage(this.phase()));
}
