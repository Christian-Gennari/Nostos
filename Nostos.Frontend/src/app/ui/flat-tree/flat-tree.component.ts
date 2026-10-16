import {
  Component,
  input,
  output,
  signal,
  computed,
  effect,
  viewChild,
  inject,
  ElementRef,
  ChangeDetectionStrategy,
} from '@angular/core';
import { DragDropModule, CdkDragMove } from '@angular/cdk/drag-drop';
import { buildFlatTree, FlatTreeNode, TreeItem } from './flat-tree.helper';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { InputDirective } from '../form-control/form-control.directive';

export interface DropIndicator {
  nodeId: string;
  zone: 'inside';
}

@Component({
  selector: 'app-flat-tree',
  imports: [IconButtonComponent, InputDirective, DragDropModule, NostosIconComponent],
  templateUrl: './flat-tree.component.html',
  styleUrls: ['./flat-tree.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FlatTreeComponent {
  private readonly elRef = inject(ElementRef);

  readonly items = input.required<TreeItem[]>();
  readonly activeId = input<string | null>(null);
  readonly treatAllAsFolders = input(false);
  readonly editingId = input<string | null>(null);
  readonly emptyLabel = input('Nothing here yet');

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

  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('renameInput');

  constructor() {
    effect(() => {
      const el = this.renameInput();
      if (el) {
        el.nativeElement.focus();
        el.nativeElement.select();
      }
    });
  }

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

  onRenameSave(id: string, event: Event): void {
    const el = event.target as HTMLInputElement;
    this.nodeRenameSaved.emit({ id, newName: el.value });
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
