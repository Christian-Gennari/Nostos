/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

/** A pairing code waiting for its owner (Carrel Sync Protocol v1, WP8). */
export interface CarrelPendingPairing {
  code: string;
  deviceName: string;
  expiresAt: string;
}

/** The answer to approving a code. */
export interface CarrelPairingApproved {
  deviceId: string;
  deviceName: string;
}

/** One paired writing device. Never carries a token. */
export interface CarrelDevice {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
}

export interface CarrelDeviceList {
  devices: CarrelDevice[];
}
