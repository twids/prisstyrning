import type { ControlMode, DataQuality, DhwWriter, ThermalStatus } from '../types/api';

const modes: readonly ControlMode[] = ['Legacy', 'Shadow', 'LwtActive', 'FullActive'];
const writers: readonly DhwWriter[] = ['Legacy', 'Joint'];
const qualities: readonly DataQuality[] = ['Valid', 'Stale', 'Invalid', 'Unavailable'];

// ASP.NET currently uses numeric enum values on the wire. Keep that server
// contract intact, accept named values too, and never default unknown data to Valid/Legacy.
function readEnum<T extends string>(value: unknown, names: readonly T[], label: string): T {
  const name = typeof value === 'number' && Number.isInteger(value) ? names[value] : names.find(candidate => candidate === value);
  if (name !== undefined) return name;
  throw new Error(`Serverns ${label} kunde inte tolkas. Ladda om sidan eller kontrollera appversionen.`);
}

export const readControlMode = (value: unknown): ControlMode => readEnum(value, modes, 'driftläge');
export const readDataQuality = (value: unknown): DataQuality => readEnum(value, qualities, 'datakvalitet');

export function writeControlMode(mode: ControlMode): number {
  return modes.indexOf(readControlMode(mode));
}

export type ThermalStatusWire = Omit<ThermalStatus, 'mode' | 'dhwWriter' | 'overallDataQuality'> & {
  mode: unknown;
  dhwWriter: unknown;
  overallDataQuality: unknown;
};

export function readThermalStatus(status: ThermalStatusWire): ThermalStatus {
  return {
    ...status,
    mode: readControlMode(status.mode),
    dhwWriter: readEnum(status.dhwWriter, writers, 'varmvattenskrivare'),
    overallDataQuality: readDataQuality(status.overallDataQuality),
  };
}
