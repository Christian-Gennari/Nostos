import { RelatedTopicDto, TopicDetailDto } from '../core/services/topics.service';

/**
 * Component-scoped coordination for Brain topic detail/related reads.
 *
 * The component still decides when data should be loaded or invalidated and
 * owns every UI signal. This class only owns cached values, pending-request
 * bookkeeping and the request generations used to reject stale responses.
 */
export class TopicDataCache {
  private readonly detailCache = new Map<string, TopicDetailDto>();
  private readonly pendingDetailRequests = new Set<string>();
  private detailRequestVersion = 0;

  private readonly relatedCache = new Map<string, RelatedTopicDto[]>();
  private readonly pendingRelatedRequests = new Set<string>();
  private relatedRequestVersion = 0;

  detail(id: string): TopicDetailDto | undefined {
    return this.detailCache.get(id);
  }

  isDetailPending(id: string): boolean {
    return this.pendingDetailRequests.has(id);
  }

  detailVersion(): number {
    return this.detailRequestVersion;
  }

  beginPendingDetail(id: string): number {
    this.pendingDetailRequests.add(id);
    return this.detailRequestVersion;
  }

  resolveDetail(
    id: string,
    requestVersion: number,
    detail: TopicDetailDto,
    pending: boolean
  ): boolean {
    if (pending) this.pendingDetailRequests.delete(id);
    if (requestVersion !== this.detailRequestVersion) return false;
    this.detailCache.set(id, detail);
    return true;
  }

  rejectDetail(id: string, requestVersion: number, pending: boolean): boolean {
    if (pending) this.pendingDetailRequests.delete(id);
    return requestVersion === this.detailRequestVersion;
  }

  commitDetail(id: string, detail: TopicDetailDto): void {
    this.detailCache.set(id, detail);
  }

  advanceDetailVersion(): void {
    this.detailRequestVersion += 1;
  }

  invalidateDetailEntries(...ids: string[]): void {
    this.detailRequestVersion += 1;
    for (const id of ids) {
      this.detailCache.delete(id);
      this.pendingDetailRequests.delete(id);
    }
  }

  invalidateAllDetailEntries(): void {
    this.detailRequestVersion += 1;
    this.detailCache.clear();
    this.pendingDetailRequests.clear();
  }

  related(id: string): RelatedTopicDto[] | undefined {
    return this.relatedCache.get(id);
  }

  isRelatedPending(id: string): boolean {
    return this.pendingRelatedRequests.has(id);
  }

  beginRelated(id: string): number {
    this.pendingRelatedRequests.add(id);
    return this.relatedRequestVersion;
  }

  resolveRelated(id: string, requestVersion: number, related: RelatedTopicDto[]): boolean {
    this.pendingRelatedRequests.delete(id);
    if (requestVersion !== this.relatedRequestVersion) return false;
    this.relatedCache.set(id, related);
    return true;
  }

  rejectRelated(id: string, requestVersion: number): boolean {
    this.pendingRelatedRequests.delete(id);
    return requestVersion === this.relatedRequestVersion;
  }

  invalidateRelatedData(): void {
    this.relatedRequestVersion += 1;
    this.relatedCache.clear();
    this.pendingRelatedRequests.clear();
  }
}
