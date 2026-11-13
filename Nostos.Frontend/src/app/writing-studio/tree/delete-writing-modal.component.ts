import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { ConfirmModal } from '../../ui/confirm-modal/confirm-modal.component';
import { DeleteWritingCopy } from './move-writing.helpers';

@Component({
  selector: 'app-delete-writing-modal',
  standalone: true,
  imports: [ConfirmModal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-confirm-modal
      [isOpen]="isOpen()"
      [heading]="copy().heading"
      [description]="copy().description"
      [confirmLabel]="copy().confirmLabel"
      busyLabel="Deleting…"
      [busy]="busy()"
      (confirm)="confirmed.emit()"
      (cancel)="cancelled.emit()"
    />
  `,
})
export class DeleteWritingModalComponent {
  readonly isOpen = input(false);
  readonly copy = input.required<DeleteWritingCopy>();
  readonly busy = input(false);

  readonly confirmed = output<void>();
  readonly cancelled = output<void>();
}
