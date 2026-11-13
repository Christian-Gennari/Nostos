import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { ModalShell } from '../../ui/modal-shell/modal-shell.component';
import { MoveRow } from './move-writing.helpers';

@Component({
  selector: 'app-move-writing-dialog',
  standalone: true,
  imports: [ModalShell, ButtonComponent, NostosIconComponent, LoadingIndicatorComponent],
  templateUrl: './move-writing-dialog.component.html',
  styleUrl: './move-writing-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MoveWritingDialogComponent {
  readonly isOpen = input(false);
  readonly documentName = input('');
  readonly locationLabel = input('');
  readonly rows = input<readonly MoveRow[]>([]);
  readonly busy = input(false);
  readonly pendingKey = input<string | null>(null);
  readonly hasDestination = input(false);
  readonly initialFocusKey = input<string | null>(null);

  readonly closed = output<void>();
  readonly moveSelected = output<string | null>();

  close(): void {
    if (!this.busy()) {
      this.closed.emit();
    }
  }

  chooseMove(row: MoveRow): void {
    if (this.busy() || row.current) return;
    this.moveSelected.emit(row.parentId);
  }

  onMoveRowKeydown(event: KeyboardEvent, row: MoveRow): void {
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    const selectable = this.rows().filter((candidate) => !candidate.current);
    if (selectable.length === 0) return;

    let next: MoveRow;
    if (event.key === 'Home') {
      next = selectable[0];
    } else if (event.key === 'End') {
      next = selectable[selectable.length - 1];
    } else {
      const at = selectable.findIndex((candidate) => candidate.key === row.key);
      const step = event.key === 'ArrowDown' ? 1 : -1;
      let index: number;
      if (at === -1) index = event.key === 'ArrowDown' ? 0 : selectable.length - 1;
      else index = (at + step + selectable.length) % selectable.length;
      next = selectable[index];
    }

    event.preventDefault();
    const list = (event.currentTarget as HTMLElement).closest('.move-list');
    const target = Array.from(
      list?.querySelectorAll<HTMLButtonElement>('[data-move-row]') ?? [],
    ).find((button) => button.dataset['moveRow'] === next.key);
    target?.focus();
  }
}
