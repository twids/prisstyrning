import { useMutation } from '@tanstack/react-query';
import { Alert, Autocomplete, Button, Stack, TextField, Typography } from '@mui/material';
import { apiClient } from '../../api/client';
import type { HomeAssistantEntity } from '../../types/api';
import type { EntityCatalogView } from './HomeAssistantEntityPicker';
import { formatDateTime } from './thermalUi';

export default function WeatherSourcePicker({ catalog, entityId, onChange }: {
  catalog: EntityCatalogView; entityId: string; onChange: (entity: HomeAssistantEntity | null) => void;
}) {
  const test = useMutation({ mutationFn: () => apiClient.testWeather(entityId) });
  const options = catalog.entities.filter((entity) => entity.entityId.startsWith('weather.'));
  const selected = options.find((entity) => entity.entityId === entityId);
  const result = test.data;
  return <Stack spacing={2}>
    <Typography variant="h6">Gemensam väderkälla</Typography>
    <Typography color="text.secondary">Välj en weather-entity från exempelvis SMHI eller met.no. Timprognosen hämtas separat från HA. Egna temperatur-, vind- och solgivare nedan är separata mätkällor, inte krav på fler prognosintegrationer.</Typography>
    <Autocomplete options={options} value={selected ?? null} loading={catalog.loading}
      getOptionLabel={(entity) => `${entity.friendlyName} · ${entity.entityId}`}
      isOptionEqualToValue={(a, b) => a.entityId === b.entityId}
      onChange={(_, value) => onChange(value)}
      renderInput={(params) => <TextField {...params} label="Väderkälla (weather.*)" helperText={entityId && !selected ? `Sparad källa: ${entityId}. Mappningen är kvar även om källan inte finns i listan.` : 'Testa att just denna källa levererar användbar timprognos.'} />} />
    <Button onClick={() => test.mutate()} disabled={!entityId.startsWith('weather.') || test.isPending}>{test.isPending ? 'Hämtar timprognos…' : 'Testa väderprognos'}</Button>
    {test.isError && <Alert severity="error">Prognostestet misslyckades: {test.error.message}</Alert>}
    {result && <Alert severity={result.quality === 'Valid' ? 'success' : 'warning'}>
      {result.quality === 'Valid' && result.points.length > 0
        ? `${result.points.length} giltiga prognospunkter: ${formatDateTime(result.points[0].timestampUtc)} – ${formatDateTime(result.points[result.points.length - 1].timestampUtc)}. Vind finns i ${result.points.filter((point) => point.windSpeedMps != null).length} punkter, omräknad till m/s. Solinstrålning finns i ${result.points.filter((point) => point.solarIrradianceWm2 != null).length} punkter.`
        : result.reason ?? 'Ingen användbar timprognos.'}
    </Alert>}
    <Typography variant="caption">Ett lyckat test garanterar inte 48 timmars täckning. Molnighet och UV är inte solinstrålning i W/m². Saknade uppgifter räknas inte som noll.</Typography>
  </Stack>;
}
