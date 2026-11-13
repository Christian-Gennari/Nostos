import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WritingDto } from '../../core/dtos/writing.dtos';
import { NOSTOS_ICONS } from '../../ui/icon/nostos-icons';
import { buildDeleteWritingCopy, buildMoveRows, moveLocationLabel } from './move-writing.helpers';
import { MoveWritingDialogComponent } from './move-writing-dialog.component';

describe('MoveWritingDialogComponent & helpers', () => {
  let fixture: ComponentFixture<MoveWritingDialogComponent>;
  let component: MoveWritingDialogComponent;

  const sampleTree: WritingDto[] = [
    {
      id: 'f1',
      name: 'Essays',
      type: 'Folder',
      parentId: null,
      updatedAt: '2026-01-01T00:00:00Z',
    },
    {
      id: 'f2',
      name: 'Notes',
      type: 'Folder',
      parentId: 'f1',
      updatedAt: '2026-01-01T00:00:00Z',
    },
    {
      id: 'd1',
      name: 'Draft',
      type: 'Document',
      parentId: 'f1',
      updatedAt: '2026-01-01T00:00:00Z',
    },
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MoveWritingDialogComponent],
    }).compileComponents();

    expect(NOSTOS_ICONS).toBeDefined();
    fixture = TestBed.createComponent(MoveWritingDialogComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    fixture?.destroy();
  });

  it('builds move rows and delete copy from tree items', () => {
    const rows = buildMoveRows(sampleTree, 'd1');
    expect(rows.map((r) => [r.name, r.level, r.current])).toEqual([
      ['Top level', 0, false],
      ['Essays', 0, true],
      ['Notes', 1, false],
    ]);
    expect(moveLocationLabel('f1', sampleTree)).toBe('in “Essays”');
    expect(moveLocationLabel(null, sampleTree)).toBe('at the top level');

    const delFolder = buildDeleteWritingCopy('f1', sampleTree);
    expect(delFolder.heading).toBe('Delete “Essays”?');
    expect(delFolder.confirmLabel).toBe('Delete folder');
  });

  it('renders rows, skips current destination, and emits moveSelected on non-current choice', () => {
    const rows = buildMoveRows(sampleTree, 'd1');
    const selected: Array<string | null> = [];
    component.moveSelected.subscribe((parentId) => selected.push(parentId));

    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('documentName', 'Draft');
    fixture.componentRef.setInput('locationLabel', 'in “Essays”');
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('hasDestination', true);
    fixture.detectChanges();

    const currentBtn = document.querySelector('[data-move-row="folder:f1"]') as HTMLButtonElement;
    currentBtn.click();
    expect(selected).toEqual([]);

    const topBtn = document.querySelector('[data-testid="studio-move-top"]') as HTMLButtonElement;
    topBtn.click();
    expect(selected).toEqual([null]);
  });
});
