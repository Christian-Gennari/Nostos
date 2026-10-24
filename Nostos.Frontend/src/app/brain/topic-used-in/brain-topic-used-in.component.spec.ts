import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import type { TopicWritingUsageDto } from '../../core/services/topics.service';
import { BrainTopicUsedInComponent } from './brain-topic-used-in.component';

function usage(count: number): TopicWritingUsageDto {
  return {
    total: count,
    writings: Array.from({ length: count }, (_, i) => ({ id: `w${i}`, name: `Essay ${i}`, updatedAt: '2026-10-01T00:00:00Z' })),
    noteCitations: [],
  };
}

// Issue #1162: the topic page's read-only "Used in" row, under "Often with".
describe('BrainTopicUsedInComponent', () => {
  let navigate: ReturnType<typeof vi.spyOn>;

  function render(value: TopicWritingUsageDto | null): HTMLElement {
    TestBed.configureTestingModule({ imports: [BrainTopicUsedInComponent], providers: [provideRouter([])] });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(BrainTopicUsedInComponent);
    fixture.componentRef.setInput('usage', value);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const menuItems = () => Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item'));

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove()));

  it('is labelled Used in and shows the writings as chips, newest edited first', () => {
    const host = render(usage(2));
    const chips = [...host.querySelectorAll('button.used-in-chip')] as HTMLButtonElement[];

    expect(host.querySelector('.often-label')?.textContent).toBe('Used in');
    expect(host.getAttribute('role')).toBe('group');
    expect(chips.map((c) => c.textContent?.trim())).toEqual(['Essay 0', 'Essay 1']);
    expect(chips.every((c) => c.classList.contains('nostos-chip') && c.classList.contains('often-chip'))).toBe(true);
    expect(host.querySelector('.used-in-more')).toBeNull();
  });

  it('opens a writing in the Studio from its chip', () => {
    const host = render(usage(1));

    (host.querySelector('button.used-in-chip') as HTMLButtonElement).click();

    expect(navigate).toHaveBeenCalledWith(['/studio'], { queryParams: { writingId: 'w0' } });
  });

  it('shows at most three chips, then a +N chip whose menu lists the rest', () => {
    const host = render(usage(6));

    expect(host.querySelectorAll('button.used-in-chip')).toHaveLength(3);
    const more = host.querySelector('button.used-in-more') as HTMLButtonElement;
    expect(more.textContent?.trim()).toBe('+3');
    expect(more.getAttribute('aria-label')).toBe('3 more documents');

    more.click();
    TestBed.tick();
    expect(menuItems().map((i) => i.textContent?.trim())).toEqual(['Essay 3', 'Essay 4', 'Essay 5']);

    menuItems()[1].click();
    expect(navigate).toHaveBeenCalledWith(['/studio'], { queryParams: { writingId: 'w4' } });
  });

  it('is hidden when no writing uses the topic', () => {
    const host = render(usage(0));
    expect(host.classList).toContain('is-empty');
    expect(host.querySelector('.often-label')).toBeNull();
    TestBed.resetTestingModule();
    expect(render(null).querySelector('button')).toBeNull();
  });

  it('offers nothing that would add a topic to a writing', () => {
    const host = render(usage(5));

    expect(host.textContent).not.toMatch(/add/i);
    expect(host.querySelectorAll('input')).toHaveLength(0);
  });
});
