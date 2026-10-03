import { describe, expect, it } from 'vitest';

import { TopicDetailDto } from '../core/services/topics.service';
import { TopicDataCache } from './topic-data-cache';

const detail = (id: string, name: string): TopicDetailDto => ({
  id,
  name,
  notes: [],
});

describe('TopicDataCache', () => {
  it('rejects stale detail and related responses after mutation invalidation', () => {
    const cache = new TopicDataCache();
    const detailVersion = cache.beginPendingDetail('c-alpha');
    const relatedVersion = cache.beginRelated('c-alpha');

    cache.invalidateAllDetailEntries();
    cache.invalidateRelatedData();

    expect(
      cache.resolveDetail('c-alpha', detailVersion, detail('c-alpha', 'Stale Alpha'), true)
    ).toBe(false);
    expect(
      cache.resolveRelated('c-alpha', relatedVersion, [
        { id: 'c-beta', name: 'Beta', sharedNotes: 1 },
      ])
    ).toBe(false);
    expect(cache.detail('c-alpha')).toBeUndefined();
    expect(cache.related('c-alpha')).toBeUndefined();
  });

  it('keeps current-generation detail and related responses cacheable', () => {
    const cache = new TopicDataCache();
    const currentDetail = detail('c-alpha', 'Alpha');
    const detailVersion = cache.beginPendingDetail('c-alpha');
    const relatedVersion = cache.beginRelated('c-alpha');
    const related = [{ id: 'c-beta', name: 'Beta', sharedNotes: 2 }];

    expect(cache.resolveDetail('c-alpha', detailVersion, currentDetail, true)).toBe(true);
    expect(cache.resolveRelated('c-alpha', relatedVersion, related)).toBe(true);
    expect(cache.detail('c-alpha')).toBe(currentDetail);
    expect(cache.related('c-alpha')).toBe(related);
    expect(cache.isDetailPending('c-alpha')).toBe(false);
    expect(cache.isRelatedPending('c-alpha')).toBe(false);
  });

  it('invalidates only named detail entries while advancing the shared generation', () => {
    const cache = new TopicDataCache();
    cache.commitDetail('c-alpha', detail('c-alpha', 'Alpha'));
    cache.commitDetail('c-beta', detail('c-beta', 'Beta'));
    const staleVersion = cache.beginPendingDetail('c-gamma');

    cache.invalidateDetailEntries('c-alpha');

    expect(cache.detail('c-alpha')).toBeUndefined();
    expect(cache.detail('c-beta')?.name).toBe('Beta');
    expect(
      cache.resolveDetail('c-gamma', staleVersion, detail('c-gamma', 'Gamma'), true)
    ).toBe(false);
  });
});
