import { HttpErrorResponse } from '@angular/common/http';
import { planLimitMessage } from './plan-limit-message';

describe('planLimitMessage', () => {
  const limit = (body: unknown, status = 403) => new HttpErrorResponse({ status, error: body });

  it("shows the host's message for a plan limit", () => {
    const message = 'Your free trial includes 25 uploaded files, and you have used them all. Subscribe to keep adding books.';
    expect(planLimitMessage(limit({ code: 'trial_upload_limit_reached', detail: message }))).toBe(message);
    expect(planLimitMessage(limit({ code: 'storage_limit_reached', detail: ' Your library is full. ' }))).toBe(
      'Your library is full.',
    );
  });

  it('ignores other problems, missing detail and odd shapes', () => {
    expect(planLimitMessage(limit({ code: 'book_import_in_progress', detail: 'Wait.' }, 409))).toBeNull();
    expect(planLimitMessage(limit({ code: 'storage_limit_reached' }))).toBeNull();
    expect(planLimitMessage(limit('Forbidden'))).toBeNull();
    expect(planLimitMessage(limit({ code: 'storage_limit_reached', detail: 'x'.repeat(401) }))).toBeNull();
    expect(planLimitMessage(null)).toBeNull();
    expect(planLimitMessage({ status: 0 })).toBeNull();
  });
});
