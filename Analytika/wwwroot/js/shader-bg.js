/* WebGPU aurora. Unsupported devices retain the CSS background; no WebGL fallback. */
(function () {
  'use strict';
  const shader = `
struct Uniforms { resolution: vec2f, time: f32, pad: f32 };
@group(0) @binding(0) var<uniform> u: Uniforms;
@vertex fn vs(@builtin(vertex_index) i:u32)->@builtin(position) vec4f {
 let p=array<vec2f,3>(vec2f(-1,-1),vec2f(3,-1),vec2f(-1,3)); return vec4f(p[i],0,1);
}
fn hash(p:vec2f)->f32{return fract(sin(dot(p,vec2f(127.1,311.7)))*43758.5453);}
fn noise(p:vec2f)->f32{let i=floor(p);let f=fract(p);let s=f*f*(3-2*f);return mix(mix(hash(i),hash(i+vec2f(1,0)),s.x),mix(hash(i+vec2f(0,1)),hash(i+vec2f(1,1)),s.x),s.y);}
@fragment fn fs(@builtin(position) q:vec4f)->@location(0) vec4f {
 let aspect=u.resolution.x/max(u.resolution.y,1);var p=q.xy/max(u.resolution,vec2f(1));p=(p-.5)*vec2f(aspect,1);
 let t=u.time*.12;let a=exp(-9*abs(p.y-.14*sin(p.x*4+t*2)));let b=exp(-12*abs(p.y+.2*cos(p.x*3-t)));
 return vec4f(vec3f(.025,.055,.075)+vec3f(.02,.52,.56)*a+vec3f(.24,.25,.7)*b+noise(p*5+vec2f(t,-t))*.08,1);
}`;
  async function init(host) {
    if (host.dataset.webgpuInitialized || !navigator.gpu || document.hidden) return;
    host.dataset.webgpuInitialized='true';
    try {
      const adapter=await navigator.gpu.requestAdapter({powerPreference:'low-power'}); if(!adapter)return;
      const device=await adapter.requestDevice(), canvas=document.createElement('canvas');
      canvas.className='webgpu-aurora';canvas.setAttribute('aria-hidden','true');
      canvas.style.cssText='position:absolute;inset:0;width:100%;height:100%;pointer-events:none;z-index:0;border-radius:inherit';
      if(getComputedStyle(host).position==='static')host.style.position='relative';host.insertBefore(canvas,host.firstChild);
      const context=canvas.getContext('webgpu'),format=navigator.gpu.getPreferredCanvasFormat();
      context.configure({device,format,alphaMode:'opaque'});
      const module=device.createShaderModule({code:shader});
      const pipeline=device.createRenderPipeline({layout:'auto',vertex:{module,entryPoint:'vs'},fragment:{module,entryPoint:'fs',targets:[{format}]},primitive:{topology:'triangle-list'}});
      const uniform=device.createBuffer({size:16,usage:GPUBufferUsage.UNIFORM|GPUBufferUsage.COPY_DST});
      const group=device.createBindGroup({layout:pipeline.getBindGroupLayout(0),entries:[{binding:0,resource:{buffer:uniform}}]});
      let width=0,height=0,frame=0,resizeFrame=0,visible=true,last=0;const started=performance.now();
      const reduced=matchMedia('(prefers-reduced-motion: reduce)').matches;
      function resize(){resizeFrame=0;const d=Math.min(devicePixelRatio||1,1.5),w=Math.max(1,Math.min(2048,Math.round(host.clientWidth*d))),h=Math.max(1,Math.min(1200,Math.round(host.clientHeight*d)));if(w===width&&h===height)return;width=canvas.width=w;height=canvas.height=h;}
      function draw(now){frame=0;if(!visible||document.hidden)return;if(now-last<50&&!reduced){frame=requestAnimationFrame(draw);return;}last=now;resize();device.queue.writeBuffer(uniform,0,new Float32Array([width,height,(now-started)/1000,0]));const e=device.createCommandEncoder(),p=e.beginRenderPass({colorAttachments:[{view:context.getCurrentTexture().createView(),clearValue:{r:.025,g:.055,b:.075,a:1},loadOp:'clear',storeOp:'store'}]});p.setPipeline(pipeline);p.setBindGroup(0,group);p.draw(3);p.end();device.queue.submit([e.finish()]);if(!reduced)frame=requestAnimationFrame(draw);}
      new ResizeObserver(()=>{if(resizeFrame)cancelAnimationFrame(resizeFrame);resizeFrame=requestAnimationFrame(()=>{resize();if(!frame)frame=requestAnimationFrame(draw);});}).observe(host);
      new IntersectionObserver(x=>{visible=x[0].isIntersecting;if(visible&&!frame)frame=requestAnimationFrame(draw);else if(!visible&&frame){cancelAnimationFrame(frame);frame=0;}}).observe(host);
      document.addEventListener('visibilitychange',()=>{if(!document.hidden&&visible&&!frame)frame=requestAnimationFrame(draw);},{passive:true});
      frame=requestAnimationFrame(draw);
    } catch (_) { host.dataset.webgpuUnavailable='true'; }
  }
  const boot=()=>document.querySelectorAll('[data-shader-bg]').forEach(init);
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',boot,{once:true});else boot();
})();
