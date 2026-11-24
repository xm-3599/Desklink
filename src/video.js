"use strict";

// FFmpeg's FLV muxer frames each H.264 access unit and supplies its AVC configuration.
// The reader retains partial headers/frames across arbitrary HTTPS packet boundaries.
class AvcReader {
  constructor(configure, accessUnit) {
    this.configure = configure;
    this.accessUnit = accessUnit;
    this.buffer = new Uint8Array(65536);
    this.length = 0;
    this.header = false;
  }
  push(chunk) {
    const needed = this.length + chunk.length;
    if (needed > 16 * 1024 * 1024) throw Error("视频帧过大");
    if (needed > this.buffer.length) {
      const next = new Uint8Array(Math.max(needed,this.buffer.length * 2));
      next.set(this.buffer.subarray(0,this.length));
      this.buffer = next;
    }
    const b = this.buffer;
    b.set(chunk,this.length);
    this.length = needed;
    let pos = 0;
    if (!this.header) {
      if (needed < 13) return;
      if (b[0]!==70 || b[1]!==76 || b[2]!==86 || b[3]!==1 || new DataView(b.buffer).getUint32(5)!==9)
        throw Error("不支持的视频流格式");
      this.header = true;
      pos = 13;
    }
    while (needed-pos >= 11) {
      const size = b[pos+1]*65536 + b[pos+2]*256 + b[pos+3];
      const payload = pos+11, end = payload+size;
      if (size > 8*1024*1024) throw Error("视频帧长度异常");
      if (end+4 > needed) break;
      if (new DataView(b.buffer).getUint32(end)!==size+11) throw Error("视频帧损坏");
      if (b[pos]===9 && size>5 && (b[payload]&15)===7) {
        const bytes = b.subarray(payload+5,end);
        if (b[payload+1]===0) this.configure(bytes);
        if (b[payload+1]===1) {
          const ms = b[pos+7]*16777216 + b[pos+4]*65536 + b[pos+5]*256 + b[pos+6];
          this.accessUnit(bytes,ms*1000,(b[payload]>>4)===1);
        }
      }
      pos = end+4;
    }
    if (pos) { b.copyWithin(0,pos,needed); this.length = needed-pos; }
  }
}

async function playAvc(canvas, quality, fps, source, controller, drawn) {
  const signal = controller.signal;
  const ctx = canvas.getContext("2d",{alpha:false,desynchronized:true});
  let decoder, error, watchdog, needsKey=true;
  function fail(cause) { error=cause; controller.abort(); }
  function arm(ms) {
    clearTimeout(watchdog);
    watchdog=setTimeout(()=>fail(Error("视频启动或解码超时")),ms);
  }
  function render(frame) {
    try {
      if (signal.aborted) return;
      if (canvas.width!==frame.displayWidth || canvas.height!==frame.displayHeight) {
        canvas.width=frame.displayWidth; canvas.height=frame.displayHeight;
      }
      ctx.drawImage(frame,0,0);
      drawn(canvas.width,canvas.height);
      arm(4500);
    } catch (e) { fail(e); }
    finally { frame.close(); }
  }
  try {
    arm(7000);
    const response=await fetch("/api/video",{
      method:"POST",credentials:"same-origin",cache:"no-store",signal,
      headers:{"Content-Type":"application/json","X-Desklink":"1"},
      body:JSON.stringify({quality,fps:Number(fps),source})
    });
    if (!response.ok) { const e=Error("电脑未能启动视频"); e.status=response.status; throw e; }
    const reader=response.body.getReader();
    // The decoder releases frames in display order. Draw immediately so a tablet's
    // animation timer does not impose another frame of input-to-display latency.
    decoder=new VideoDecoder({output:render,error:fail});
    const packets=new AvcReader(description=>{
      if (description.length<7 || description[0]!==1) throw Error("无效的视频配置");
      const codec="avc1."+Array.from(description.subarray(1,4),v=>v.toString(16).padStart(2,"0")).join("");
      decoder.configure({codec,description,optimizeForLatency:true});
      needsKey=true;
    },(data,timestamp,key)=>{
      // Never feed predictive frames after a skipped dependency. Resume at the next IDR.
      if (decoder.decodeQueueSize>5) needsKey=true;
      if ((needsKey && !key) || decoder.state!=="configured") return;
      decoder.decode(new EncodedVideoChunk({type:key?"key":"delta",timestamp,data}));
      needsKey=false;
    });
    for (;;) {
      const {value,done}=await reader.read();
      if (done) throw Error("视频流已结束");
      packets.push(value);
    }
  } catch (e) { throw error || e; }
  finally {
    controller.abort();
    clearTimeout(watchdog);
    if (decoder && decoder.state!=="closed") decoder.close();
  }
}
