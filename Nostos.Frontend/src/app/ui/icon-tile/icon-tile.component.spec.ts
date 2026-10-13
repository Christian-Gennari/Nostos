import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { IconTileComponent } from './icon-tile.component';

@Component({
  standalone: true,
  imports: [IconTileComponent],
  template: `
    <span appIconTile class="site-class" aria-hidden="true">a</span>
    <span appIconTile tone="quiet">b</span>
    <span appIconTile tone="danger" [size]="26">c</span>
  `,
})
class IconTileHarnessComponent {}

describe('IconTileComponent', () => {
  let fixture: ComponentFixture<IconTileHarnessComponent>;
  let tiles: HTMLElement[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [IconTileHarnessComponent] }).compileComponents();
    fixture = TestBed.createComponent(IconTileHarnessComponent);
    fixture.detectChanges();
    tiles = Array.from(fixture.nativeElement.querySelectorAll('.nostos-icon-tile'));
  });

  it('marks the tone, and none for neutral', () => {
    expect(
      tiles.map((tile) => [...tile.classList].filter((name) => name.startsWith('nostos-icon-tile--'))),
    ).toEqual([[], ['nostos-icon-tile--quiet'], ['nostos-icon-tile--danger']]);
  });

  it('sets the size only when asked, so a stylesheet can own it otherwise', () => {
    expect(tiles[0].style.getPropertyValue('--icon-tile-size')).toBe('');
    expect(tiles[2].style.getPropertyValue('--icon-tile-size')).toBe('26px');
  });

  it('keeps the call site class and attributes', () => {
    expect(tiles[0].classList.contains('site-class')).toBe(true);
    expect(tiles[0].getAttribute('aria-hidden')).toBe('true');
  });
});
