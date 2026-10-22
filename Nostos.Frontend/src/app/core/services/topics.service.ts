import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import type { TopicLinkResult } from '../dtos/note.dtos';

export interface TopicDto {
  id: string;
  name: string;
  usageCount: number;
  // Appended for the index search (issue #158). The plain list has no note text,
  // so a term that only appears inside a note can only be matched on the server;
  // these carry what matched, and are absent on the unfiltered list.
  noteMatchCount?: number;
  noteMatchSnippet?: string | null;
  /** Appended: the reader's own words about the topic, if any. */
  description?: string | null;
}

export interface RelatedTopicDto {
  id: string;
  name: string;
  sharedNotes: number;
  /** Exact notes that make the structural co-occurrence inspectable. */
  sharedNoteIds?: string[];
}

export interface TopicGraphNodeDto {
  id: string;
  name: string;
  usageCount: number;
}

export interface TopicGraphEdgeDto {
  sourceId: string;
  targetId: string;
  sharedNotes: number;
}

export interface TopicGraphDto {
  nodes: TopicGraphNodeDto[];
  edges: TopicGraphEdgeDto[];
}

export interface NoteContextDto {
  noteId: string;
  content: string;
  selectedText?: string;
  cfiRange?: string;
  bookId: string;
  bookTitle: string;
  // API responses include this; optional keeps existing local fixtures
  // compatible until the detail-surface tests add their timestamp data.
  createdAt?: string;
  sourceAnchorKind?: string;
  sourceAnchorValue?: string | null;
  anchorVerified?: boolean;
  /** The note's highlighter pen, by name; absent when none was recorded. */
  highlightColour?: string | null;
  /** A passage (issue #1014); `selectedText` is then only its opening. */
  kind?: 'note' | 'passage';
  passageTitle?: string | null;
  passageEnd?: string | null;
  passageWordCount?: number | null;
  /** The book's author, when known (issue #1038). */
  bookAuthor?: string | null;
}

export interface TopicDetailDto {
  id: string;
  name: string;
  notes: NoteContextDto[];
}

/** A writing that cites one of a topic's notes (issue #1162). */
export interface TopicWritingDto {
  id: string;
  name: string;
  updatedAt: string;
}

/** How many writings cite one of a topic's notes. */
export interface NoteCitationCountDto {
  noteId: string;
  writings: number;
}

/**
 * Where a topic is used in writing, joined at read time from what writings
 * cite: the newest few, how many in all, and a count per cited note.
 */
export interface TopicWritingUsageDto {
  total: number;
  writings: TopicWritingDto[];
  noteCitations: NoteCitationCountDto[];
}

@Injectable({ providedIn: 'root' })
export class TopicsService {
  private http = inject(HttpClient);

  list(): Observable<TopicDto[]> {
    return this.http.get<TopicDto[]>('/api/topics');
  }

  /** Writings that cite the topic's notes (issue #1162), newest first. Read-only. */
  getWritingUsage(id: string, limit = 5): Observable<TopicWritingUsageDto> {
    return this.http.get<TopicWritingUsageDto>(`/api/topics/${id}/writings`, { params: { limit } });
  }

  get(id: string): Observable<TopicDetailDto> {
    return this.http.get<TopicDetailDto>(`/api/topics/${id}`);
  }

  getRelated(id: string): Observable<RelatedTopicDto[]> {
    return this.http.get<RelatedTopicDto[]>(`/api/topics/${id}/related`);
  }

  getGraph(): Observable<TopicGraphDto> {
    return this.http.get<TopicGraphDto>('/api/topics/graph');
  }

  rename(id: string, topic: string): Observable<TopicDto> {
    return this.http.put<TopicDto>(`/api/topics/${id}`, { topic });
  }

  /** Sets the topic's own words; an empty value clears them. */
  setDescription(id: string, description: string | null): Observable<TopicDto> {
    return this.http.put<TopicDto>(`/api/topics/${id}/description`, { description });
  }

  merge(sourceId: string, targetId: string): Observable<TopicDto> {
    return this.http.post<TopicDto>(`/api/topics/${sourceId}/merge`, { targetId });
  }

  /** Adds the named topics to every listed note, creating new topics; one unit of work. */
  link(topics: string[], noteIds: string[]): Observable<TopicLinkResult> {
    return this.http.post<TopicLinkResult>('/api/topics/link', { topics, noteIds });
  }

  /** Removes the named topics from every listed note; also how a link is undone. */
  unlink(topics: string[], noteIds: string[]): Observable<TopicLinkResult> {
    return this.http.post<TopicLinkResult>('/api/topics/unlink', { topics, noteIds });
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/topics/${id}`);
  }
}
