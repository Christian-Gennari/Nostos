import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BLOCK_MENU_ITEMS } from './block-menu';
import { EditorBlockMenuComponent, computeBlockMenuPlacement } from './editor-block-menu.component';

describe('EditorBlockMenuComponent', () => {
  let fixture: ComponentFixture<EditorBlockMenuComponent>;
  let component: EditorBlockMenuComponent;

  const sampleMenu = {
    items: BLOCK_MENU_ITEMS.slice(0, 3),
    rect: { top: 100, bottom: 120, left: 40, right: 60 },
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EditorBlockMenuComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(EditorBlockMenuComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    fixture?.destroy();
  });

  it('renders menu items and emits selection and active index changes', () => {
    fixture.componentRef.setInput('menu', sampleMenu);
    fixture.componentRef.setInput('activeIndex', 1);
    fixture.detectChanges();

    const items = fixture.nativeElement.querySelectorAll('.block-menu-item') as NodeListOf<HTMLElement>;
    expect(items.length).toBe(3);
    expect(items[1].classList.contains('active')).toBe(true);
    expect(items[1].getAttribute('aria-selected')).toBe('true');

    const hovered: number[] = [];
    const selected: string[] = [];
    component.activeIndexChange.subscribe((i) => hovered.push(i));
    component.actionSelect.subscribe((a) => selected.push(a));

    items[2].dispatchEvent(new MouseEvent('mouseenter'));
    items[2].click();

    expect(hovered).toEqual([2]);
    expect(selected).toEqual([sampleMenu.items[2].action]);
  });

  it('focuses blockImageInput after timeout when imageMode opens', () => {
    vi.useFakeTimers();
    fixture.componentRef.setInput('menu', sampleMenu);
    fixture.componentRef.setInput('imageMode', true);
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('.block-menu-image input') as HTMLInputElement;
    expect(input).toBeTruthy();
    const focusSpy = vi.spyOn(input, 'focus');

    vi.runAllTimers();
    expect(focusSpy).toHaveBeenCalledTimes(1);
  });

  it('clears blockImageInput focus timer when the menu closes before firing', () => {
    vi.useFakeTimers();
    fixture.componentRef.setInput('menu', sampleMenu);
    fixture.componentRef.setInput('imageMode', true);
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('.block-menu-image input') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus');

    fixture.componentRef.setInput('menu', null);
    fixture.componentRef.setInput('imageMode', false);
    fixture.detectChanges();
    vi.runAllTimers();

    expect(focusSpy).not.toHaveBeenCalled();
  });

  it('clears blockImageInput focus timer on destroy', () => {
    vi.useFakeTimers();
    fixture.componentRef.setInput('menu', sampleMenu);
    fixture.componentRef.setInput('imageMode', true);
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('.block-menu-image input') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus');

    fixture.destroy();
    vi.runAllTimers();

    expect(focusSpy).not.toHaveBeenCalled();
  });

  it('places the menu above the line when there is no room below', () => {
    const placement = computeBlockMenuPlacement({
      top: window.innerHeight - 30,
      bottom: window.innerHeight - 10,
      left: 40,
      right: 60,
    })!;
    expect(placement.top).toBeNull();
    expect(placement.bottom).toBe(36);
  });
});
