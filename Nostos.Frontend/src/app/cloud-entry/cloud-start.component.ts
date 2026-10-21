import { Component, OnInit, inject, ChangeDetectionStrategy } from '@angular/core';
import { Router } from '@angular/router';

/**
 * Local acquisition entry route.
 *
 * The root Cloud gate owns authentication/onboarding. This component is only
 * instantiated once that gate has admitted the user to the product, at which
 * point the acquisition URL (including its offer query) is replaced by the
 * normal Library route.
 */
@Component({
  selector: 'app-cloud-start',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.Eager,
  template: '',
})
export class CloudStartComponent implements OnInit {
  private readonly router = inject(Router);

  ngOnInit(): void {
    void this.router.navigateByUrl('/library', { replaceUrl: true });
  }
}
