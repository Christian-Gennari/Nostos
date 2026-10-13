import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { NoticeComponent } from './notice.component';

@Component({
  standalone: true,
  imports: [NoticeComponent],
  template: `
    <p appNotice role="status" class="site-class">Neutral</p>
    <p appNotice tone="info" role="status">Info</p>
    <div appNotice tone="caution">Caution</div>
    <p appNotice tone="danger" role="alert">Danger</p>
  `,
})
class NoticeHarnessComponent {}

describe('NoticeComponent', () => {
  let fixture: ComponentFixture<NoticeHarnessComponent>;
  let notices: HTMLElement[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [NoticeHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(NoticeHarnessComponent);
    fixture.detectChanges();
    notices = Array.from(fixture.nativeElement.querySelectorAll('.nostos-notice'));
  });

  it('marks one tone per notice, and none for neutral', () => {
    const tones = notices.map((notice) =>
      [...notice.classList].filter((name) => name.startsWith('nostos-notice--')),
    );

    expect(tones).toEqual([[], ['nostos-notice--info'], ['nostos-notice--caution'], ['nostos-notice--danger']]);
  });

  it('leaves the element, its role and its own classes to the call site', () => {
    expect(notices.map((notice) => notice.tagName)).toEqual(['P', 'P', 'DIV', 'P']);
    expect(notices.map((notice) => notice.getAttribute('role'))).toEqual(['status', 'status', null, 'alert']);
    expect(notices[0].classList.contains('site-class')).toBe(true);
  });
});
