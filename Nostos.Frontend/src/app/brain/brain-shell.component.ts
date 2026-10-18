import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';

import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { BrainTopicPickerComponent } from '../second-brain/topic-picker/brain-topic-picker.component';
import { BrainSidebarComponent } from './brain-sidebar.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';

/**
 * The Brain's frame, the Library's shape: the sidebar of places and topics on
 * the left, the page on the right. On a phone the sidebar is a drawer the
 * page's title opens, as the Library's is. The topic dialogs (merge, delete)
 * live here once, whichever page or row asked for them.
 */
@Component({
  selector: 'app-brain-shell',
  standalone: true,
  imports: [RouterOutlet, BrainSidebarComponent, ConfirmModal, ModalShell, BrainTopicPickerComponent],
  templateUrl: './brain-shell.component.html',
  styleUrls: ['./brain-shell.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainShellComponent {
  readonly state = inject(BrainStateService);
  readonly actions = inject(BrainTopicActionsService);

  constructor() {
    this.state.ensureLoaded();
    // Choosing a place closes the phone drawer.
    inject(Router).events
      .pipe(filter((event) => event instanceof NavigationEnd), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => this.state.drawerOpen.set(false));
  }

  closeDrawer(): void {
    this.state.drawerOpen.set(false);
  }
}
