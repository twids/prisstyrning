import { Alert, Box, Chip, Stack, Typography } from '@mui/material';
import type { ThermalConfig } from '../../types/api';

// Mirrors ThermalModelTrainingData and ThermalPlanningInputs.RequiredSignalRoles.
// These describe the CURRENT implementation, not a promise of readiness.
export const sensorRequirements: Record<string, { label: string; required: boolean; purpose: string; limitation?: string }> = {
  outside_temperature: { label: 'Krävs för husmodellen', required: true, purpose: 'Aktuell utetemperatur för husets värmeförluster. En väderprognos ersätter inte denna mätning.' },
  leaving_water_temperature: { label: 'Krävs för husmodellen', required: true, purpose: 'Uppmätt framledning, inte börvärdet. Används med retur och flöde för att beräkna avgiven värme.' },
  return_water_temperature: { label: 'Krävs för husmodellen', required: true, purpose: 'Uppmätt returtemperatur behövs för temperaturdifferensen över värmesystemet.' },
  flow: { label: 'Krävs för husmodellen', required: true, purpose: 'Vattenflöde tillsammans med LWT och RWT ger värmeeffekt. Noll är normalt vid stillestånd.' },
  dhw_active: { label: 'Krävs för husmodellen', required: true, purpose: 'Skiljer faktisk varmvattenproduktion från husvärme. Välj en av/på-signal eller härledd hjälpsensor, inte bara ett tillåtet driftläge.' },
  defrost_active: { label: 'Givare eller ej tillämpligt', required: true, purpose: 'Hindrar att avfrostning blandas in i träning och styrning.', limitation: 'För bergvärme utan avfrostning: välj uttryckligen ”Anläggningen har ingen avfrostning”. Ingen HA-hjälpsensor behövs. Saknad givare tolkas aldrig automatiskt som avstängd.' },
  brine_in: { label: 'Krävs för COP och planering', required: true, purpose: 'Ingående köldbärartemperatur används för att modellera och förutsäga verkningsgraden.' },
  tank_temperature: { label: 'Krävs för gemensam plan', required: true, purpose: 'Behövs för att planera varmvatten och reservera tid då kompressorn inte värmer huset.' },
  backup_heater_active: { label: 'Krävs för COP och planering', required: true, purpose: 'Skiljer elpatron från kompressor. Välj av/på-status; en effektgivare kan omvandlas till en HA-hjälpsensor med effekt > 0.' },
  heat_pump_power: { label: 'Krävs i nuvarande planering', required: true, purpose: 'Elektrisk effekt i kW, inte ampere eller avgiven värme. Separat elmätning behöver verifieras.', limitation: 'Extern COP kan användas för COP-träning utan separat elmätare, men planeringens nuvarande inläsning kräver fortfarande detta effektvärde.' },
  property_power: { label: 'Krävs i nuvarande planering', required: true, purpose: 'Fastighetens importerade effekt används som underlag för hushållslast och eventuell effekttariff.', limitation: 'Även med effekttariff avstängd kräver dagens planerare värdet. Tariffen bidrar fortfarande med noll när den är avstängd.' },
  weather_forecast: { label: 'Krävs för planering', required: true, purpose: 'Minst 24 sammanhängande timmar med temperaturprognos. En weather-entity kan även leverera vind och molnighet; testet visar vilket innehåll som finns.' },
  heating_deviation: { label: 'Inför aktiv LWT-styrning', required: false, purpose: 'Numerisk återkoppling från exempelvis sensor.bridge0_lwt_deviation_heating, inte absolut LWT-börvärde. Skrivreglaget (number eller climate) väljs separat under Home Assistant → styrning.', limitation: 'RT-läge med avvikelse noll bevisar inte en verifierad grundkurva. Grundkurveprov kräver en kontrollerad manuell övergång till LWT/väderkurva; Shadow ändrar inte pumpens läge.' },
  cop_realtime: { label: 'Alternativ COP-källa', required: false, purpose: 'Välj extern realtids-COP eller använd egen beräkning med verifierad elmätning. Extern COP behöver giltigt belastningsunderlag och driftstatus; medel-COP ersätter inte detta.' },
  cop_average: { label: 'Valfri uppföljning', required: false, purpose: 'Ger en långsiktig jämförelse av verkningsgraden. Används inte som realtids-COP eller för modellträning.' },
  brine_out: { label: 'Valfri uppföljning', required: false, purpose: 'Ger extra insyn i köldbärarkretsen. Inte ett krav för nuvarande modellträning eller planering.' },
  wind_speed: { label: 'Valfri förbättring', required: false, purpose: 'Kan förbättra modellen för vindpåverkan om valideringen visar nytta. En separat mätning kan ge träningsdata; prognosvind kan komma från vald weather-entity.' },
  solar_irradiance: { label: 'Valfri förbättring', required: false, purpose: 'Kan förbättra modellen för solvärmetillskott om valideringen visar nytta. Molnighet är inte en uppmätt solinstrålning i W/m².' },
  spot_price: { label: 'Valfri uppföljning', required: false, purpose: 'Visar aktuellt spotpris från HA. Planeraren hämtar sin prisserie separat från Nord Pool och kontots elområde; detta fält ersätter inte prisprognosen.' },
};

export function SensorRequirement({ role, selected }: { role: string; selected: boolean }) {
  const requirement = sensorRequirements[role];
  if (!requirement) return null;
  return <Stack spacing={1}>
    <Box><Chip size="small" variant="outlined" color={requirement.required ? 'primary' : 'default'} label={requirement.label}
      sx={{ height: 'auto', '& .MuiChip-label': { whiteSpace: 'normal', py: .5 } }} /></Box>
    <Typography variant="body2">{requirement.purpose}</Typography>
    <Typography variant="caption">{selected ? 'Datakälla vald – kvalitet kontrolleras separat.' : requirement.required ? 'Saknas eller är avstängd – behövs innan modellen eller planeringen kan använda den.' : 'Kan lämnas tom i denna etapp.'}</Typography>
    {requirement.limitation && <Typography variant="body2" color="text.secondary">{requirement.limitation}</Typography>}
  </Stack>;
}

export function SensorRequirementsSummary({ draft, labels }: { draft: ThermalConfig; labels: readonly (readonly [string, string, string])[] }) {
  const required = labels.filter(([role]) => sensorRequirements[role]?.required);
  const missing = required.filter(([role]) => !draft.entities.some(entity => entity.role === role && entity.enabled && (entity.entityId.trim() || role === 'defrost_active' && entity.notApplicable)));
  const criticalRoom = draft.rooms.some(room => room.enabled && room.isCritical && room.entityId.trim());
  return <Stack spacing={1} component="section" aria-label="Vad behöver fyllas i?">
    <Typography variant="h6" component="h3">Vad behöver fyllas i?</Typography>
    <Typography>”Krävs” avser underlag för modell eller planering. ”Inför aktiv LWT-styrning” behövs senare. Valfria givare ger förbättring eller uppföljning, men garanterar inte bättre resultat.</Typography>
    <Alert role="status" aria-live="polite" aria-label="Valda datakällor" severity={missing.length || !criticalRoom ? 'info' : 'success'}>
      {required.length - missing.length} av {required.length} nödvändiga datakällor är valda i utkastet. Detta är inte ett godkännande av mätdata, modell eller aktiv styrning.
      {missing.length > 0 && <Typography variant="body2" sx={{ mt: 1 }}>Saknas eller avstängda: {missing.map(([, label]) => label).join(', ')}.</Typography>}
      <Typography variant="body2" sx={{ mt: 1 }}>{criticalRoom ? 'Minst ett kritiskt rum har en vald givare.' : 'Under Rum behövs också minst ett aktiverat, kritiskt rum med temperaturgivare.'}</Typography>
    </Alert>
    <Typography variant="body2" color="text.secondary">Du kan spara stegvis. Saknade modell- och planeringsvärden är inte nya spärrar för att spara eller starta skrivfri Shadow; lägesguiden visar villkoren för lägesbyte. Ett valt värde kan fortfarande vara felaktigt eller otillgängligt.</Typography>
  </Stack>;
}
