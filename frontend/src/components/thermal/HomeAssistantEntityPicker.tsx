import { useMemo } from 'react';
import { Autocomplete, Box, Chip, Stack, TextField, Typography } from '@mui/material';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import type { HomeAssistantEntity } from '../../types/api';
import { assessEntityChoice, type EntityChoiceQuality } from './entityCatalog';
import { formatRelative, QualityChip } from './thermalUi';

export interface EntityCatalogView {
  entities: HomeAssistantEntity[];
  nowUtc: number;
  issue?: string;
  loading?: boolean;
}

export default function HomeAssistantEntityPicker({ catalog, entityId, expectedUnit, label, onChange, required = false }: {
  catalog: EntityCatalogView;
  entityId: string;
  expectedUnit: string;
  label: string;
  onChange: (entity: HomeAssistantEntity | null) => void;
  required?: boolean;
}) {
  const found = catalog.entities.find((entity) => entity.entityId === entityId) ?? null;
  // Preserve configured IDs through empty/error responses. Never clear on refetch.
  const selected = useMemo(() => found ?? (entityId ? {
    entityId, friendlyName: entityId, state: '', unit: null,
    lastUpdatedUtc: null, receivedAtUtc: '', quality: 'Unavailable' as const,
    qualityReason: 'Den sparade entityn finns inte i den aktuella listan. Mappningen är kvar.',
  } : null), [found, entityId]);
  const options = !found && selected ? [selected, ...catalog.entities] : catalog.entities;
  const quality = assessEntityChoice(found, expectedUnit, catalog.nowUtc, catalog.issue);

  return <Box role="group" aria-label={`Datakälla: ${label}`} sx={{ minWidth: 0, width: '100%' }}>
    <Autocomplete
      options={options}
      value={selected}
      onChange={(_, entity) => onChange(entity)}
      disabled={Boolean(catalog.issue) || catalog.loading}
      loading={catalog.loading}
      loadingText="Hämtar sensorer…"
      noOptionsText="Inga matchande entities. Kontrollera anslutningen och sökningen."
      clearText="Rensa val"
      openText="Visa sensorer"
      closeText="Stäng sensorlistan"
      slotProps={{ popper: { role: 'region', 'aria-label': `Sensoralternativ: ${label}` } }}
      getOptionLabel={(option) => option.friendlyName === option.entityId ? option.entityId : `${option.friendlyName} · ${option.entityId}`}
      isOptionEqualToValue={(option, value) => option.entityId === value.entityId}
      renderOption={({ key, ...props }, option) => {
        const result = assessEntityChoice(option, expectedUnit, catalog.nowUtc, catalog.issue);
        return <Box component="li" {...props} key={key} sx={{ minWidth: 0, overflowWrap: 'anywhere' }}>
          <Stack spacing={.5} sx={{ minWidth: 0, width: '100%' }}>
            <Typography>{option.friendlyName}</Typography>
            <Typography variant="caption" color="text.secondary">{option.entityId}</Typography>
            <Typography variant="body2">{option.state || 'Värde saknas'} {option.unit ?? ''} · {formatRelative(option.lastUpdatedUtc)}</Typography>
            <Box><ChoiceChip result={result} /></Box>
            {result.quality !== 'Valid' && <Typography variant="caption" color="text.secondary">{result.reason}</Typography>}
          </Stack>
        </Box>;
      }}
      renderInput={(params) => <TextField
        {...params}
        label={label}
        required={required}
        error={(required && !entityId) || (Boolean(entityId) && quality.quality === 'Invalid')}
        helperText={entityId || 'Inte mappad'}
        slotProps={{ formHelperText: { sx: { overflowWrap: 'anywhere' } } }}
      />}
    />
    {entityId && <Stack spacing={.5} sx={{ mt: 1, overflowWrap: 'anywhere' }} role="status" aria-live="polite" aria-atomic="true">
      <Box><ChoiceChip result={quality} /></Box>
      {!catalog.issue && found && <Typography variant="body2">
        Senast mottaget värde: {found.state || 'saknas'} {found.unit ?? ''}.
        {' '}HA uppdaterad {formatRelative(found.lastUpdatedUtc)}, mottaget {formatRelative(found.receivedAtUtc)}.
        {found.lastReportedUtc && <> Senast rapporterat av HA-integrationen {formatRelative(found.lastReportedUtc)}.</>}
      </Typography>}
      <Typography variant="body2" color="text.secondary">{quality.reason}</Typography>
    </Stack>}
  </Box>;
}

function ChoiceChip({ result }: { result: EntityChoiceQuality }) {
  return result.quality === 'Valid'
    ? <Chip size="small" variant="outlined" color="success" icon={<CheckCircleOutlineIcon />} label="Värde/enhet OK" />
    : <QualityChip quality={result.quality} />;
}
