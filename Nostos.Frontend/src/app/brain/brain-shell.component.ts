import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { PHONE_QUERY } from '../ui/breakpoints';
import { lockScrollWhile } from '../ui/scroll-lock/scroll-lock.service';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';

import { DrawerFocusDirective } from '../core/directives/drawer-focus.directive';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { BrainTopicPickerComponent } from './topic-picker/brain-topic-picker.component';
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
  imports: [DrawerFocusDirective, RouterOutlet, BrainSidebarComponent, ConfirmModal, ModalShell, BrainTopicPickerComponent],
  // The topic dialogs live and die with the Brain.
  providers: [BrainTopicActionsService],
  templateUrl: './brain-shell.component.html',
  styleUrls: ['./brain-shell.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrainShellComponent {
  readonly state = inject(BrainStateService);
  readonly actions = inject(BrainTopicActionsService);

  constructor() {
    // The phone drawer owns the screen while open.
    lockScrollWhile(() => this.state.drawerOpen(), PHONE_QUERY);
    // Notes change outside the Brain (the reader, a book page): coming in
    // rereads the counts and topics, and stales any list kept for Back.
    this.state.refresh();
    // Choosing a place closes the phone drawer.
    inject(Router).events
      .pipe(filter((event) => event instanceof NavigationEnd), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => this.state.drawerOpen.set(false));
  }

  closeDrawer(): void {
    this.state.drawerOpen.set(false);
  }
}
