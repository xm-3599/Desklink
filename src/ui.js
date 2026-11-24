"use strict";
const $ = id => document.getElementById(id);
const icons = {
  link:"M10 13a4 4 0 0 0 6 0l3-3a4 4 0 0 0-6-6l-2 2 M14 11a4 4 0 0 0-6 0l-3 3a4 4 0 0 0 6 6l2-2",
  unlink:"m3 3 18 18 M8 12l-3 3a4 4 0 0 0 6 6l2-2 M12 7l2-2a4 4 0 0 1 6 6l-1 1",
  monitor:"M3 4h18v13H3z M12 17v4 M7 21h10",arrow:"M4 12h15m-6-6 6 6-6 6",
  prev:"m15 5-7 7 7 7",next:"m9 5 7 7-7 7",eye:"M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0z",
  settings:"M3 6h7m5 0h6 M3 12h2m5 0h11 M3 18h11m5 0h2 M10 3v6m-5 0v6m9 0v6",
  keyboard:"M2 5h20v14H2z M5 9h1m3 0h1m3 0h1m3 0h1 M5 12h1m3 0h1m3 0h1m3 0h1 M8 15h8",
  expand:"M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5",exit:"M10 4H4v16h6m-2-8h13m-5-5 5 5-5 5",
  close:"m5 5 14 14M19 5 5 19",layers:"M3 4h14v11H3z M8 18v2h13V9h-2"
};
document.querySelectorAll("[data-icon]").forEach(node => {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns,"svg"), path = document.createElementNS(ns,"path");
  Object.entries({viewBox:"0 0 24 24",fill:"none",stroke:"currentColor","stroke-width":"1.7","stroke-linecap":"round","stroke-linejoin":"round",class:"icon","aria-hidden":"true"}).forEach(([k,v])=>svg.setAttribute(k,v));
  path.setAttribute("d",icons[node.dataset.icon]); svg.append(path); node.replaceWith(svg);
});
let page = "pair", state = null, generation = 0, frameLive = false;
const clamp = n => Math.max(0,Math.min(1,n));
const delay = ms => new Promise(resolve=>setTimeout(resolve,ms));
const pref = (key, choices, fallback) => {
  try { const val = localStorage.getItem("desk-"+key); return choices.includes(val) ? val : fallback; }
  catch { return fallback; }
};
let quality = pref("quality",["light","balanced","sharp"],"balanced");
let rate = pref("fps",["5","10","15","30","60"],"30");
let transport = pref("transport",["auto","jpeg"],"auto");
let playback=0, streamController=null, dxgiFailed=false;
let gesture = pref("gestures",["pad","direct"],"direct");
function save(key,value) { try { localStorage.setItem("desk-"+key,value); } catch {} }
let toastClock;
function toast(message) {
  $("toast").textContent=message; $("toast").hidden=false; clearTimeout(toastClock);
  toastClock=setTimeout(()=>$("toast").hidden=true,3300);
}
async function request(path, data, image=false) {
  const abort = new AbortController(), timeout = setTimeout(()=>abort.abort(),10000);
  try {
    const options = {cache:"no-store",credentials:"same-origin",signal:abort.signal};
    if (data !== undefined) Object.assign(options,{method:"POST",headers:{"X-Desklink":"1","Content-Type":"application/json"},body:JSON.stringify(data)});
    const res = await fetch("/api/"+path,options);
    if (!res.ok) {
      const msg = await res.json().catch(()=>({}));
      const err = new Error(msg.error || "连接失败"); err.status=res.status;
      if (res.status===401 && page!=="pair") resetPair("连接已结束，请在电脑上生成新码。");
      throw err;
    }
    return image ? await res.blob() : await res.json();
  } catch (e) {
    if (!e.status) throw new Error("联系不到电脑，请检查共享状态和网络。");
    throw e;
  } finally { clearTimeout(timeout); }
}
function show(next) {
  stopPlayback();
  generation++; frameLive=false; resetPointers(); page=next;
  for (const id of ["pair","devices","detail","session"]) $(id).hidden=(id!==next);
  $("home").hidden=$("topbar").hidden=next==="session";
  $("drawer").hidden=$("keysheet").hidden=true; $("tools").classList.remove("active"); $("tools").setAttribute("aria-expanded","false");
  for (const id of ["preview-img","screen"]) {
    const img=$(id); if (img.src.startsWith("blob:")) URL.revokeObjectURL(img.src);
    img.removeAttribute("src"); img.hidden=true;
  }
  if (next==="detail" || next==="session") {
    const note=$(next==="detail"?"preview-note":"screen-state"); note.textContent="正在获取电脑画面…"; note.hidden=false;
    $("meter").textContent="正在测量帧率…";
    if (next==="session") startPlayback(); else frames(generation,false,playback);
  }
  if (next!=="session" && document.fullscreenElement) document.exitFullscreen().catch(()=>{});
  window.scrollTo(0,0);
}
function resetPair(msg) {
  state=null; show("pair"); $("pin").value=""; $("pair-error").textContent=msg; $("pair-error").hidden=!msg;
}
function setState(value) {
  state=value; document.querySelectorAll(".pc-name").forEach(el=>el.textContent=value.name);
  $("online-count").textContent="1"; $("device-card").disabled=false;
  if (!active && !sending && !pending.length) cursor={x:value.x,y:value.y};
  $("resolution").textContent=value.width+" × "+value.height;
  $("enter").disabled=!value.allowed; $("enter").title=value.allowed?"":"电脑端已设为仅观看";
  renderControls();
}
async function connect() {
  const value=await request("state"); await request("mode",{mode:"view"}); value.control=false;
  setState(value); show("devices");
}
$("pin").addEventListener("input",()=>{
  const raw=$("pin").value.replace(/\D/g,"").slice(0,8);
  $("pin").value=raw.length>4 ? raw.slice(0,4)+" "+raw.slice(4) : raw;
});
$("pair-form").addEventListener("submit",async e=>{
  e.preventDefault(); $("pair-error").hidden=true;
  const digits=$("pin").value.replace(/\s/g,"");
  if (digits.length!==8) { $("pair-error").textContent="请输入 8 位配对码。"; $("pair-error").hidden=false; return; }
  $("pair-submit").disabled=true;
  try { await request("pair",{pin:digits}); await connect(); $("pin").value=""; }
  catch(err){ $("pair-error").textContent=err.message; $("pair-error").hidden=false; }
  finally { $("pair-submit").disabled=false; }
});
$("device-card").onclick=()=>show("detail"); $("back-list").onclick=()=>show("devices");
$("unpair").onclick=async()=>{ try { await request("leave",{}); resetPair("配对已断开。再次连接时，请在电脑上生成新码。"); } catch(e){ toast(e.message); } };
async function selectMode(mode,enter=false) {
  try {
    resetPointers(); await queueDrain();
    const v=await request("mode",{mode}); if (!state) return;
    state.control=v.control; renderControls(); if (enter) show("session");
  } catch(e){ toast(e.message); }
}
$("enter").onclick=()=>selectMode("control",true); $("watch").onclick=()=>selectMode("view",true);
$("exit").onclick=async()=>{
  cancelPending(); show("detail");
  try { await queueDrain(); await request("mode",{mode:"view"}); if(state) { state.control=false; renderControls(); } }
  catch(e){ toast(e.message); }
};
function stopPlayback() {
  playback++;
  if (streamController) streamController.abort();
  streamController=null; frameLive=false;
  $("video-screen").hidden=true;
}
async function startPlayback() {
  stopPlayback();
  if(page!=="session" || document.hidden) return;
  const version=generation, run=playback, note=$("transport-note");
  $("screen").hidden=true;
  $("screen-state").textContent="正在获取电脑画面…"; $("screen-state").hidden=false;
  $("meter").textContent="正在测量帧率…";
  if (transport==="auto" && state?.h264 && typeof VideoDecoder==="function") {
    note.textContent="正在连接 H.264 视频…";
    for (const source of state.dxgi && !dxgiFailed ? ["dxgi","gdi"] : ["gdi"]) {
      const controller=new AbortController(); streamController=controller;
      let count=0, start=0;
      try {
        await playAvc($("video-screen"),quality,rate,source,controller,(w,h)=>{
          if (run!==playback) return;
          if (!frameLive) note.textContent="H.264 视频流已连接 · "+source.toUpperCase()+" 采集";
          frameLive=true; $("video-screen").hidden=false; $("screen-state").hidden=true;
          const now=performance.now();
          if (!start) { start=now; return; }
          count++;
          if (now-start>=1000) {
            $("meter").textContent=w+" × "+h+" · H.264 · 实际绘制 "+(count*1000/(now-start)).toFixed(1)+" 帧/秒";
            count=0; start=now;
          }
        });
      } catch (e) {
        if (run!==playback) return;
        if (e.status===401) { resetPair("连接已结束，请在电脑上生成新码。"); return; }
        cancelPending(); frameLive=false; $("video-screen").hidden=true;
        if (source==="dxgi") { dxgiFailed=true; note.textContent="正在切换到 GDI 视频采集…"; }
        else note.textContent="H.264 暂不可用，已切换 JPEG。点「自动」可重试。";
      } finally { if (streamController===controller) streamController=null; }
    }
  } else {
    note.textContent=transport==="jpeg"?"已选择 JPEG 兼容模式。":!state?.h264?"电脑未找到 FFmpeg，使用 JPEG。":"平板浏览器未提供视频解码接口，使用 JPEG。";
  }
  if (run===playback) frames(version,true,run);
}
async function frames(version,live,run) {
  const img=$(live?"screen":"preview-img"), note=$(live?"screen-state":"preview-note");
  let measuredAt=performance.now(), count=0;
  const current=()=>generation===version && (!live || run===playback);
  while (current()) {
    const started=performance.now();
    if (document.hidden) { await delay(350); measuredAt=performance.now(); count=0; continue; }
    let failed=false, url="";
    try {
      const blob=await request("frame/"+(live?quality:"light"),undefined,true);
      url=URL.createObjectURL(blob); const decoded=new Image(); decoded.src=url; await decoded.decode();
      if (!current()) break;
      const old=img.src; img.src=url; url=""; img.hidden=false; note.hidden=true; frameLive=live;
      if(old.startsWith("blob:")) URL.revokeObjectURL(old);
      count++; const period=performance.now()-measuredAt;
      if(live && period>=1100) { $("meter").textContent=decoded.naturalWidth+" × "+decoded.naturalHeight+" · JPEG · 已解码 "+(count*1000/period).toFixed(1)+" 帧/秒"; count=0; measuredAt=performance.now(); }
    } catch(e) {
      if(!current()) break;
      failed=true; frameLive=false; cancelPending(); note.hidden=false; note.textContent=e.message+" 正在重试…";
      $("meter").textContent="等待电脑画面…";
    } finally { if(url) URL.revokeObjectURL(url); }
    await delay(failed?1200:live?Math.max(0,1000/Number(rate)-(performance.now()-started)):4500);
  }
}
let polling=false;
setInterval(async()=>{
  if (page==="pair" || polling || document.hidden) return;
  polling=true; const version=generation;
  try { const v=await request("state"); if (version===generation) setState(v); }
  catch(e) { if(page==="devices" || page==="detail") { document.querySelectorAll(".tag.online").forEach(t=>t.textContent="连接中"); $("online-count").textContent="0"; $("device-card").disabled=true; toast(e.message); } }
  finally { polling=false; }
},4500);
function selectButtons(group,value) { $(group).querySelectorAll("button").forEach(b=>b.setAttribute("aria-pressed",String(b.dataset.value===value))); }
function renderControls() {
  if (!state) return;
  document.querySelectorAll(".tag.online").forEach(t=>t.textContent="在线");
  selectButtons("modes",state.control?"control":"view"); selectButtons("gestures",gesture); selectButtons("quality",quality); selectButtons("fps",rate);
  selectButtons("transport",transport);
  $("modes").querySelector('[data-value="control"]').disabled=!state.allowed;
  $("keyboard").disabled=!state.control;
  if (!state.control) { $("keysheet").hidden=true; cancelPending(); }
  document.querySelectorAll("[data-key],#gestures button,#send-text").forEach(b=>b.disabled=!state.control);
  $("gesture-hint").textContent=gesture==="direct"?"轻点左击，长按右击，按住滑动可拖拽。双指上下滑动可滚屏。":"滑动移动指针，轻点左击，长按右击。双指滚屏；拖拽请切到直接触控。";
  const widths={light:960,balanced:1440,sharp:1920};
  $("quality-hint").textContent="画面长边最多 "+widths[quality]+" 像素，保持电脑原始比例。";
}
function options(group,fn){ $(group).addEventListener("click",e=>{const b=e.target.closest("[data-value]"); if(b) fn(b.dataset.value);}); }
options("modes",value=>selectMode(value));
options("gestures",value=>{gesture=value;save("gestures",value);renderControls();});
options("quality",value=>{quality=value;save("quality",value);renderControls();startPlayback();});
options("fps",value=>{rate=value;save("fps",value);renderControls();startPlayback();});
options("transport",value=>{transport=value;dxgiFailed=false;save("transport",value);renderControls();startPlayback();});
function toggleTools(showTools) { $("drawer").hidden=!showTools; $("tools").classList.toggle("active",showTools); $("tools").setAttribute("aria-expanded",String(showTools)); $("keysheet").hidden=true; }
$("tools").onclick=()=>toggleTools($("drawer").hidden); $("close-tools").onclick=()=>toggleTools(false);
$("keyboard").onclick=()=>{ const open=$("keysheet").hidden; toggleTools(false); $("keysheet").hidden=!open; if(open) $("text-input").focus(); };
$("close-keys").onclick=()=>{$("keysheet").hidden=true; $("text-input").blur();};
$("full").onclick=async()=>{
  try {
    if(document.fullscreenElement) await document.exitFullscreen();
    else if($("session").requestFullscreen) await $("session").requestFullscreen();
    else toast("当前浏览器不支持网页全屏，可横屏使用。");
  } catch { toast("当前浏览器未允许网页全屏。"); }
};
const pending=[]; let sending=false, active=null, two=false, holdTimer=0;
const fingers=new Map();
function resetPointers(){ clearTimeout(holdTimer); fingers.clear(); active=null; two=false; }
function cancelPending(){ resetPointers(); while(pending.length) pending.shift().done(false); }
async function queueDrain(){ while(sending || pending.length) await delay(15); }
function sendInput(cmd) {
  if(page!=="session" || !state?.control || !frameLive) return Promise.resolve(false);
  return new Promise(done=>{
    const last=pending[pending.length-1];
    if(last && last.cmd.op==="move" && cmd.op==="move") { last.done(false); pending.pop(); }
    pending.push({cmd,done}); pump();
  });
}
async function pump() {
  if(sending) return; sending=true;
  try {
    while(pending.length) {
      const item=pending.shift();
      try { await request("input",item.cmd); item.done(true); }
      catch(e){ item.done(false); cancelPending(); if(e.status===403 && state){ state.control=false; renderControls(); } toast(e.message); }
    }
  } finally { sending=false; }
}
document.querySelectorAll("[data-key]").forEach(b=>b.onclick=()=>sendInput({op:"key",key:b.dataset.key}));
$("send-text").onclick=async()=>{
  const box=$("text-input"), value=box.value; if(!value) return;
  $("send-text").disabled=true;
  try { if(await sendInput({op:"text",text:value})) { if(box.value===value) box.value=""; toast("文字已发送"); } }
  finally { $("send-text").disabled=!state?.control; }
};
function area() {
  const img=$("screen"), video=$("video-screen"), r=$("stage").getBoundingClientRect();
  const width=video.hidden?img.naturalWidth:video.width, height=video.hidden?img.naturalHeight:video.height;
  const scale=Math.min(r.width/width,r.height/height);
  const w=width*scale,h=height*scale;
  return {left:r.left+(r.width-w)/2,top:r.top+(r.height-h)/2,w,h};
}
function point(e, clip=false) {
  const a=area(),x=(e.clientX-a.left)/a.w,y=(e.clientY-a.top)/a.h;
  if(!Number.isFinite(x+y) || (!clip && (x<0 || x>1 || y<0 || y>1))) return null;
  return {x:clamp(x),y:clamp(y)};
}
let cursor={x:.5,y:.5}, scrollLast=0, scrollRest=0;
const stage=$("stage");
function command(op,p){ return sendInput({op,x:p.x,y:p.y}); }
function avgY(){ return [...fingers.values()].reduce((n,v)=>n+v.y,0)/fingers.size; }
stage.addEventListener("contextmenu",e=>e.preventDefault());
stage.addEventListener("pointerdown",e=>{
  if (!$("drawer").hidden || !$("keysheet").hidden) { toggleTools(false); return; }
  if (!state?.control || !frameLive || ![0,2].includes(e.button)) return;
  const p=point(e); if(!p) return; e.preventDefault(); stage.setPointerCapture(e.pointerId);
  if(e.button===2){ command("rightclick",p); return; }
  fingers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  if(fingers.size>1) {
    clearTimeout(holdTimer); if(active?.drag) sendInput({op:"release"});
    active=null; two=true; scrollLast=avgY(); scrollRest=0; return;
  }
  const pad=gesture==="pad" && e.pointerType!=="mouse";
  if(!pad) cursor=p;
  active={id:e.pointerId, start:p, x:e.clientX,y:e.clientY, pad, moved:false, long:false, drag:false};
  if(e.pointerType!=="mouse") holdTimer=setTimeout(()=>{ if(active && !active.moved) { active.long=true; command("rightclick",cursor); } },560);
});
stage.addEventListener("pointermove",e=>{
  if(!fingers.has(e.pointerId) || !state?.control) return;
  const prev=fingers.get(e.pointerId); fingers.set(e.pointerId,{x:e.clientX,y:e.clientY});
  if(two) {
    if(fingers.size>=2) {
      const y=avgY(); scrollRest+=y-scrollLast; scrollLast=y;
      const ticks=Math.trunc(scrollRest/30);
      if(ticks) { scrollRest-=ticks*30; sendInput({op:"wheel",delta:Math.max(-1200,Math.min(1200,ticks*120))}); }
    }
    return;
  }
  if(!active || active.long) return;
  const p=point(e,true); if(!p) return;
  if(!active.moved && Math.hypot(e.clientX-active.x,e.clientY-active.y)<5) return;
  clearTimeout(holdTimer);
  if(active.pad) {
    const a=area();
    cursor={x:clamp(cursor.x+(e.clientX-prev.x)*1.25/a.w),y:clamp(cursor.y+(e.clientY-prev.y)*1.25/a.h)};
    command("move",cursor);
  } else {
    if(!active.moved) { command("press",active.start); active.drag=true; }
    cursor=p; command("move",p);
  }
  active.moved=true;
});
function finish(e,cancelled) {
  if(!fingers.has(e.pointerId)) return;
  fingers.delete(e.pointerId); clearTimeout(holdTimer);
  if(two) { if(!fingers.size) resetPointers(); return; }
  if(active?.id===e.pointerId) {
    if(cancelled) { if(active.drag) sendInput({op:"release"}); }
    else if(active.drag) command("up",point(e,true)||cursor);
    else if(!active.moved && !active.long) command("click",active.pad?cursor:active.start);
    active=null;
  }
}
stage.addEventListener("pointerup",e=>finish(e,false)); stage.addEventListener("pointercancel",e=>finish(e,true)); stage.addEventListener("lostpointercapture",e=>finish(e,true));
stage.addEventListener("wheel",e=>{
  if(!state?.control || !frameLive) return; e.preventDefault(); const p=point(e); if(p) command("move",p);
  if(e.deltaY) sendInput({op:"wheel",delta:-Math.sign(e.deltaY)*120});
},{passive:false});
setInterval(()=>{if(active?.drag && !pending.length && !sending) sendInput({op:"keep"});},900);
document.addEventListener("visibilitychange",()=>{
  if(page!=="session") return;
  if(document.hidden) { cancelPending(); sendInput({op:"release"}); stopPlayback(); }
  else startPlayback();
});
const physical={Enter:"enter",Backspace:"back",Tab:"tab",Escape:"esc",ArrowLeft:"left",ArrowRight:"right",ArrowUp:"up",ArrowDown:"down",Delete:"delete"};
document.addEventListener("keydown",e=>{
  if(page!=="session" || !state?.control || e.isComposing || !$("keysheet").hidden || !$("drawer").hidden || e.target.closest("input,textarea")) return;
  if(e.target.closest("button") && [" ","Enter"].includes(e.key)) return;
  const combo=e.ctrlKey || e.metaKey;
  const key=combo?({a:"all",c:"copy",v:"paste",z:"undo"})[e.key.toLowerCase()]:physical[e.key];
  if(key) { e.preventDefault(); sendInput({op:"key",key}); }
  else if(!combo && !e.altKey && [...e.key].length===1) { e.preventDefault(); sendInput({op:"text",text:e.key}); }
});
connect().catch(e=>{ if(e.status!==401) resetPair(e.message); });
