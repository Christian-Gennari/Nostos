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

import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';

import {
  HostActivationState,
} from '../models/library-transfer.models';
import { MigrationActivationPhase } from '../models/migration-http.dtos';
import { activationPhaseMessage } from '../library-transfer.copy';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';

@Component({
  selector: 'app-library-activation-overlay',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LoadingIndicatorComponent, A11yModule],
  templateUrl: './library-activation-overlay.component.html',
  styleUrl: './library-activation-overlay.component.css',
})
export class LibraryActivationOverlayComponent {
  readonly state = input<HostActivationState>('idle');
  readonly phase = input<MigrationActivationPhase | null>(null);

  readonly visible = computed(() => this.state() === 'in-progress');
  readonly phaseMessage = computed(() => activationPhaseMessage(this.phase()));

  constructor() {
    // The boundary must also block the keyboard shortcuts that open app-level
    // surfaces (the command palette, Ask Nostos) and the Escape that would
    // close what is behind it. Those listen on the document; this runs first,
    // in the capture phase on the window, and stops them from seeing the key.
    const doc = inject(DOCUMENT);
    const block = (event: KeyboardEvent): void => {
      const chord = (event.ctrlKey || event.metaKey) && ['k', 'j'].includes(event.key.toLowerCase());
      if (!chord && event.key !== 'Escape') return;
      event.preventDefault();
      event.stopImmediatePropagation();
    };
    effect((onCleanup) => {
      if (!this.visible()) return;
      const view = doc.defaultView;
      view?.addEventListener('keydown', block, true);
      onCleanup(() => view?.removeEventListener('keydown', block, true));
    });
  }
}
