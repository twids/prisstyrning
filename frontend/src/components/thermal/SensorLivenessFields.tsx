import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Alert, Autocomplete, Button, Checkbox, FormControlLabel, Stack, TextField, Typography } from '@mui/material';
import { apiClient } from '../../api/client';
import type { SensorFreshnessRequest } from '../../types/api';
import type { EntityCatalogView } from './HomeAssistantEntityPicker';
import { formatDateTime } from './thermalUi';

export function livenessConfigError(value: Pick<SensorFreshnessRequest, 'freshnessEntityId' | 'freshnessAttribute'>): string | null {
  if (value.freshnessEntityId != null && (value.freshnessEntityId.length > 255 || !/^[a-z_]+\.[a-z0-9_]+$/.test(value.freshnessEntityId))) return 'Välj ett giltigt entity-ID för livstecknet.';
  if (value.freshnessAttribute != null && (value.freshnessAttribute.length > 100 || !/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(value.freshnessAttribute))) return 'Ange ett enkelt attributnamn, till exempel last_seen.';
  return null;
}

export default function SensorLivenessFields({ value, onChange, catalog, label }: {
  value: SensorFreshnessRequest; onChange: (value: Pick<SensorFreshnessRequest, 'freshnessEntityId' | 'freshnessAttribute'>) => void;
  catalog: EntityCatalogView; label: string;
}) {
  const [requestedKey, setRequestedKey] = useState<string | null>(null);
  const test = useMutation({ mutationFn: (request: SensorFreshnessRequest) => apiClient.previewSensorFreshness(request) });
  const key = JSON.stringify(value);
  const age = value.maximumReportAgeMinutes;
  const invalidAge = age != null && (!Number.isInteger(age) || age < 1 || age > 1440);
  const result = requestedKey === key && !invalidAge && !test.isPending && !test.isError && !catalog.issue ? test.data : undefined;
  const current = result && Date.parse(result.checkedAtUtc) <= catalog.nowUtc + 30_000 && catalog.nowUtc - Date.parse(result.checkedAtUtc) <= 120_000;
  const error = livenessConfigError(value);
  const configured = value.freshnessEntityId != null || value.freshnessAttribute != null;
  const useReportTime = value.freshnessAttribute === '__ha_report_time';
  return <Stack spacing={1.5} role="group" aria-label={`Livstecken för ${label}`}>
    <Typography fontWeight={700}>Långsam eller förändringsbaserad givare</Typography>
    <Typography variant="body2">Standard använder HA:s rapporteringstid. Om givaren bara publicerar ändringar kan du välja ett separat tidsstämplat livstecken från samma fysiska givare. Valet gäller först efter att inställningarna sparats.</Typography>
    <Autocomplete freeSolo options={catalog.entities.map(entity => entity.entityId)} value={value.freshnessEntityId ?? ''}
      disabled={test.isPending || Boolean(catalog.issue)}
      onInputChange={(_, text, reason) => { if (reason === 'input' || reason === 'clear') onChange({ freshnessAttribute: value.freshnessAttribute, freshnessEntityId: text || null }); }}
      onChange={(_, text) => onChange({ freshnessAttribute: value.freshnessAttribute, freshnessEntityId: text || null })}
      renderInput={params => <TextField {...params} label={`Livstecknets entity för ${label}`} helperText="Välj en entitet från samma fysiska givare. Använd valet nedan för en vanlig sensor; annars krävs ett tidsstämpelvärde eller attribut. Tomt använder temperatur-/vädergivaren." />} />
    <FormControlLabel label={`Använd entitetens senaste HA-rapport som livstecken för ${label}`} control={<Checkbox checked={useReportTime} disabled={test.isPending}
      onChange={(_, checked) => onChange({ freshnessEntityId: value.freshnessEntityId, freshnessAttribute: checked ? '__ha_report_time' : null })} />} />
    {useReportTime && <Alert severity="info">Välj en rapporterande entitet från samma fysiska givare, exempelvis lufttryck eller batterispänning. Vi använder HA:s rapporteringstid (uppdateringstid om rapporteringstid saknas), inte sensorvärdet eller tidpunkten då appen läser HA. Temperaturen får ingen ny mättid.</Alert>}
    {!useReportTime && <TextField label={`Livstecknets attribut för ${label}`} value={value.freshnessAttribute ?? ''} disabled={test.isPending}
      onChange={event => onChange({ freshnessEntityId: value.freshnessEntityId, freshnessAttribute: event.target.value || null })}
      error={Boolean(error)} helperText={error ?? 'Exempel: last_seen. Tomt med vald entity läser dess tillstånd som tidsstämpel. Båda tomma återställer standard.'} />}
    {configured && <Alert severity="warning">Kontrollera att källan hör till rätt fysiska givare och faktiskt uppdateras vid kontakt. En allmän HA-ping eller available räcker inte. Livstecken är inte nya temperaturmätningar.</Alert>}
    <Button disabled={!value.entityId || invalidAge || Boolean(error || catalog.issue) || catalog.loading || test.isPending}
      onClick={() => { setRequestedKey(key); test.mutate(value); }}>Testa rapportålder för {label}</Button>
    {test.isPending && <Typography role="status">Kontrollerar kontots aktuella HA-startbild…</Typography>}
    {test.isError && requestedKey === key && <Alert severity="error">Kontrollen kunde inte genomföras. Inga inställningar har sparats. Kontrollera anslutningen och försök igen.</Alert>}
    {result && <Alert severity={current && result.quality === 'Valid' ? 'success' : 'warning'}>
      {current ? result.reason : 'Kontrollen är äldre än två minuter. Testa igen innan du bedömer resultatet.'}
      {current && result.livenessUtc && <Typography variant="body2">Livstecken: {formatDateTime(result.livenessUtc)}. Värdet uppdaterat: {result.valueUpdatedUtc ? formatDateTime(result.valueUpdatedUtc) : 'okänd tid'}.</Typography>}
    </Alert>}
    <Typography variant="caption">Testet är skrivskyddat och ändrar inte sensorns felräknare. Osäker ålder ger varning och hindrar inte i sig Shadow; aktiv styrning har separata säkerhetskrav.</Typography>
  </Stack>;
}
