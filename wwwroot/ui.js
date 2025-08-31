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
async function loadSchedule(){
  schedulePreviewEl.textContent='...';
  try{const d=await j('/api/schedule/preview');schedulePreviewEl.textContent=JSON.stringify(d,null,2);}catch(e){schedulePreviewEl.textContent='Fel: '+e.message;}
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
