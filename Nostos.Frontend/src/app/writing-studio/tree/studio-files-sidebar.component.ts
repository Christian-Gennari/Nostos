import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { WritingDto } from '../../core/dtos/writing.dtos';
import { ButtonComponent } from '../../ui/button/button.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { FlatTreeComponent } from '../../ui/flat-tree/flat-tree.component';
import { TreeItem } from '../../ui/flat-tree/flat-tree.helper';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';

@Component({
  selector: 'app-studio-files-sidebar',
  standalone: true,
  imports: [
    ButtonComponent,
    IconButtonComponent,
    NostosIconComponent,
    EmptyStateComponent,
    FlatTreeComponent,
  ],
  templateUrl: './studio-files-sidebar.component.html',
  styleUrl: './studio-files-sidebar.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StudioFilesSidebarComponent {
  readonly items = input.required<WritingDto[]>();
  readonly activeId = input<string | null>(null);
  readonly editingId = input<string | null>(null);
  readonly renameError = input<string | null>(null);
  readonly renameBusy = input(false);
  readonly treeFailed = input(false);
  readonly isDrawerMode = input(true);
  readonly isOpen = input(false);
  readonly railLabel = input('Show files');

  readonly createDocument = output<void>();
  readonly createFolder = output<void>();
  readonly collapseRail = output<void>();
  readonly retryLoad = output<void>();
  readonly nodeSelected = output<TreeItem>();
  readonly nodeMoved = output<{ item: TreeItem; newParentId: string | null }>();
  readonly nodeRenamed = output<TreeItem>();
  readonly nodeDeleted = output<string>();
  readonly nodeRenameSaved = output<{ id: string; newName: string }>();
  readonly nodeRenameCancelled = output<void>();
}
