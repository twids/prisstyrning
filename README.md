# Prisstyrning

Prisbaserad schemagenerering för Daikin (DHW) med integration mot Home Assistant.

## Funktioner
- Hämtar timpriser via Home Assistant sensor
- Genererar DHW-schema (comfort/eco/turn_off) med max 4 actions/dag och turn_off ≤ 2h
- Visar och laddar upp schema till Daikin gateway (manual PUT, ingen automatisk applicering om inte konfigurerat)
- Stöd för både `appsettings.json` och miljövariabler (env vars prioriteras)
- Frontend med prisgraf och schema-grid

## Konfiguration
Hierarki (högst prioritet först):
1. Miljövariabler (prefixed `PRISSTYRNING_` eller utan prefix)
2. `appsettings.development.json`
3. `appsettings.json`

### Viktiga nycklar
| Sektion | Nyckel | Miljövariabel | Beskrivning |
|--------|-------|---------------|-------------|
| HomeAssistant | BaseUrl | `PRISSTYRNING_HomeAssistant__BaseUrl` | URL till HA (ex http://homeassistant:8123) |
| HomeAssistant | Token | `PRISSTYRNING_HomeAssistant__Token` | Long-lived Access Token |
| HomeAssistant | Sensor | `PRISSTYRNING_HomeAssistant__Sensor` | Sensor-id för prisdata |
| Daikin | ApplySchedule | `PRISSTYRNING_Daikin__ApplySchedule` | true/false om schemat får pushas automatiskt |
| Daikin | AccessToken | `PRISSTYRNING_Daikin__AccessToken` | (Valfritt) injicera access token |
| Daikin | RefreshToken | `PRISSTYRNING_Daikin__RefreshToken` | (Valfritt) refresh token |
| Daikin:Http | Log | `PRISSTYRNING_Daikin__Http__Log` | Logga HTTP (true/false) |
| Daikin:Http | LogBody | `PRISSTYRNING_Daikin__Http__LogBody` | Logga body (true/false) |
| Daikin:Http | BodySnippetLength | `PRISSTYRNING_Daikin__Http__BodySnippetLength` | Max antal tecken av body |
| Schedule | ComfortHours | `PRISSTYRNING_Schedule__ComfortHours` | Antal timmar comfort-block |
| Schedule | TurnOffPercentile | `PRISSTYRNING_Schedule__TurnOffPercentile` | Percentil gräns för turn_off |
| Schedule | TurnOffMaxConsecutive | `PRISSTYRNING_Schedule__TurnOffMaxConsecutive` | Max sammanhängande turn_off timmar (innan komprimering) |
| Storage | Directory | `PRISSTYRNING_Storage__Directory` | Katalog för persisterad pris/schedule snapshot |
| Root | PORT | `PRISSTYRNING_PORT` | Lyssningsport |

Dubbelunderscore `__` används för att representera kolon / nested sektioner i .NET config.

## Kör lokalt med Docker
Bygg image:
```bash
docker build -t prisstyrning:local .
```

Starta container (exempel):
```bash
docker run --rm -p 5000:5000 \
  -e PRISSTYRNING_HomeAssistant__BaseUrl=http://homeassistant:8123 \
  -e PRISSTYRNING_HomeAssistant__Token=REDACTED \
  -e PRISSTYRNING_HomeAssistant__Sensor=sensor.nordpool_sell \
  -e PRISSTYRNING_Storage__Directory=/data \
  -v $(pwd)/data:/data \
  prisstyrning:local
```

## docker-compose
Se `docker-compose.example.yml`:
```yaml
version: '3.9'
services:
  prisstyrning:
    image: ghcr.io/your-org/prisstyrning:latest
    environment:
      PRISSTYRNING_HomeAssistant__BaseUrl: http://homeassistant:8123
      PRISSTYRNING_HomeAssistant__Token: REDACTED
      PRISSTYRNING_HomeAssistant__Sensor: sensor.nordpool_sell
      PRISSTYRNING_Storage__Directory: /data
      PRISSTYRNING_Daikin__ApplySchedule: "false"
    volumes:
      - ./data:/data
    ports:
      - "5000:5000"
```
Starta:
```bash
docker compose -f docker-compose.example.yml up -d
```

## GitHub Container Registry
Workflow finns i `.github/workflows/container.yml` och pushar till `ghcr.io/<owner>/prisstyrning` vid push på master/tag.

## OAuth Tokens
Efter OAuth-flödet sparas tokens i `tokens/daikin.json` (om filvolym mountas). Du kan också injicera miljövariabler för engångstest.

## Licens
Ingen licens specificerad ännu.
