import { Component, inject } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PROGRESS_SCOPE_KEY, PROGRESS_SCOPE_FLASH_MS, ReaderProgressScope } from './reader-progress-scope';
import { ReaderTimers } from './reader-timers';
import type { ReaderProgress } from './reader.interface';

@Component({
  selector: 'app-reader-progress-scope-host',
  standalone: true,
  template: '',
  providers: [ReaderTimers, ReaderProgressScope],
})
class ReaderProgressScopeHost {
  readonly progress = inject(ReaderProgressScope);
}

const chaptered: ReaderProgress = {
  label: 'Chapter 2 of 9',
  percentage: 40,
  chapterPercentage: 25,
  chapterLabel: 'Chapter 2',
  tooltip: '2% ≈ 3 min',
};

describe('ReaderProgressScope (#1269, #1607)', () => {
  let fixture: ComponentFixture<ReaderProgressScopeHost>;
  let progress: ReaderProgressScope;

  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [ReaderProgressScopeHost] });
    fixture = TestBed.createComponent(ReaderProgressScopeHost);
    progress = fixture.componentInstance.progress;
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    vi.useRealTimers();
  });

  it('starts on book progress and falls back to it for a reader with no chapters', () => {
    progress.update({ label: 'Page 3 of 20', percentage: 15, chapterPercentage: null });

    expect(progress.scope()).toBe('book');
    expect(progress.effective()).toBe('book');
    expect(progress.chapterAvailable()).toBe(false);
    expect(progress.fillPercent()).toBe(15);
    expect(progress.label()).toBe('Page 3 of 20');
  });

  it('flips to the chapter, says so briefly, and remembers the choice', () => {
    progress.update(chaptered);
    progress.toggle();

    expect(progress.scope()).toBe('chapter');
    expect(progress.effective()).toBe('chapter');
    expect(progress.fillPercent()).toBe(25);
    expect(progress.flash()).toBe('Chapter progress');
    expect(progress.label()).toBe('Chapter progress');
    expect(localStorage.getItem(PROGRESS_SCOPE_KEY)).toBe('chapter');

    vi.advanceTimersByTime(PROGRESS_SCOPE_FLASH_MS);

    expect(progress.flash()).toBeNull();
    expect(progress.label()).toBe('25% of Chapter 2');
  });

  it('falls back to the book bar silently when the reader cannot place a chapter', () => {
    progress.scope.set('chapter');
    progress.update({ label: 'Page 3 of 20', percentage: 15, chapterPercentage: null });

    expect(progress.effective()).toBe('book');
    expect(progress.fillPercent()).toBe(15);
    expect(progress.label()).toBe('Page 3 of 20');
    // Nothing to flip: a toggle would have no second state.
    progress.toggle();
    expect(progress.scope()).toBe('chapter');
  });

  it('restores the remembered scope and cancels a pending flash on destroy', () => {
    localStorage.setItem(PROGRESS_SCOPE_KEY, 'chapter');
    TestBed.resetTestingModule();

    TestBed.configureTestingModule({ imports: [ReaderProgressScopeHost] });
    fixture = TestBed.createComponent(ReaderProgressScopeHost);
    progress = fixture.componentInstance.progress;
    progress.update(chaptered);
    expect(progress.scope()).toBe('chapter');

    progress.toggle();
    expect(progress.flash()).toBe('Book progress');

    fixture.destroy();
    vi.advanceTimersByTime(PROGRESS_SCOPE_FLASH_MS);
    expect(progress.flash()).toBe('Book progress');
  });
});
