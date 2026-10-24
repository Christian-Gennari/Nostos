import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { BrainWritingHandoffComponent } from './writing-handoff/brain-writing-handoff.component';
import { BrainNoteActionsService } from './brain-note-actions.service';

/** The dialogs a page's note actions open: delete, and keep with a writing. */
@Component({
  selector: 'app-brain-note-dialogs',
  standalone: true,
  imports: [ConfirmModal, BrainWritingHandoffComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (actions.handoffIds().length) {
    <app-brain-writing-handoff
      [noteIds]="actions.handoffIds()"
      (cancelled)="actions.closeHandoff()"
      (completed)="actions.closeHandoff()"
    />
    }
    <app-confirm-modal
      [isOpen]="actions.deleteIds().length > 0"
      [heading]="actions.deleteHeading()"
      [description]="actions.deleteDescription()"
      [confirmLabel]="actions.deleteConfirmLabel()"
      busyLabel="Deleting…"
      [busy]="actions.deleting()"
      (confirm)="actions.confirmDelete()"
      (cancel)="actions.cancelDelete()"
    ></app-confirm-modal>
  `,
})
export class BrainNoteDialogsComponent {
  readonly actions = inject(BrainNoteActionsService);
}
