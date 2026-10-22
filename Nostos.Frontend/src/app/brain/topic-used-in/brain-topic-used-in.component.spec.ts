import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import type { TopicWritingUsageDto } from '../../core/services/topics.service';
import { BrainTopicUsedInComponent } from './brain-topic-used-in.component';

function usage(total: number, count = total): TopicWritingUsageDto {
  return {
    total,
    writings: Array.from({ length: count }, (_, i) => ({ id: `w${i}`, name: `Essay ${i}`, updatedAt: '2026-10-01T00:00:00Z' })),
    noteCitations: [],
  };
}

// Issue #1162: the topic page's read-only "Used in" section.
describe('BrainTopicUsedInComponent', () => {
  function render(value: TopicWritingUsageDto | null): HTMLElement {
    TestBed.configureTestingModule({ imports: [BrainTopicUsedInComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(BrainTopicUsedInComponent);
    fixture.componentRef.setInput('usage', value);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists the writings in the order given (newest first), each opening in the Studio', () => {
    const host = render(usage(2));
    const links = [...host.querySelectorAll('a.used-in-link')] as HTMLAnchorElement[];

    expect(host.querySelector('h2')?.textContent).toBe('Used in');
    expect(links.map((a) => a.textContent)).toEqual(['Essay 0', 'Essay 1']);
    expect(links[0].getAttribute('href')).toBe('/studio?writingId=w0');
    expect(host.querySelector('.used-in-more')).toBeNull();
  });

  it('shows the first five, then "and N more"', () => {
    const host = render(usage(9, 5));

    expect(host.querySelectorAll('a.used-in-link')).toHaveLength(5);
    expect(host.querySelector('.used-in-more')?.textContent?.trim()).toBe('and 4 more');
  });

  it('caps at five even if the server sends more', () => {
    const host = render(usage(7, 7));

    expect(host.querySelectorAll('a.used-in-link')).toHaveLength(5);
    expect(host.querySelector('.used-in-more')?.textContent?.trim()).toBe('and 2 more');
  });

  it('is hidden when no writing uses the topic', () => {
    expect(render(usage(0)).querySelector('[data-testid="topic-used-in"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(render(null).querySelector('[data-testid="topic-used-in"]')).toBeNull();
  });

  it('offers nothing that would add a topic to a writing', () => {
    const host = render(usage(3));

    expect(host.querySelectorAll('button, input')).toHaveLength(0);
  });
});
