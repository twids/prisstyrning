async function j(url, opts){const r=await fetch(url,opts);if(!r.ok) throw new Error(r.status+" "+url);return r.json();}
const authStatusEl=document.getElementById('authStatus');
const schedulePreviewEl=document.getElementById('schedulePreview');
const daikinDataEl=document.getElementById('daikinData');
const gatewayDataEl=document.getElementById('gatewayData');
const currentScheduleRawEl=document.getElementById('currentScheduleRaw');
const toggleCurrentRawBtn=document.getElementById('toggleCurrentRaw');
if(toggleCurrentRawBtn && currentScheduleRawEl){
  toggleCurrentRawBtn.onclick=()=>{
    const vis = currentScheduleRawEl.style.display!=='none';
    currentScheduleRawEl.style.display = vis?'none':'block';
    toggleCurrentRawBtn.textContent = vis? 'Visa rå JSON':'Dölj rå JSON';
  };
}

async function loadAuth(){
  try{
    const d=await j('/auth/daikin/status');
    const expiresStr = d.expiresAtUtc ? new Date(d.expiresAtUtc).toLocaleTimeString(navigator.language) : '';
    authStatusEl.textContent = d.authorized ? `Authorized (expires ${expiresStr})` : 'Not authorized';
    authStatusEl.className = d.authorized ? 'good' : 'bad';
  }
  catch(e){authStatusEl.textContent='Fel: '+e.message;authStatusEl.className='bad';}
}
async function loadPrices(){
  try{
    const d=await j('/api/prices/memory');
    // Update chart and meta only
    const meta=document.getElementById('latestPriceMeta');
  if(d.updated && meta) meta.textContent='Last updated: '+new Date(d.updated).toLocaleString(navigator.language, { dateStyle: 'medium', timeStyle: 'medium' });
    const allPrices = [];
    if(Array.isArray(d.today)) allPrices.push(...d.today);
    if(Array.isArray(d.tomorrow)) allPrices.push(...d.tomorrow);
    if(allPrices.length){
      renderPriceChart(allPrices);
    }
  }catch(e){
    const meta=document.getElementById('latestPriceMeta');
    if(meta) meta.textContent='Fel: '+e.message;
  }
}
function renderPriceChart(prices){
  const ctx = document.getElementById('chart').getContext('2d');
  // Separera dagens och morgondagens priser
  const today = [], tomorrow = [];
  for(const p of prices){
    // Extrahera tid exakt som i prisfilen, utan Date-konvertering
    let dateStr = p.start.replace(/\u002B/g, '+');
    dateStr = dateStr.replace(/(\.\d{7})/, '');
    // Behåll strängen som x-värde
    const x = dateStr;
    // Jämför datumdelen
    const dayStr = dateStr.slice(0,10);
    const todayStr = (new Date()).toISOString().slice(0,10);
    if(dayStr === todayStr){
      today.push({ x, y: p.value });
    }else{
      tomorrow.push({ x, y: p.value });
    }
  }
  if(window._priceChart) window._priceChart.destroy();
  window._priceChart = new Chart(ctx, {
    type: 'line',
    data: { datasets: [
    today.length ? { label: 'Today', data: today, borderColor: '#1976d2', backgroundColor: 'rgba(25,118,210,0.1)', fill: true } : null,
    tomorrow.length ? { label: 'Tomorrow', data: tomorrow, borderColor: '#FFB74D', backgroundColor: 'rgba(255,183,77,0.1)', fill: true } : null
    ].filter(Boolean) },
    options: {
      scales: {
        x: {
          type: 'time',
          time: {
            unit: 'hour',
            tooltipFormat: 'Pp',
            displayFormats: {
              hour: 'HH:mm'
            }
          },
          ticks: {
            callback: function(value, index, ticks) {
              // Format tick labels using browser locale
              const dt = new Date(value);
              return dt.toLocaleTimeString(navigator.language, { hour: '2-digit', minute: '2-digit' });
            }
          }
        },
        y: { beginAtZero: true }
      },
      plugins: {
        legend: { display: true },
        tooltip: {
          callbacks: {
            title: function(context) {
              // Format tooltip title using browser locale
              const dt = new Date(context[0].parsed.x);
              return dt.toLocaleString(navigator.language);
            }
          }
        }
      }
    }
  });
}

// Zon-hantering
// User settings (only present on settings page)
const comfortHoursEl = document.getElementById('comfortHours');
if(comfortHoursEl){
  const turnOffPercentileEl = document.getElementById('turnOffPercentile');
  const turnOffMaxConsecutiveEl = document.getElementById('turnOffMaxConsecutive');
  const saveUserSettingsBtn = document.getElementById('saveUserSettings');
  const userSettingsStatus = document.getElementById('userSettingsStatus');

  (async function loadUserSettings(){
    try {
      const res = await fetch('/api/user/settings');
      if(!res.ok) throw new Error('Could not load settings');
      const d = await res.json();
  comfortHoursEl.value = d.comfortHours ?? 3;
  if(turnOffPercentileEl) turnOffPercentileEl.value = d.turnOffPercentile ?? 0.9;
  if(turnOffMaxConsecutiveEl) turnOffMaxConsecutiveEl.value = d.turnOffMaxConsecutive ?? 2;
      if(userSettingsStatus) userSettingsStatus.textContent = '';
    } catch(e) { if(userSettingsStatus) userSettingsStatus.textContent = 'Error: ' + e.message; }
  })();

  async function saveUserSettings(){
    if(userSettingsStatus) userSettingsStatus.textContent = 'Saving...';
    try {
      const body = {
        ComfortHours: comfortHoursEl.value || 3,
        TurnOffPercentile: turnOffPercentileEl?.value || 0.9,
        TurnOffMaxConsecutive: turnOffMaxConsecutiveEl?.value || 2
      };
      const res = await fetch('/api/user/settings', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
      if(!res.ok) throw new Error(await res.text());
      if(userSettingsStatus) userSettingsStatus.textContent = 'Saved!';
    } catch(e){ if(userSettingsStatus) userSettingsStatus.textContent = 'Error: ' + e.message; }
  }
  if(saveUserSettingsBtn) saveUserSettingsBtn.onclick = saveUserSettings;
}
const zoneSelect=document.getElementById('zoneSelect');
const saveZoneBtn=document.getElementById('saveZone');
const refreshNordpoolBtn=document.getElementById('refreshNordpool');
const zoneStatus=document.getElementById('zoneStatus');

async function loadZone(){
  if(!zoneSelect) return;
  try{ const d=await j('/api/prices/zone');
    if(d.zone){
      zoneSelect.value=d.zone;
      const zoneLabel = document.getElementById('zoneLabel');
      if(zoneLabel) zoneLabel.textContent = 'Active zone: ' + d.zone;
    }
  }
  catch(e){
    const zoneLabel = document.getElementById('zoneLabel');
    if(zoneLabel) zoneLabel.textContent = 'Zone error: ' + e.message;
  }
}
async function saveZone(){
  if(!zoneSelect) return; zoneStatus.textContent='Sparar...';
  try{
    const z=zoneSelect.value;
    const r=await fetch('/api/prices/zone',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({zone:z})});
    const txt=await r.text(); if(!r.ok) throw new Error(txt);
    zoneStatus.textContent='Sparad ('+z+')';
  }catch(e){ zoneStatus.textContent='Fel: '+e.message; }
}
async function refreshNordpool(){
  if(!zoneSelect) return; zoneStatus.textContent='Hämtar...';
  try{
    const r=await fetch('/api/prices/refresh',{method:'POST'});
    const txt=await r.text(); if(!r.ok) throw new Error(txt);
    try{ const obj=JSON.parse(txt); zoneStatus.textContent='Uppdaterad ('+obj.zone+')'; }catch{ zoneStatus.textContent='Uppdaterad'; }
    await loadPrices();
  }catch(e){ zoneStatus.textContent='Fel: '+e.message; }
}
if(saveZoneBtn) saveZoneBtn.onclick=saveZone;
if(refreshNordpoolBtn) refreshNordpoolBtn.onclick=refreshNordpool;
loadZone();
function renderScheduleGrid(schedulePayload, targetId){
  const grid=document.getElementById(targetId);
  grid.innerHTML='';
  if(!schedulePayload){ grid.textContent='Inget schema'; return; }
  // payload format: {"0": { actions: { monday: {"10:00:00": {domesticHotWaterTemperature:"eco"}, ... } } } }
  const firstKey=Object.keys(schedulePayload)[0];
  if(!firstKey) { grid.textContent='Tomt'; return; }
  const actions=schedulePayload[firstKey]?.actions||schedulePayload[firstKey]?.Actions||{};
  const dayNames=Object.keys(actions);
  if(dayNames.length===0){grid.textContent='Inga dagar';return;}
  // Header row
  const header=document.createElement('div');header.className='day-row';
  const hLabel=document.createElement('div');hLabel.className='hdr';hLabel.textContent='Dag';header.appendChild(hLabel);
  for(let h=0;h<24;h++){const c=document.createElement('div');c.className='hdr';c.textContent=h.toString().padStart(2,'0');header.appendChild(c);}grid.appendChild(header);
  const now=new Date();const nowHour=now.getHours();const todayName=now.toLocaleDateString('en-GB',{weekday:'long'}).toLowerCase();
  for(const day of dayNames){
    const row=document.createElement('div');row.className='day-row';
    const lbl=document.createElement('div');lbl.className='day-label';lbl.textContent=day;row.appendChild(lbl);
    const actionMap=actions[day]||{};
    // Build hourly state; default carry previous or none => blank
    let prev=null;
    const hourly=new Array(24).fill(null);
    // Each key like HH:MM:SS
    Object.keys(actionMap).sort().forEach(k=>{ const hr=parseInt(k.split(':')[0]); const v=actionMap[k]; const state=v?.domesticHotWaterTemperature||v?.roomTemperature||v; if(!isNaN(hr)) hourly[hr]=state; });
    for(let h=0;h<24;h++){
      if(!hourly[h]) hourly[h]=prev; else prev=hourly[h];
    }
    for(let h=0;h<24;h++){
      const cell=document.createElement('div');
      const state=hourly[h];
      if(state){
        let cls=state;
        if(cls==='turn_off') cls='off';
        cell.className='cell '+cls;
        const span=document.createElement('span');span.textContent=state==='turn_off'?'off':state[0];cell.appendChild(span);
      } else { cell.className='cell'; }
      if(day===todayName && h===nowHour) cell.classList.add('now');
      row.appendChild(cell);
    }
  grid.appendChild(row);
  }
}

async function loadSchedule(){
  const msgEl=document.getElementById('scheduleMsg');
  schedulePreviewEl.textContent='...'; msgEl.textContent='';
  try{
    const d=await j('/api/schedule/preview');
    schedulePreviewEl.textContent=JSON.stringify(d,null,2);
    renderScheduleGrid(d.schedulePayload,'scheduleGrid');
    msgEl.textContent=d.message||'';
  window._lastPreview = d; // cache for PUT
  }catch(e){schedulePreviewEl.textContent='Fel: '+e.message;}
}
async function loadCurrentSchedule(){
  currentScheduleRawEl.textContent='...';
  const grid=document.getElementById('currentScheduleGrid');
  grid.textContent='Laddar...';
  try{
    // Försök explicit embeddedId=2 först
    let d;
    try { d = await j('/api/daikin/gateway/schedule?embeddedId=2'); } catch(e){ d = await j('/api/daikin/gateway/schedule'); }
    // Hämta hela gateway-listan för att extrahera MP 2 (DHW)
    let gatewayRaw = null; let dhwMp=null; let gwDevice=null;
    try {
      gatewayRaw = await j('/api/daikin/gateway?debug=true');
      if(Array.isArray(gatewayRaw) && gatewayRaw.length){
        gwDevice = gatewayRaw.find(dev=>dev && dev.managementPoints && dev.managementPoints.some(mp=>mp.embeddedId==='2')) || gatewayRaw[0];
        if(gwDevice && Array.isArray(gwDevice.managementPoints)){
          dhwMp = gwDevice.managementPoints.find(mp=>mp && mp.embeddedId==='2');
        }
      }
    } catch(_){ }

    // Fallback: bygg schedulePayload från dhwMp om backend ej hittade
  if((!d.schedulePayload || Object.keys(d.schedulePayload||{}).length===0) && dhwMp && dhwMp.schedule){
      try {
    // schema finns under schedule.value för DHW
    const sch = dhwMp.schedule?.value || dhwMp.schedule;
        let container=null; let detectedMode=null; let currentId=null;
        if(sch.modes){
          for(const k of Object.keys(sch.modes)){
            const m=sch.modes[k];
            if(!detectedMode && m && m.schedules){ detectedMode=k; container=m.schedules; }
            if(m && m.currentSchedule && m.currentSchedule.value) currentId = m.currentSchedule.value;
          }
        }
        if(!container && sch.schedules) container=sch.schedules;
        if(container && typeof container==='object'){
          const keys=Object.keys(container);
            if(keys.length){
              const chosen = currentId && container[currentId]? currentId : keys[0];
              const chosenObj = container[chosen];
              if(chosenObj && chosenObj.actions){
                const root={}; root[chosen]={ actions: chosenObj.actions }; d.schedulePayload=root; d.chosenScheduleId=chosen; if(!d.detectedMode && detectedMode) d.detectedMode=detectedMode;
              }
            }
        }
      } catch(_){ }
    }
    currentScheduleRawEl.textContent=JSON.stringify(d,null,2);
    renderScheduleGrid(d.schedulePayload,'currentScheduleGrid');
    // Autofyll fält för PUT schedule
    try {
      const gw=document.getElementById('putGatewayDeviceId');
      const emb=document.getElementById('putEmbeddedId');
      const act=document.getElementById('putActivateId');
      if(gw && d.deviceId) gw.value = d.deviceId;
      if(emb){ if(d.embeddedId) emb.value = d.embeddedId; else if(!emb.value) emb.value='2'; }
      const scheduleId = d.currentScheduleId || d.chosenScheduleId;
      if(act && scheduleId && !act.value) act.value = scheduleId;
      // För att visa komplett DHW-info: kombinera schedule + mp
      if(dhwMp){
        // Rå JSON (om användare väljer att visa) – visa schedule.value om finns
        try { currentScheduleRawEl.textContent = JSON.stringify({ schedulePayload: d.schedulePayload }, null, 2); } catch{}
      }
      window._lastCurrentSchedule = d;
    } catch(_){}
  }catch(e){ currentScheduleRawEl.textContent='Fel: '+e.message; grid.textContent='Fel'; }
}
async function startAuth(){
  try{const d=await j('/auth/daikin/start');window.location=d.url;}catch(e){alert('Auth start fel: '+e.message);} }
async function loadSites(){
  daikinDataEl.textContent='...';
  try{
    const s=await j('/api/daikin/sites');
    if (typeof s === 'string') {
      // API kan returnera rå JSON-sträng
      try { daikinDataEl.textContent = JSON.stringify(JSON.parse(s), null, 2); }
      catch { daikinDataEl.textContent = s; }
    } else {
      daikinDataEl.textContent = JSON.stringify(s,null,2);
    }
  }catch(e){daikinDataEl.textContent='Fel: '+e.message;}
}
async function loadGateway(){
  if(!gatewayDataEl) return;
  gatewayDataEl.textContent='...';
  try{
    const g=await j('/api/daikin/gateway?debug=true');
    gatewayDataEl.textContent=JSON.stringify(g,null,2);
  }catch(e){ gatewayDataEl.textContent='Fel gateway: '+e.message; }
}

const refreshPricesBtn = document.getElementById('refreshPrices');
if(refreshPricesBtn) refreshPricesBtn.onclick = loadPrices;

const loadScheduleBtn = document.getElementById('loadSchedule');
if(loadScheduleBtn) loadScheduleBtn.onclick = loadSchedule;

const btnCur = document.getElementById('loadCurrentSchedule');
if(btnCur) btnCur.onclick = loadCurrentSchedule;

const authStartBtn = document.getElementById('authStart');
if(authStartBtn) authStartBtn.onclick = startAuth;

const showSitesBtn = document.getElementById('showSites');
if(showSitesBtn) showSitesBtn.onclick = loadSites;

const gwBtn = document.getElementById('showGateway');
if(gwBtn) gwBtn.onclick = loadGateway;

window.addEventListener('DOMContentLoaded', () => {
  loadAuth();
  loadPrices();
  // Do NOT load gateway automatically on page load. Only load on explicit user action (button click)
});

// Autofyll gatewayDeviceId & embeddedId direkt vid sidladdning
async function tryAutoFillScheduleIds(){
  const gwEl=document.getElementById('putGatewayDeviceId');
  const embEl=document.getElementById('putEmbeddedId');
  if(!gwEl||!embEl) return;
  if(gwEl.value && embEl.value) return; // redan ifyllt
  try{
    const d=await j('/api/daikin/gateway/schedule');
    if(d.deviceId && !gwEl.value) gwEl.value=d.deviceId;
    if(d.embeddedId && !embEl.value) embEl.value=d.embeddedId;
    // Förifyll aktiverings-id om tomt
    const act=document.getElementById('putActivateId');
    if(act && !act.value){ const sid=d.currentScheduleId||d.chosenScheduleId; if(sid) act.value=sid; }
  }catch(e){ /* tyst */ }
}
tryAutoFillScheduleIds();

// Hämta gateway vid sidladdning och fyll id-fält om tomma
async function initialGatewayPopulate(){
  try{
    const gwResp = await j('/api/daikin/gateway');
    if(gatewayDataEl) gatewayDataEl.textContent = JSON.stringify(gwResp,null,2);
    if(Array.isArray(gwResp) && gwResp.length>0){
      const first = gwResp[0];
      const gwEl=document.getElementById('putGatewayDeviceId');
      const embEl=document.getElementById('putEmbeddedId');
      const actEl=document.getElementById('putActivateId');
      if(first && gwEl && !gwEl.value && first.id) gwEl.value=first.id;
      // Leta managementPoint
      if(first && first.managementPoints && Array.isArray(first.managementPoints)){
        // Först leta domesticHotWaterTank, annars climateControl
        const mps = first.managementPoints;
        let pick = mps.find(mp=>mp && mp.managementPointType==='domesticHotWaterTank');
        if(!pick) pick = mps.find(mp=>mp && mp.managementPointType==='climateControl');
        if(pick){
          if(embEl && !embEl.value && pick.embeddedId) embEl.value=pick.embeddedId;
          try{
            const modes = pick.schedule?.modes?.heating;
            const curId = modes?.currentSchedule?.value;
            if(actEl && !actEl.value && curId) actEl.value=curId;
          }catch{}
        }
      }
    }
  }catch(e){ /* tyst */ }
}
// initialGatewayPopulate(); // Disabled: Only load gateway on explicit user action

// Apply schedule PUT
const applyBtn=document.getElementById('applyScheduleBtn');
if(applyBtn){
  applyBtn.onclick=async()=>{
    const resEl=document.getElementById('applyResult');
    resEl.textContent='Skickar...';
    try{
      // Om id saknas, försök autohämta först
      const gwEl=document.getElementById('putGatewayDeviceId');
      const embEl=document.getElementById('putEmbeddedId');
      if(gwEl && embEl && (!gwEl.value || !embEl.value)){
        await tryAutoFillScheduleIds();
      }
      if(!window._lastPreview || !window._lastPreview.schedulePayload) throw new Error('Ingen preview schedulePayload');
      const gatewayDeviceId=document.getElementById('putGatewayDeviceId').value.trim();
      const embeddedId=document.getElementById('putEmbeddedId').value.trim();
      const mode=document.getElementById('putMode').value.trim()||'heating';
      const activateId=document.getElementById('putActivateId').value.trim();
      const body={ gatewayDeviceId, embeddedId, mode, schedulePayload: window._lastPreview.schedulePayload };
      if(activateId) body.activateScheduleId=activateId;
      const r=await fetch('/api/daikin/gateway/schedule/put',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
      const txt=await r.text();
      if(!r.ok) throw new Error(txt);
      try{ const obj=JSON.parse(txt); resEl.textContent='OK '+JSON.stringify(obj); }
      catch{ resEl.textContent='OK'; }
    }catch(e){ resEl.textContent='Fel: '+e.message; }
  };
}
