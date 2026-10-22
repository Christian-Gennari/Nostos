import { buildFlatTree } from '../../ui/flat-tree/flat-tree.helper';

/** A writing as the Studio's tree lists it. */
export interface ExportTreeItem {
  id: string;
  name: string;
  type: 'Folder' | 'Document';
  parentId?: string | null;
}

/** A document of an exported folder, with the folders it sits in below that one. */
export interface ExportChapterRef {
  id: string;
  title: string;
  groupPath: { id: string; title: string }[];
}

/**
 * The documents a folder export holds, in the Studio's tree order (issue
 * #1164): folders before documents, each by name, nested folders flattened
 * depth first. The same ordering the file tree draws, from the same helper.
 * Exporting a document gives that one document.
 */
export function exportChapters(items: readonly ExportTreeItem[], rootId: string): ExportChapterRef[] {
  const root = items.find((item) => item.id === rootId);
  if (!root) return [];
  if (root.type === 'Document') return [{ id: root.id, title: root.name, groupPath: [] }];

  const allFolders = new Set(items.filter((item) => item.type === 'Folder').map((item) => item.id));
  const nodes = buildFlatTree([...items], allFolders);
  const start = nodes.findIndex((node) => node.id === rootId);
  if (start < 0) return [];
  const rootLevel = nodes[start].level;
  const chapters: ExportChapterRef[] = [];
  const path: { id: string; title: string }[] = [];
  for (let i = start + 1; i < nodes.length && nodes[i].level > rootLevel; i++) {
    const node = nodes[i];
    const depth = node.level - rootLevel - 1;
    path.length = depth;
    if (node.type === 'Folder') path.push({ id: node.id, title: node.name });
    else chapters.push({ id: node.id, title: node.name, groupPath: [...path] });
  }
  return chapters;
}

/** The folders a writing sits in, nearest first: what else an export from it could take in. */
export function ancestorFolders(items: readonly ExportTreeItem[], id: string): ExportTreeItem[] {
  const byId = new Map(items.map((item) => [item.id, item]));
  const out: ExportTreeItem[] = [];
  const seen = new Set<string>([id]);
  let parent = byId.get(id)?.parentId ?? null;
  while (parent && !seen.has(parent)) {
    seen.add(parent);
    const folder = byId.get(parent);
    if (!folder) break;
    out.push(folder);
    parent = folder.parentId ?? null;
  }
  return out;
}
