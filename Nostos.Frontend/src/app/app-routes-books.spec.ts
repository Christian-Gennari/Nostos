import { routes } from './app.routes';

describe('the /books/:id address', () => {
  it("redirects to the book's page, not to the Library", () => {
    const layout = routes.find((route) => route.children?.some((child) => child.path === 'books/:id'));
    const route = layout?.children?.find((child) => child.path === 'books/:id');
    expect(route?.redirectTo).toBe('library/:id');
    expect(route?.pathMatch).toBe('full');
  });
});
