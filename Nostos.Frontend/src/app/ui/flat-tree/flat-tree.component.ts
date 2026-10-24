import {
  Component,
  input,
  output,
  signal,
  computed,
  inject,
  ElementRef,
  ChangeDetectionStrategy,
} from '@angular/core';
import { DragDropModule, CdkDragMove } from '@angular/cdk/drag-drop';
import { buildFlatTree, FlatTreeNode, TreeItem } from './flat-tree.helper';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { InlineRenameComponent } from '../inline-rename/inline-rename.component';

export interface DropIndicator {
  nodeId: string;
  zone: 'inside';
}

@Component({
  selector: 'app-flat-tree',
  imports: [IconButtonComponent, InlineRenameComponent, DragDropModule, NostosIconComponent],
  templateUrl: './flat-tree.component.html',
  styleUrls: ['./flat-tree.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.flat-tree--quiet]': "appearance() === 'quiet'" },
})
export class FlatTreeComponent {
  private readonly elRef = inject(ElementRef);

  readonly items = input.required<TreeItem[]>();
  readonly activeId = input<string | null>(null);
  readonly treatAllAsFolders = input(false);
  readonly editingId = input<string | null>(null);
  /** The owner's refusal of the last rename (the server said no). */
  readonly renameError = input<string | null>(null);
  /** A rename is being saved: the field holds its text and waits. */
  readonly renameBusy = input(false);
  /**
   * Refuse a name a sibling already has, before it is sent. On for the
   * Library's collections (the server refuses them too); off for Studio's
   * documents, where two drafts may share a name.
   */
  readonly rejectDuplicateNames = input(false);
  readonly emptyLabel = input('Nothing here yet');
  /**
   * `filled` marks the selected row with the solid primary pill (Studio's file
   * tree). `quiet` is for a tree sitting on the page ground beside other nav
   * rows (the Library's collections): the selected row takes the soft hover
   * tint, and counts are plain numbers rather than chips.
   */
  readonly appearance = input<'filled' | 'quiet'>('filled');

  readonly nodeSelected = output<TreeItem>();
  readonly nodeMoved = output<{ item: TreeItem; newParentId: string | null }>();
  readonly nodeRenamed = output<TreeItem>();
  readonly nodeDeleted = output<string>();
  readonly nodeRenameSaved = output<{ id: string; newName: string }>();
  readonly nodeRenameCancelled = output<void>();
  readonly expandedIds = signal<Set<string>>(new Set());
  readonly dropIndicator = signal<DropIndicator | null>(null);
  readonly isDragging = signal(false);

  private draggedNode: FlatTreeNode | null = null;
  private wasExpanded = false;

  /** The duplicate check for the row being renamed, when the owner wants one. */
  readonly renameValidate = computed(() => {
    const id = this.editingId();
    if (!id || !this.rejectDuplicateNames()) return null;
    const items = this.items();
    const parentId = items.find((item) => item.id === id)?.parentId ?? null;
    const taken = new Set(
      items
        .filter((item) => item.id !== id && (item.parentId ?? null) === parentId)
        .map((item) => item.name.trim().toLocaleLowerCase()),
    );
    return (name: string) =>
      taken.has(name.toLocaleLowerCase()) ? `“${name}” is already used here.` : null;
  });

  readonly treeNodes = computed(() =>
    buildFlatTree(this.items(), this.expandedIds(), this.treatAllAsFolders()),
  );

  private toggleExpandState(nodeId: string): void {
    this.expandedIds.update((set) => {
      const next = new Set(set);
      if (next.has(nodeId)) next.delete(nodeId);
      else next.add(nodeId);
      return next;
    });
  }

  toggleExpand(event: MouseEvent, node: FlatTreeNode): void {
    event.stopPropagation();
    if (!node.expandable) return;
    this.toggleExpandState(node.id);
  }

  handleSelect(node: FlatTreeNode): void {
    this.nodeSelected.emit(node.originalData);
  }

  moveToRoot(node: FlatTreeNode, event: Event): void {
    event.stopPropagation();
    this.nodeMoved.emit({ item: node.originalData, newParentId: null });
  }

  // ── Drag & Drop ──────────────────────────────────────────────

  onDragStarted(node: FlatTreeNode): void {
    this.draggedNode = node;
    this.isDragging.set(true);

    // Collapse expanded folder so children visually travel with it
    if (node.isExpanded) {
      this.wasExpanded = true;
      this.expandedIds.update((set) => {
        const next = new Set(set);
        next.delete(node.id);
        return next;
      });
    } else {
      this.wasExpanded = false;
    }
  }

  onDragMoved(event: CdkDragMove): void {
    if (!this.draggedNode) return;

    const pointerY = event.pointerPosition.y;
    const rows: NodeListOf<HTMLElement> = this.elRef.nativeElement.querySelectorAll(
      '.tree-row:not(.cdk-drag-placeholder)',
    );

    let indicator: DropIndicator | null = null;

    for (const row of rows) {
      const rect = row.getBoundingClientRect();
      if (pointerY < rect.top || pointerY > rect.bottom) continue;

      const nodeId = row.dataset['nodeId'];
      if (!nodeId || nodeId === this.draggedNode.id) break;

      const node = this.treeNodes().find((n) => n.id === nodeId);
      if (!node) break;

      // Honest drop model: only "drop inside a folder" is offered. No above/below
      // insertion zones; siblings stay alphabetically sorted.
      if (node.type === 'Folder') {
        indicator = { nodeId, zone: 'inside' };

        // Validate: reject no-ops and circular nesting
        if (
          this.draggedNode.parentId === nodeId ||
          this.isDescendantOf(nodeId, this.draggedNode.id)
        ) {
          indicator = null;
        }
      }

      break;
    }

    // Below all rows → root drop zone
    if (!indicator && rows.length > 0 && this.draggedNode.parentId !== null) {
      const lastRow = rows[rows.length - 1];
      const lastRect = lastRow.getBoundingClientRect();
      if (pointerY > lastRect.bottom) {
        indicator = { nodeId: '__root__', zone: 'inside' };
      }
    }

    this.dropIndicator.set(indicator);
  }

  onDragEnded(): void {
    const dragged = this.draggedNode;
    const indicator = this.dropIndicator();

    if (dragged && indicator) {
      this.performDrop(dragged, indicator);
    } else if (dragged && this.wasExpanded) {
      // Restore expanded state for cancelled drag
      this.expandedIds.update((set) => {
        const s = new Set(set);
        s.add(dragged.id);
        return s;
      });
    }

    this.draggedNode = null;
    this.isDragging.set(false);
    this.dropIndicator.set(null);
    this.wasExpanded = false;
  }

  private performDrop(dragged: FlatTreeNode, indicator: DropIndicator): void {
    // Root drop zone
    if (indicator.nodeId === '__root__') {
      if (dragged.parentId !== null) {
        this.nodeMoved.emit({ item: dragged.originalData, newParentId: null });
      }
      return;
    }

    const targetNode = this.treeNodes().find((n) => n.id === indicator.nodeId);
    if (!targetNode) return;

    const newParentId = targetNode.id;
    this.expandedIds.update((set) => {
      const s = new Set(set);
      s.add(targetNode.id);
      return s;
    });

    if (dragged.parentId === newParentId) return;
    if (this.isDescendantOf(newParentId, dragged.id)) {
      return;
    }

    this.nodeMoved.emit({ item: dragged.originalData, newParentId });
  }

  private isDescendantOf(nodeId: string, potentialAncestorId: string): boolean {
    if (nodeId === potentialAncestorId) return true;
    const allItems = this.items();
    let currentId: string | null = nodeId;
    while (currentId !== null) {
      const item = allItems.find((i) => i.id === currentId);
      if (!item) return false;
      const pid = item.parentId ?? null;
      if (pid === potentialAncestorId) return true;
      currentId = pid;
    }
    return false;
  }

  // ── Keyboard Navigation ──────────────────────────────────────

  onKeydown(event: KeyboardEvent, node: FlatTreeNode): void {
    // Only keys aimed at the row itself. Keys typed into the inline rename
    // field bubble up here too: Space used to be swallowed (a name could not
    // contain one), Enter selected the row on top of saving the rename, and
    // the arrow keys folded folders while moving the caret.
    if (event.target !== event.currentTarget) return;
    switch (event.key) {
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.handleSelect(node);
        break;
      case 'ArrowRight':
        if (node.expandable && !node.isExpanded) {
          event.preventDefault();
          this.toggleExpandState(node.id);
        }
        break;
      case 'ArrowLeft':
        if (node.expandable && node.isExpanded) {
          event.preventDefault();
          this.toggleExpandState(node.id);
        }
        break;
    }
  }
}
