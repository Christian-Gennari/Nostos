import { Component, computed, effect, inject, signal, ChangeDetectionStrategy } from '@angular/core';
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
import { CommandPaletteService } from './ui/command-palette/command-palette.service';
import { RecentItemsService } from './ui/command-palette/recent-items.service';
import { HighlightImportModal } from './ui/highlight-import-modal/highlight-import-modal.component';
import { AssistantComponent } from './ui/assistant/assistant.component';
import { AssistantService } from './ui/assistant/assistant.service';
import { AssistantLauncherService } from './ui/assistant/assistant-launcher.service';
import { HighlightImportService } from './core/services/highlight-import.service';
import { PassageViewerComponent } from './ui/passage-viewer/passage-viewer.component';
import { PassageViewerService } from './ui/passage-viewer/passage-viewer.service';
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
    PassageViewerComponent,
    AssistantComponent,
    CloudEntryComponent,
    LibraryActivationOverlayComponent,
    LibraryTransferIndicatorComponent,
  ],
  templateUrl: './app.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './app.component.css',
})
export class App {
  private readonly router = inject(Router);
  readonly navigationPending = signal(false);
  readonly cloudEntry = inject(CloudEntryService);
  /** Root-scoped cutover state; the overlay blocks the app while it runs. */
  readonly activation = inject(LibraryActivationController);
  /** Opens the deferred command palette; see app.component.html. */
  readonly palette = inject(CommandPaletteService);
  readonly passageViewer = inject(PassageViewerService);
  readonly highlightImport = inject(HighlightImportService);
  private readonly assistant = inject(AssistantService);
  private readonly assistantLauncher = inject(AssistantLauncherService);
  /** True once the first navigation has rendered, so the dock's presence is known. */
  private readonly routed = signal(false);

  /** When the deferred Ask Nostos shell must load before the browser is idle. */
  readonly assistantWanted = computed(
    () =>
      this.assistant.surfaceWanted() ||
      this.assistant.isOpen() ||
      // A route without the dock shows the shell's own launcher capsule.
      (this.routed() && this.assistantLauncher.available() && !this.assistantLauncher.dockHosted()),
  );

  constructor() {
    // Applies the persisted theme immediately. `index.html` already set the
    // attribute before first paint; this reconciles the signal with the DOM and
    // owns the attribute for the rest of the session.
    inject(ThemeService).active();

    // Keeps the service worker's navigation manifest fresh, so a deploy that
    // changes how URLs are served (the app shell vs. the API) reaches an
    // already-open client without a manual reload.
    inject(SwUpdateService).start();
    // Host browser integrations also run on entry/recovery screens. They
    // never participate in product readiness or entitlement decisions.
    inject(HostedBrowserIntegrationService).start();
    // Cmd/Ctrl+K and the palette's Recent list both work before the palette
    // itself has loaded (it is deferred until first opened).
    this.palette.start();
    // Likewise Cmd/Ctrl+J before the deferred Ask Nostos shell has loaded.
    this.assistantLauncher.start();
    inject(RecentItemsService).watch(this.router);
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
      if (event instanceof NavigationEnd) this.routed.set(true);
    });
  }
}
