import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { AssistantChangesetCardComponent } from './assistant-changeset-card.component';
import type { AssistantChangesetDto } from '../assistant.service';

@Component({
  standalone: true,
  imports: [AssistantChangesetCardComponent],
  template: `
    <app-assistant-changeset-card
      [changeset]="changeset"
      (stepToggled)="step = $event"
      (stepsToggled)="steps = $event"
    />
  `,
})
class HostComponent {
  changeset = reviewWithKinds();
  step: { index: number; included: boolean } | null = null;
  steps: { indexes: number[]; included: boolean } | null = null;
}

function reviewWithKinds(): AssistantChangesetDto {
  return {
    changesetId: 'c1',
    status: 'review',
    steps: [
      { index: 0, summary: 'Create the topic kind “School of thought”', included: true, dependsOn: [] },
      { index: 1, summary: 'Set “Freire” to “School of thought”', included: true, dependsOn: [0] },
      { index: 2, summary: 'Set “Montessori” to “School of thought”', included: true, dependsOn: [0] },
      { index: 3, summary: 'Set “Paulo Freire” to “Person”', included: true, dependsOn: [] },
    ],
    diff: {
      collections: [],
      memberships: [],
      bookEdits: [],
      counts: {
        collectionsCreated: 0,
        collectionsDeleted: 0,
        collectionsUpdated: 0,
        booksTouched: 0,
        membershipsAdded: 0,
        membershipsRemoved: 0,
        booksLosingMembership: 0,
        bookEdits: 0,
        topicKindsCreated: 1,
        topicKindChanges: 3,
      },
      topicKinds: [
        {
          id: 'kind-school',
          kind: 'created',
          name: 'School of thought',
          previousName: null,
          colorKey: 'kind-7',
          topicCount: 2,
          stepIndex: 0,
        },
      ],
      topicKindAssignments: [
        {
          topicId: 't1',
          topicName: 'Freire',
          kindId: 'kind-school',
          kindName: 'School of thought',
          previousKindName: null,
          source: 'assistant',
          stepIndex: 1,
        },
        {
          topicId: 't2',
          topicName: 'Montessori',
          kindId: 'kind-school',
          kindName: 'School of thought',
          previousKindName: null,
          source: 'assistant',
          stepIndex: 2,
        },
        {
          topicId: 't3',
          topicName: 'Paulo Freire',
          kindId: '11111111-0000-0000-0000-000000000001',
          kindName: 'Person',
          previousKindName: null,
          source: 'assistant',
          stepIndex: 3,
        },
      ],
    },
    diffHash: 'hash',
    actionToken: 'token',
    reviewReasons: [],
    canUndo: false,
  };
}

describe('AssistantChangesetCardComponent topic kinds', () => {
  function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('groups the changes by kind, with a new kind first and its colour', () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;
    const groups = [...element.querySelectorAll('.changeset__kind-group')];

    expect(groups).toHaveLength(2);
    expect(groups[0].textContent).toContain('School of thought');
    expect(groups[0].textContent).toContain('(2)');
    expect(groups[0].textContent).toContain('New');
    expect(groups[0].querySelector('.changeset__kind-swatch')?.getAttribute('style'))
      .toContain('--map-kind-7');
    expect(groups[1].textContent).toContain('Person');
  });

  it('one group checkbox ticks or unticks every topic in the group', () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;
    const first = element.querySelector('.changeset__kind-group input') as HTMLInputElement;

    first.checked = false;
    first.dispatchEvent(new Event('change'));

    expect(fixture.componentInstance.steps).toEqual({ indexes: [0, 1, 2], included: false });
  });

  it('each topic can be unticked under Show every change', () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="assistant-changeset-details-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const toggles = [...element.querySelectorAll('[data-testid="assistant-changeset-topic-toggle"]')] as HTMLInputElement[];
    expect(toggles).toHaveLength(3);

    toggles[0].checked = false;
    toggles[0].dispatchEvent(new Event('change'));

    expect(fixture.componentInstance.step).toEqual({ index: 1, included: false });
  });

  it('counts the kinds in the headline instead of saying nothing changed', () => {
    const fixture = setup();
    const headline = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;

    expect(headline).toContain('3 topics a kind');
    expect(headline).toContain('1 topic kind');
  });
});
