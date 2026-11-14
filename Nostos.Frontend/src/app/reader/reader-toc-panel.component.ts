import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ButtonComponent } from '../ui/button/button.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import type { TocItem } from './reader.interface';

/**
 * The book's contents drawer. It lists the reader's own TOC, marks where the
 * reader is, and turns the book to the row that is chosen.
 *
 * It is a presentation of the shell's `toc()` and `isActive()` (issue #1607):
 * the mounted reader owns the outline, so the component takes both as inputs
 * and emits the chosen row.
 */
@Component({
  selector: 'app-reader-toc-panel',
  standalone: true,
  imports: [ButtonComponent, EmptyStateComponent, IconButtonComponent, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './reader-toc-panel.component.html',
  styleUrl: './reader-toc-panel.component.css',
  host: { style: 'display: contents' },
})
export class ReaderTocPanel {
  /** Whether the drawer is on screen. PDF uses its own sidebar, so the shell
   * also passes the open format and the drawer stays shut for it. */
  readonly open = input(false);
  readonly fileType = input<'pdf' | 'epub' | 'audio' | null>(null);
  readonly items = input<readonly TocItem[]>([]);
  /** The location the reader is at; the row whose target matches is active. */
  readonly activeTarget = input<string | number | null>(null);

  readonly closed = output<void>();
  readonly itemSelected = output<TocItem>();

  isActive(item: TocItem): boolean {
    const active = this.activeTarget();
    return active != null && item.target === active;
  }
}
