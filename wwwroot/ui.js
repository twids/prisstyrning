async function j(url, opts){const r=await fetch(url,opts);if(!r.ok) throw new Error(r.status+" "+url);return r.json();}
const authStatusEl=document.getElementById('authStatus');
const priceDataEl=document.getElementById('priceData');
const schedulePreviewEl=document.getElementById('schedulePreview');
const daikinDataEl=document.getElementById('daikinData');

async function loadAuth(){
  try{const d=await j('/auth/daikin/status');authStatusEl.textContent=d.authorized?`Authorized (expires ${new Date(d.expiresAtUtc).toLocaleTimeString()})`:'Inte auktoriserad';authStatusEl.className=d.authorized?'good':'bad';}
  catch(e){authStatusEl.textContent='Fel: '+e.message;authStatusEl.className='bad';}
}
async function loadPrices(){
  priceDataEl.textContent='...';
  try{const d=await j('/api/prices/memory');priceDataEl.textContent=JSON.stringify(d,null,2);}catch(e){priceDataEl.textContent='Fel: '+e.message;}
}
function renderScheduleGrid(schedulePayload){
  const grid=document.getElementById('scheduleGrid');
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
    renderScheduleGrid(d.schedulePayload);
    msgEl.textContent=d.message||'';
  }catch(e){schedulePreviewEl.textContent='Fel: '+e.message;}
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

document.getElementById('refreshPrices').onclick=loadPrices;

document.getElementById('loadSchedule').onclick=loadSchedule;

document.getElementById('authStart').onclick=startAuth;

document.getElementById('showSites').onclick=loadSites;

loadAuth();loadPrices();
