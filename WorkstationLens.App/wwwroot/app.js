'use strict';
const byId = id => document.getElementById(id);
const safe = value => String(value).replace(/[&<>"']/g, ch =>
  ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
const toGiB = bytes => bytes == null ? null : Number(bytes) / 1073741824;
let lastPreset = '24', customWindow = null, liveRows = [], historicalRows = [];
let lastLiveTime = 0, lastSavedMinutes = null;
let historyRequestId = 0;
const rangeHours = () => customWindow
  ? (customWindow.to - customWindow.from) / 3600 : Number(byId('range').value);
const rangeParams = () => customWindow
  ? 'from='+customWindow.from+'&to='+customWindow.to
  : 'hours='+(byId('range').value === 'custom' ? lastPreset : byId('range').value);
const includesNow = () => !customWindow || customWindow.to >= Date.now()/1000-30;
const updateBanner = window.mountClemarUpdater({appName:'Workstation Lens'});

const configs = [
  {id:'compute-chart',unit:'%',domain:100,format:v=>v.toFixed(1)+'%',
    lines:[{name:'CPU',color:'#72b9ff',value:r=>r.cpu},
           {name:'GPU',color:'#66d9bb',value:r=>r.gpu}]},
  {id:'memory-chart',unit:'GiB',format:v=>v.toFixed(1)+' GiB',
    lines:[{name:'RAM',color:'#eab36d',value:r=>toGiB(r.ram)},
           {name:'VRAM',color:'#b59eff',value:r=>r.vramTotal>0?toGiB(r.vramUsed):null}]},
  {id:'paging-chart',unit:'/s',format:v=>v.toFixed(1)+'/s',
    lines:[{name:'Reads',color:'#66d9bb',value:r=>r.pageReads}]},
  {id:'storage-chart',unit:'ms',format:v=>v.toFixed(2)+' ms',
    lines:[{name:'Read',color:'#72b9ff',value:r=>r.diskReads>0?r.readLatency:null},
           {name:'Write',color:'#eab36d',value:r=>r.diskWrites>0?r.writeLatency:null}]}
];
const charts = new Map();

async function json(url) {
  const response = await fetch(url,{cache:'no-store'});
  if (!response.ok) throw new Error('HTTP '+response.status);
  return response.json();
}
function localDate(time) {
  return new Date(time*1000).toLocaleString([],
    {month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'});
}
function plot(chart) {
  const {canvas,config,rows}=chart;
  const w=canvas.clientWidth,h=canvas.clientHeight;
  if(!w||!h) return;
  const dpr=window.devicePixelRatio||1;
  canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);
  const g=canvas.getContext('2d');g.setTransform(dpr,0,0,dpr,0,0);
  const frame={left:49,right:13,top:17,bottom:31};
  const plotWidth=w-frame.left-frame.right,plotHeight=h-frame.top-frame.bottom;
  g.clearRect(0,0,w,h);
  const defined=rows.flatMap(r=>config.lines.map(line=>line.value(r)))
    .filter(v=>v!==null&&Number.isFinite(v)&&v>=0);
  const domain=config.domain||Math.max(1,...defined)*1.12;
  chart.frame=frame;chart.plotWidth=plotWidth;chart.plotHeight=plotHeight;
  chart.domain=domain;
  const first=Number(rows[0]?.time??0),last=Number(rows[rows.length-1]?.time??first);
  const x=time=>frame.left+(last===first?plotWidth/2:plotWidth*(Number(time)-first)/(last-first));
  const y=value=>frame.top+plotHeight*(1-Math.min(domain,Math.max(0,value))/domain);
  chart.pointX=x;chart.pointY=y;

  g.lineWidth=1;g.font='11px Segoe UI, system-ui, sans-serif';
  for(let i=0;i<=4;i++) {
    const value=domain*i/4,axisY=frame.top+plotHeight*(1-i/4);
    g.strokeStyle='#293848';g.beginPath();g.moveTo(frame.left,axisY);
    g.lineTo(w-frame.right,axisY);g.stroke();
    g.fillStyle='#93a6bc';g.textAlign='right';g.fillText(
      config.unit==='%'?Math.round(value)+'%':config.unit==='GiB'?value.toFixed(domain<5?1:0)+'G':
      config.unit==='ms'?value.toFixed(1)+'ms':value.toFixed(0)+'/s',
      frame.left-9,axisY+4);
  }
  if (!rows.length) {
    g.fillStyle='#93a6bc';g.textAlign='center';
    g.fillText(includesNow()?'Waiting for first live sample':'No recorded data in selected dates',
      frame.left+plotWidth/2,frame.top+plotHeight/2);
    return;
  }
  const step=rangeHours()<=48?60:rangeHours()<=336?900:rangeHours()<=720?3600:21600;
  g.save();g.beginPath();g.rect(frame.left,frame.top,plotWidth,plotHeight);g.clip();
  for(const line of config.lines) {
    g.strokeStyle=line.color;g.lineWidth=1.9;g.beginPath();
    let prev=null;
    for(const row of rows) {
      const value=line.value(row),time=Number(row.time);
      if(value===null||!Number.isFinite(value)) {prev=null;continue;}
      if(prev===null||time-prev>step*1.6)g.moveTo(x(time),y(value));
      else g.lineTo(x(time),y(value));
      prev=time;
    }
    g.stroke();
    // A live observation is available before the first complete minute
    // is written to SQLite. Render it as a clear point, not an empty plot.
    if(rows.length===1 || rows[rows.length-1].live === true) {
      const row=rows[rows.length-1], value=line.value(row);
      if(value!==null&&Number.isFinite(value)) {
        g.fillStyle=line.color;g.beginPath();g.arc(x(row.time),y(value),4.5,0,Math.PI*2);g.fill();
      }
    }
  }
  if(chart.hoverIndex!=null&&chart.hoverIndex>=0&&chart.hoverIndex<rows.length){
    const row=rows[chart.hoverIndex],axisX=x(row.time);
    g.strokeStyle='#d3e7fba9';g.lineWidth=1;g.setLineDash([3,4]);
    g.beginPath();g.moveTo(axisX,frame.top);g.lineTo(axisX,frame.top+plotHeight);g.stroke();
    g.setLineDash([]);
    for(const line of config.lines){
      const value=line.value(row);
      if(value===null||!Number.isFinite(value))continue;
      g.fillStyle=line.color;g.beginPath();g.arc(axisX,y(value),4,0,Math.PI*2);g.fill();
      g.strokeStyle='#151e2b';g.lineWidth=1.5;g.stroke();
    }
  }
  g.restore();
  g.fillStyle='#93a6bc';g.textAlign='left';
  g.fillText(localDate(first),frame.left,h-8);
  g.textAlign='right';g.fillText(localDate(last),w-frame.right,h-8);
  if(rows.length===1 && rows[0].live){
    g.fillStyle='#a9bdd1';g.textAlign='center';
    g.fillText('Live sample · saving first minute',frame.left+plotWidth/2,frame.top+plotHeight/2);
  }
}
function tooltip(chart) {
  const {hoverIndex,rows,config,tip,canvas}=chart;
  if(hoverIndex===null||hoverIndex<0||hoverIndex>=rows.length) {
    tip.hidden=true;return;
  }
  const row=rows[hoverIndex];
  tip.innerHTML='<div class="tooltip-time">'+safe(localDate(row.time))+'</div>'+
    config.lines.map(line=>{
      const value=line.value(row);
      return '<div class="tooltip-row"><span><i class="tooltip-dot" style="background:'+line.color+
        '"></i>'+safe(line.name)+'</span><strong>'+
        (value===null||!Number.isFinite(value)?'—':safe(config.format(value)))+
        '</strong></div>';
    }).join('');
  tip.hidden=false;
  const pointX=chart.pointX(row.time),maxLeft=canvas.clientWidth-tip.offsetWidth-8;
  tip.style.left=Math.max(8,Math.min(pointX+12,maxLeft))+'px';
  tip.style.top='16px';
}
function nearestIndex(rows,x,chart) {
  const first=rows[0]?.time,last=rows[rows.length-1]?.time;
  if(first==null)return null;
  const t=Number(first)+(Number(last)-Number(first))*
    Math.max(0,Math.min(1,(x-chart.frame.left)/chart.plotWidth));
  let lo=0,hi=rows.length-1;
  while(lo<hi){const mid=Math.floor((lo+hi)/2);if(Number(rows[mid].time)<t)lo=mid+1;else hi=mid;}
  if(lo>0&&Math.abs(Number(rows[lo-1].time)-t)<Math.abs(Number(rows[lo].time)-t))lo--;
  return lo;
}
function initCharts() {
  for(const config of configs) {
    const canvas=byId(config.id),tip=canvas.parentElement.querySelector('.tooltip');
    const chart={canvas,tip,config,rows:[],hoverIndex:null,locked:false};
    charts.set(config.id,chart);
    canvas.addEventListener('pointermove',event=>{
      if(chart.locked||!chart.rows.length)return;
      const box=canvas.getBoundingClientRect();
      chart.hoverIndex=nearestIndex(chart.rows,event.clientX-box.left,chart);
      plot(chart);tooltip(chart);
    });
    canvas.addEventListener('pointerleave',()=>{
      if(chart.locked)return;
      chart.hoverIndex=null;tip.hidden=true;plot(chart);
    });
    canvas.addEventListener('click',event=>{
      if(!chart.rows.length)return;
      chart.locked=!chart.locked;
      if(!chart.locked){chart.hoverIndex=null;tip.hidden=true;}
      else {
        const box=canvas.getBoundingClientRect();
        chart.hoverIndex=nearestIndex(chart.rows,event.clientX-box.left,chart);
      }
      plot(chart);tooltip(chart);
    });
  }
}
function chartRows() {
  const rows=historicalRows.slice();
  if (includesNow()) {
    const end=rows.length?Number(rows[rows.length-1].time):0;
    for(const row of liveRows) if(row.time>end) rows.push(row);
  }
  return rows;
}
function refreshCharts(resetHover=false) {
  const rows=chartRows();
  for(const chart of charts.values()){
    chart.rows=rows;
    if(resetHover){chart.hoverIndex=null;chart.locked=false;chart.tip.hidden=true;}
    plot(chart);
    if(chart.hoverIndex!==null) tooltip(chart);
  }
}
function setExportUrl() {
  byId('export-download').href='/api/export/bundle?'+rangeParams();
}
async function refreshHistory() {
  const requestId=++historyRequestId, params=rangeParams();
  setExportUrl();
  try{
    const [rows,insights]=await Promise.all([
      json('/api/history?'+params),json('/api/insights?'+params)]);
    if(requestId!==historyRequestId)return;
    historicalRows=rows;
    refreshCharts(true);
    const count=Number(insights.recordedMinutes)||0;
    byId('recorded-status').textContent=count
      ? '· '+count.toLocaleString()+'m saved'
      : '· saving first minute…';
    for(const [id,value,observed,threshold] of [
      ['cpu-high',insights.cpuHighMinutes,count,'≥80%'],
      ['gpu-high',insights.gpuHighMinutes,count,'≥75%'],
      ['ram-high',insights.ramHighMinutes,count,'≥75%'],
      ['vram-high',insights.vramHighMinutes,insights.vramObservedMinutes,'≥75%']
    ]){
      byId(id).textContent=threshold+' · '+(Number(observed)>0
        ? Number(value||0).toLocaleString()+' min in range'
        : 'awaiting history');
    }
    byId('error').textContent='';
  }catch(error){
    if(requestId===historyRequestId)byId('error').textContent='History unavailable';
  }
}
function machineToChartRow(machine,hasGpu){
  const time=Number(machine.time)||Math.floor(Date.now()/1000);
  return {time,cpu:machine.cpu,gpu:hasGpu?machine.gpuEngine:null,
    ram:machine.ramUsed,vramUsed:machine.gpuMemoryUsed,vramTotal:machine.gpuMemoryTotal,
    pageReads:machine.pageReadsPerSec,
    diskReads:machine.diskReadOpsPerSec,diskWrites:machine.diskWriteOpsPerSec,
    readLatency:machine.diskReadLatencyMs,writeLatency:machine.diskWriteLatencyMs,
    live:true};
}
async function refreshStatus() {
  try{
    const status=await json('/api/live?after='+lastLiveTime);
    const incoming=status.machines||[];
    const m=incoming.length?incoming[incoming.length-1]:null;
    byId('connection').textContent=m||liveRows.length?'Recording · Live':'Starting sampler…';
    byId('status-dot').style.background=m||liveRows.length?'#66d9bb':'#eab36d';
    if(m) {
      byId('cpu-live').textContent=Number(m.cpu||0).toFixed(1)+'%';
      byId('gpu-live').textContent=status.gpuStatus==='Windows WDDM counters available'
        ? Number(m.gpuEngine||0).toFixed(1)+'%':'N/A';
      byId('ram-live').textContent=(toGiB(m.ramUsed)||0).toFixed(1)+' GiB';
      byId('vram-live').textContent=m.gpuMemoryTotal>0
        ? (toGiB(m.gpuMemoryUsed)||0).toFixed(1)+' GiB':'N/A';
      if(status.gpuStatus!=='Windows WDDM counters available')
        byId('gpu-high').textContent='No GPU counters';
      const received=incoming.map(m=>machineToChartRow(m,status.gpuStatus==='Windows WDDM counters available'));
      lastLiveTime=Math.max(lastLiveTime,...received.map(row=>row.time));
      liveRows=liveRows.concat(received).slice(-40);
      // No SQLite minute is needed before the very first data point is plotted.
      refreshCharts();
    }
    const count=Number(status.storedMinutes)||0;
    if(lastSavedMinutes===null)lastSavedMinutes=count;
    else if(count!==lastSavedMinutes){
      lastSavedMinutes=count;
      refreshHistory(); // One archive refresh per completed minute.
    }
    if(status.lastError)byId('error').textContent='Collector warning';
  }catch{
    byId('connection').textContent='Disconnected';
    byId('status-dot').style.background='#ef8f90';
  }
}
function customRangeLabel(fromSeconds,toSeconds) {
  const a=new Date(fromSeconds*1000),b=new Date(toSeconds*1000);
  const date=d=>d.toLocaleDateString(undefined,{day:'numeric',month:'short'});
  const time=d=>d.toLocaleTimeString(undefined,{hour:'2-digit',minute:'2-digit',hour12:false});
  if(date(a)===date(b)&&a.getFullYear()===b.getFullYear())
    return date(a)+' '+time(a)+'–'+time(b);
  return date(a)+' '+time(a)+' – '+date(b)+' '+time(b);
}
function setCustomLabel(){
  const option=byId('range').querySelector('option[value="custom"]');
  option.textContent=customWindow?customRangeLabel(customWindow.from,customWindow.to):'Custom…';
  option.title=customWindow?new Date(customWindow.from*1000).toLocaleString()+
    ' – '+new Date(customWindow.to*1000).toLocaleString():'Custom dates';
}
function localInput(date) {
  return new Date(date.getTime()-date.getTimezoneOffset()*60000).toISOString().slice(0,16);
}
function openCustomPicker(){
  const now=new Date();
  if(!byId('custom-to').value)byId('custom-to').value=localInput(now);
  if(!byId('custom-from').value)byId('custom-from').value=localInput(new Date(now.getTime()-86400000));
  byId('custom-error').hidden=true;
  byId('custom-picker').hidden=false;
  byId('custom-from').focus();
}
function cancelCustom(){
  byId('custom-picker').hidden=true;
  if(!customWindow){byId('range').value=lastPreset;byId('custom-edit').hidden=true;}
}
byId('range').addEventListener('change',()=>{
  if(byId('range').value==='custom'){
    byId('custom-edit').hidden=false;
    openCustomPicker();
  }else{
    customWindow=null;
    setCustomLabel();
    lastPreset=byId('range').value;
    byId('custom-picker').hidden=true;
    byId('custom-edit').hidden=true;
    refreshHistory();
  }
});
byId('custom-edit').addEventListener('click',openCustomPicker);
byId('custom-cancel').addEventListener('click',cancelCustom);
byId('custom-picker').addEventListener('submit',event=>{
  event.preventDefault();
  const from=Date.parse(byId('custom-from').value),to=Date.parse(byId('custom-to').value);
  const now=Date.now();
  if(!Number.isFinite(from)||!Number.isFinite(to)||from>=to||to>now+60000 ||
     from<now-90*86400000-60000||to-from>90*86400000) {
    byId('custom-error').textContent='Choose a valid past range within 90 days.';
    byId('custom-error').hidden=false;
    return;
  }
  customWindow={from:Math.floor(from/1000),to:Math.floor(to/1000)};
  setCustomLabel();
  byId('custom-picker').hidden=true;
  byId('custom-edit').hidden=false;
  byId('range').value='custom';
  byId('export-download').title='Export selected custom date range';
  refreshHistory();
});
byId('export-download').addEventListener('click',event=>{
  if(byId('range').value==='custom'&&!customWindow){
    event.preventDefault();openCustomPicker();
  }
});
document.addEventListener('keydown',event=>{
  if(event.key==='Escape'&&!byId('custom-picker').hidden)cancelCustom();
});
document.addEventListener('pointerdown',event=>{
  if(!byId('custom-picker').hidden && !byId('range-wrap').contains(event.target))cancelCustom();
});
let resizeTimer;
window.addEventListener('resize',()=>{
  clearTimeout(resizeTimer);
  resizeTimer=setTimeout(()=>charts.forEach(plot),120);
});
initCharts();
setCustomLabel();
setExportUrl();
refreshStatus();
refreshHistory();
// Live readings follow the 15-second sampler. The browser polls status lightly
// and only queries historical SQLite when a newly saved minute is detected.
setTimeout(refreshStatus,2100);
setInterval(refreshStatus,4000);
