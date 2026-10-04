import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AppDockComponent } from '../app-dock/app-dock.component';
import { UtilitySheetComponent } from '../utility-sheet/utility-sheet.component';
import { UtilitySheetService } from '../utility-sheet/utility-sheet.service';

@Component({
  standalone: true,
  selector: 'app-workspace-layout',
  imports: [RouterOutlet, AppDockComponent, UtilitySheetComponent],
  templateUrl: './workspace-layout.component.html',
  styleUrl: './workspace-layout.component.css',
})
export class WorkspaceLayout {
  readonly sheet = inject(UtilitySheetService);
}
