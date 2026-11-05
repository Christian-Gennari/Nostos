import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { BrainWritingHandoffComponent } from './writing-handoff/brain-writing-handoff.component';
import { BrainNoteActionsService } from './brain-note-actions.service';

/** The dialogs a page's note actions open: delete, keep with a writing, and which version to keep when a save finds the note changed. */
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
    <!-- Saved from another tab or device since this copy was loaded (issue #1404). -->
    <app-confirm-modal
      [isOpen]="!!actions.editConflict()"
      heading="This note changed somewhere else"
      description="A newer version was saved from another tab or device. Your words stay in the form until you choose."
      confirmLabel="Keep mine (overwrite)"
      cancelLabel="Load the newer version"
      tone="neutral"
      (confirm)="actions.keepMineAfterConflict()"
      (cancel)="actions.loadNewerAfterConflict()"
    ></app-confirm-modal>
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
