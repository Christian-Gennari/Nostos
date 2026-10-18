export interface WritingDto {
  id: string;
  name: string;
  type: 'Folder' | 'Document';
  parentId?: string | null;
  updatedAt: string;
}

export interface WritingContentDto {
  id: string;
  name: string;
  content: string;
  updatedAt: string;
}

export interface CreateWritingDto {
  name: string;
  type: 'Folder' | 'Document';
  parentId?: string | null;
}

export interface UpdateWritingDto {
  name: string;
  content?: string;
}

export interface MoveWritingDto {
  newParentId?: string | null;
}

export interface AddWritingSourceDto {
  noteId: string;
}

export interface WritingSourceDto {
  id: string;
  bookId: string;
  bookTitle?: string | null;
  content: string;
  selectedText?: string | null;
  cfiRange?: string | null;
  createdAt: string;
  addedAt: string;
  sourceAnchorKind?: string;
  sourceAnchorValue?: string | null;
  anchorVerified?: boolean;
  /** A passage (issue #1014); `selectedText` is then only its opening. */
  kind?: 'note' | 'passage';
  passageTitle?: string | null;
  passageEnd?: string | null;
  passageWordCount?: number | null;
  /** The book's author, when known (issue #1038). */
  bookAuthor?: string | null;
}

