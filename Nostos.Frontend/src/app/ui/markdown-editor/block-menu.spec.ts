import { BLOCK_MENU_ITEMS, filterBlockMenu } from './block-menu';

describe('the / menu items (issue #1043)', () => {
  it('lists every block for a bare slash', () => {
    expect(filterBlockMenu('').length).toBe(BLOCK_MENU_ITEMS.length);
  });

  it('finds blocks by label, keyword or initials, best match first', () => {
    expect(filterBlockMenu('h2')[0].action).toBe('h2');
    expect(filterBlockMenu('quote')[0].action).toBe('blockquote');
    expect(filterBlockMenu('div')[0].action).toBe('divider');
    expect(filterBlockMenu('img')[0].action).toBe('image');
    expect(filterBlockMenu('cite')[0].action).toBe('source');
  });

  it('shows the Markdown shortcut for each block', () => {
    expect(BLOCK_MENU_ITEMS.find((i) => i.action === 'h2')?.hint).toBe('## ');
  });

  it('matches nothing for nonsense', () => {
    expect(filterBlockMenu('zzzq')).toEqual([]);
  });
});
