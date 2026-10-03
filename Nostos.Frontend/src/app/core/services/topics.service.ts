import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface TopicDto {
  id: string;
  name: string;
  usageCount: number;
  // Appended for the index search (issue #158). The plain list has no note text,
  // so a term that only appears inside a note can only be matched on the server;
  // these carry what matched, and are absent on the unfiltered list.
  noteMatchCount?: number;
  noteMatchSnippet?: string | null;
}

export interface TopicStatsDto {
  totalTopics: number;
  totalReferences: number;
  singleNoteTopics: number;
  mostUsedName: string | null;
  mostUsedCount: number;
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
}

export interface TopicDetailDto {
  id: string;
  name: string;
  notes: NoteContextDto[];
}

@Injectable({ providedIn: 'root' })
export class TopicsService {
  private http = inject(HttpClient);

  list(): Observable<TopicDto[]> {
    return this.http.get<TopicDto[]>('/api/topics');
  }

  /**
   * Note-text search for the index (issue #158). `GET /api/topics` carries no
   * note text at all, so the term is matched server-side against note content,
   * quote text and book title — the same three fields the note-level search inside
   * a topic already matches.
   */
  searchNotes(term: string): Observable<TopicDto[]> {
    return this.http.get<TopicDto[]>('/api/topics', { params: { search: term } });
  }

  getStats(): Observable<TopicStatsDto> {
    return this.http.get<TopicStatsDto>('/api/topics/stats');
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

  merge(sourceId: string, targetId: string): Observable<TopicDto> {
    return this.http.post<TopicDto>(`/api/topics/${sourceId}/merge`, { targetId });
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/topics/${id}`);
  }
}
