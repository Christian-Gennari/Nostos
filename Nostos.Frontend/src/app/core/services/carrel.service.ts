/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import type {
  CarrelDeviceList,
  CarrelPairingApproved,
  CarrelPendingPairing,
} from '../dtos/carrel.dtos';

/**
 * The signed-in side of Carrel pairing (WP8): look a device's code up, approve
 * or deny it, and list or revoke the devices that sync. The protocol's own
 * endpoints (`/carrel/v1`) are only ever called by the device.
 */
@Injectable({ providedIn: 'root' })
export class CarrelService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/carrel';

  /** The device waiting with this code, or a 404 when none is. */
  findPending(code: string): Observable<CarrelPendingPairing> {
    return this.http.get<CarrelPendingPairing>(
      `${this.base}/pairing/${encodeURIComponent(code)}`,
    );
  }

  approve(code: string): Observable<CarrelPairingApproved> {
    return this.http.post<CarrelPairingApproved>(
      `${this.base}/pairing/${encodeURIComponent(code)}/approve`,
      {},
    );
  }

  deny(code: string): Observable<void> {
    return this.http.post<void>(
      `${this.base}/pairing/${encodeURIComponent(code)}/deny`,
      {},
    );
  }

  devices(): Observable<CarrelDeviceList> {
    return this.http.get<CarrelDeviceList>(`${this.base}/devices`);
  }

  revoke(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/devices/${encodeURIComponent(id)}`);
  }
}

/**
 * The pairing code as a person types it: uppercase symbols from the
 * unambiguous alphabet (no 0, 1, I, L, O or U), a dash after the third, at
 * most six symbols. Anything else the keyboard produces is dropped.
 */
export function formatPairingCode(value: string | null | undefined): string {
  const symbols = (value ?? '')
    .toUpperCase()
    .replace(/[^2-9A-HJKMNP-TV-Z]/g, '')
    .slice(0, 6);
  return symbols.length > 3 ? `${symbols.slice(0, 3)}-${symbols.slice(3)}` : symbols;
}

/** True when the input holds six symbols, however it was spaced or case-folded. */
export function isCompletePairingCode(value: string | null | undefined): boolean {
  return formatPairingCode(value).length === 7;
}
