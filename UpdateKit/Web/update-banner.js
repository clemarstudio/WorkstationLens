/*
 * Clemar.UpdateKit / lightweight UI
 * Host app includes this script (no dependencies), provides a div#update-host,
 * and uses the JSON contract described in UpdateKit/README.md.
 */
(() => {
  'use strict';
  const escape = value => String(value).replace(/[&<>"']/g, ch =>
    ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  window.mountClemarUpdater = function({
    host = '#update-host',
    api = '/api/update',
    intervalMs = 15000,
    appName = 'Application'
  } = {}) {
    const root = document.querySelector(host);
    if (!root) return { destroy(){} };
    const title = escape(appName);
    let dismissed = null, stopped = false, waiting = false;
    const styles = document.createElement('style');
    styles.textContent = `
      .clemar-update { position:sticky;top:0;z-index:1000;display:flex;gap:12px;align-items:center;
         justify-content:space-between;flex-wrap:wrap;padding:11px 16px;background:#172d40;
         border:1px solid #346078;border-radius:11px;color:#ecf6ff;font:13px/1.4 system-ui, sans-serif;
         box-shadow:0 5px 18px rgba(0,0,0,.16);margin-bottom:12px; }
      .clemar-update strong {font-weight:700}
      .clemar-update-controls{display:flex;gap:8px;align-items:center}
      .clemar-update button{background:#317ab6;color:white;border:0;border-radius:8px;padding:7px 12px;
         cursor:pointer;font:inherit}.clemar-update button.secondary{background:transparent;color:#b9d8ec;
         border:1px solid #426078}.clemar-update button:disabled{opacity:.6;cursor:default}
      .clemar-update[hidden]{display:none}
      .clemar-update-progress{font-variant-numeric:tabular-nums;opacity:.82}
    `;
    root.appendChild(styles);
    const panel = document.createElement('div');
    panel.className = 'clemar-update';panel.hidden = true;
    panel.setAttribute('role','status');
    panel.setAttribute('aria-live','polite');
    root.appendChild(panel);

    async function command(suffix, value){
      const response = await fetch(api+suffix,{method:'POST',cache:'no-store',
        headers:{'X-Clemar-Update':'1','Content-Type':'application/json'},body:JSON.stringify(value||{})});
      if(!response.ok)throw new Error('Update action failed: HTTP '+response.status);
      const body = await response.text();
      return body ? JSON.parse(body) : {};
    }
    function render(state){
      if(!state || !state.canAutoUpdate){panel.hidden=true;return;}
      if(dismissed===state.latestVersion && state.stage!=='installing'){panel.hidden=true;return;}
      const version=escape(state.latestVersion||'');
      if(state.stage==='downloading'){
        panel.innerHTML=`<div><strong>${title}: downloading update ${version}</strong> <span class="clemar-update-progress">${Number(state.progressPercent)||0}%</span><div>The current version remains usable.</div></div>`;
      } else if(state.stage==='ready'){
        panel.innerHTML=`<div><strong>${title} ${version} is ready.</strong><div>Install now or keep working. It will install automatically when you next open the app.</div></div>
          <div class="clemar-update-controls"><button data-action="install">Restart &amp; install</button>
          <button data-action="later" class="secondary">Later</button></div>`;
        panel.querySelector('[data-action="later"]').addEventListener('click',()=>{dismissed=state.latestVersion;panel.hidden=true;});
        panel.querySelector('[data-action="install"]').addEventListener('click',async()=>{
          const control=panel.querySelector('[data-action="install"]');control.disabled=true;control.textContent='Saving and restarting…';
          try{await command('/install');}
          catch(error){control.disabled=false;control.textContent='Restart & install';window.alert(error.message+' Your current version is still running.');}
        });
      } else if(state.stage==='installing'){
        panel.innerHTML='<div><strong>Saving work and handing over to the installer…</strong></div>';
      } else {panel.hidden=true;return;}
      panel.hidden=false;
    }
    async function poll(){
      if(stopped||waiting) return;
      waiting=true;
      try{const resp=await fetch(api,{cache:'no-store'});if(resp.ok)render(await resp.json());}
      catch{ /* offline/closing app must not show an unhelpful failure banner */ }
      finally{waiting=false;}
    }
    // The host may add a Check for updates link wherever it likes.
    const check=()=>command('/check');
    poll();
    const timer=setInterval(poll,Math.max(5000,intervalMs));
    return { check, refresh:poll, destroy(){stopped=true;clearInterval(timer);panel.remove();styles.remove();} };
  };
})();
