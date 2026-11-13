import { Component, input, output } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WritingDto } from '../../core/dtos/writing.dtos';
import { FlatTreeComponent } from '../../ui/flat-tree/flat-tree.component';
import { StudioFilesSidebarComponent } from './studio-files-sidebar.component';

@Component({ selector: 'app-flat-tree', standalone: true, template: '' })
class FlatTreeStub {
  readonly items = input.required<unknown[]>();
  readonly activeId = input<string | null>(null);
  readonly editingId = input<string | null>(null);
  readonly renameError = input<string | null>(null);
  readonly renameBusy = input(false);
  readonly nodeSelected = output<unknown>();
  readonly nodeMoved = output<unknown>();
  readonly nodeRenamed = output<unknown>();
  readonly nodeDeleted = output<string>();
  readonly nodeRenameSaved = output<unknown>();
  readonly nodeRenameCancelled = output<void>();
}

describe('StudioFilesSidebarComponent', () => {
  let fixture: ComponentFixture<StudioFilesSidebarComponent>;
  let component: StudioFilesSidebarComponent;

  const sampleItems: WritingDto[] = [
    {
      id: 'd1',
      name: 'Chapter 1',
      type: 'Document',
      parentId: null,
      updatedAt: '2026-01-01T00:00:00Z',
    },
  ];

  beforeEach(async () => {
    TestBed.overrideComponent(StudioFilesSidebarComponent, {
      remove: { imports: [FlatTreeComponent] },
      add: { imports: [FlatTreeStub] },
    });

    await TestBed.configureTestingModule({
      imports: [StudioFilesSidebarComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(StudioFilesSidebarComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('items', sampleItems);
    fixture.detectChanges();
  });

  it('emits createDocument, createFolder, collapseRail, and renders retry state on treeFailed', () => {
    const events: string[] = [];
    component.createDocument.subscribe(() => events.push('doc'));
    component.createFolder.subscribe(() => events.push('folder'));
    component.collapseRail.subscribe(() => events.push('collapse'));
    component.retryLoad.subscribe(() => events.push('retry'));

    (fixture.nativeElement.querySelector('.files-new-document') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('button[aria-label="New folder"]') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('.files-rail-collapse') as HTMLButtonElement).click();

    fixture.componentRef.setInput('treeFailed', true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('nostos-empty-state')).toBeTruthy();
    expect(events).toEqual(['doc', 'folder', 'collapse']);
  });
});
