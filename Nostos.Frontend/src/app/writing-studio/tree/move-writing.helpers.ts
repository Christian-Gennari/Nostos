import { WritingDto } from '../../core/dtos/writing.dtos';
import { buildFlatTree } from '../../ui/flat-tree/flat-tree.helper';

/** One destination in the "Move to…" dialog: Top level or a folder. */
export interface MoveRow {
  /** Stable row id for focus bookkeeping; a folder id, prefixed. */
  key: string;
  /** The parent id the move request takes; null for Top level. */
  parentId: string | null;
  name: string;
  /** Nesting depth, for the row's indent. Top level and root folders are 0. */
  level: number;
  /** This is where the open writing already is, so choosing it is a no-op. */
  current: boolean;
}

export interface DeleteWritingCopy {
  heading: string;
  description: string;
  confirmLabel: string;
  name: string | null;
}

/** A row's own key, matching the `key` on its MoveRow. */
export function moveRowKey(parentId: string | null): string {
  return parentId === null ? 'top-level' : `folder:${parentId}`;
}

export function buildMoveRows(
  rootItems: readonly WritingDto[],
  activeWritingId: string | null | undefined,
): MoveRow[] {
  const currentParent =
    (activeWritingId
      ? rootItems.find((item) => item.id === activeWritingId)?.parentId
      : undefined) ?? null;
  const folders = rootItems.filter((item) => item.type === 'Folder');
  const flatFolders = buildFlatTree(folders, new Set(folders.map((folder) => folder.id)));

  return [
    {
      key: 'top-level',
      parentId: null,
      name: 'Top level',
      level: 0,
      current: currentParent === null,
    },
    ...flatFolders.map((folder) => ({
      key: `folder:${folder.id}`,
      parentId: folder.id,
      name: folder.name,
      level: folder.level,
      current: currentParent === folder.id,
    })),
  ];
}

export function moveLocationLabel(
  currentParentId: string | null,
  rootItems: readonly WritingDto[],
): string {
  if (currentParentId === null) return 'at the top level';
  const folder = rootItems.find((item) => item.id === currentParentId);
  return folder ? `in “${folder.name}”` : 'in a folder';
}

export function buildDeleteWritingCopy(
  pendingId: string | null,
  rootItems: readonly WritingDto[],
): DeleteWritingCopy {
  const item = pendingId ? rootItems.find((candidate) => candidate.id === pendingId) : undefined;
  const isFolder = item?.type === 'Folder';
  const noun = isFolder ? 'folder' : 'document';
  const name = item?.name?.trim() || null;
  return {
    heading: name ? `Delete “${name}”?` : `Delete this ${noun}?`,
    description: isFolder
      ? 'The folder and everything inside it is removed. This cannot be undone.'
      : 'The document is removed. This cannot be undone.',
    confirmLabel: `Delete ${noun}`,
    name,
  };
}
