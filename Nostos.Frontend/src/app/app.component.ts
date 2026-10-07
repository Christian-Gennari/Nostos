import { Component, effect, inject, signal } from '@angular/core';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  NavigationStart,
  Router,
  RouterOutlet,
} from '@angular/router';
import { ToastContainerComponent } from './ui/toast-container/toast-container.component';
import { CommandPalette } from './ui/command-palette/command-palette.component';
import { HighlightImportModal } from './ui/highlight-import-modal/highlight-import-modal.component';
import { AssistantComponent } from './ui/assistant/assistant.component';
import { SwUpdateService } from './core/services/sw-update.service';
import { ThemeService } from './core/services/theme.service';
import { CloudEntryService } from './core/services/cloud-entry.service';
import { CloudEntryComponent } from './cloud-entry/cloud-entry.component';
import { HostedBrowserIntegrationService } from './core/services/hosted-browser-integration.service';
import { LibraryActivationOverlayComponent } from './library-transfer/components/library-activation-overlay.component';
import { LibraryTransferIndicatorComponent } from './library-transfer/components/library-transfer-indicator.component';
import { LibraryActivationController } from './library-transfer/services/library-activation-controller.service';

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    ToastContainerComponent,
    CommandPalette,
    HighlightImportModal,
    AssistantComponent,
    CloudEntryComponent,
    LibraryActivationOverlayComponent,
    LibraryTransferIndicatorComponent,
  ],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class App {
  private readonly router = inject(Router);
  readonly navigationPending = signal(false);
  readonly cloudEntry = inject(CloudEntryService);
  /** Root-scoped cutover state; the overlay blocks the app while it runs. */
  readonly activation = inject(LibraryActivationController);

  constructor() {
    // Applies the persisted theme immediately. `index.html` already set the
    // attribute before first paint; this reconciles the signal with the DOM and
    // owns the attribute for the rest of the session.
    inject(ThemeService).theme();

    // Keeps the service worker's navigation manifest fresh, so a deploy that
    // changes how URLs are served (the app shell vs. the API) reaches an
    // already-open client without a manual reload.
    inject(SwUpdateService).start();
    // Host browser integrations also run on entry/recovery screens. They
    // never participate in product readiness or entitlement decisions.
    inject(HostedBrowserIntegrationService).start();
    void this.cloudEntry.initialize();

    effect(() => {
      if (!this.cloudEntry.supportsSafeActivation()) return;
      this.activation.reattach();
    });

    this.router.events.subscribe((event) => {
      if (event instanceof NavigationStart) this.navigationPending.set(true);
      if (
        event instanceof NavigationEnd ||
        event instanceof NavigationCancel ||
        event instanceof NavigationError
      ) {
        this.navigationPending.set(false);
      }
    });
  }
}
