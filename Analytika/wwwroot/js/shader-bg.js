/* Aurora shader background — raw WebGL, no Three.js dependency.
   Attaches an animated GLSL shader canvas behind any element with [data-shader-bg]
   or .page-wrapper. Self-contained; safe to load multiple times. */
(function () {
  'use strict';

  var VERT = [
    'attribute vec2 a_pos;',
    'void main(){gl_Position=vec4(a_pos,0.0,1.0);}'
  ].join('\n');

  var FRAG = [
    'precision mediump float;',
    'uniform float iTime;',
    'uniform vec2  iResolution;',
    '#define NUM_OCTAVES 3',
    'float rand(vec2 n){return fract(sin(dot(n,vec2(12.9898,4.1414)))*43758.5453);}',
    'float noise(vec2 p){',
    '  vec2 ip=floor(p);vec2 u=fract(p);',
    '  u=u*u*(3.0-2.0*u);',
    '  return mix(mix(rand(ip),rand(ip+vec2(1,0)),u.x),mix(rand(ip+vec2(0,1)),rand(ip+vec2(1,1)),u.x),u.y);',
    '}',
    'float fbm(vec2 x){',
    '  float v=0.0;float a=0.3;',
    '  vec2 sh=vec2(100.0);',
    '  mat2 rot=mat2(cos(0.5),sin(0.5),-sin(0.5),cos(0.5));',
    '  for(int i=0;i<NUM_OCTAVES;++i){v+=a*noise(x);x=rot*x*2.0+sh;a*=0.4;}',
    '  return v;',
    '}',
    'void main(){',
    '  vec2 shake=vec2(sin(iTime*1.2)*0.005,cos(iTime*2.1)*0.005);',
    '  vec2 p=((gl_FragCoord.xy+shake*iResolution.xy)-iResolution.xy*0.5)/iResolution.y*mat2(6.0,-4.0,4.0,6.0);',
    '  vec2 v;',
    '  vec4 o=vec4(0.0);',
    '  float f=2.0+fbm(p+vec2(iTime*5.0,0.0))*0.5;',
    '  for(float i=0.0;i<35.0;i++){',
    '    v=p+cos(i*i+(iTime+p.x*0.08)*0.025+i*vec2(13.0,11.0))*3.5',
    '      +vec2(sin(iTime*3.0+i)*0.003,cos(iTime*3.5-i)*0.003);',
    '    float tailNoise=fbm(v+vec2(iTime*0.5,i))*0.3*(1.0-(i/35.0));',
    '    vec4 col=vec4(',
    '      0.1+0.3*sin(i*0.2+iTime*0.4),',
    '      0.3+0.5*cos(i*0.3+iTime*0.5),',
    '      0.7+0.3*sin(i*0.4+iTime*0.3),',
    '      1.0',
    '    );',
    '    vec4 contrib=col*exp(sin(i*i+iTime*0.8))/length(max(v,vec2(v.x*f*0.015,v.y*1.5)));',
    '    float thin=smoothstep(0.0,1.0,i/35.0)*0.6;',
    '    o+=contrib*(1.0+tailNoise*0.8)*thin;',
    '  }',
    '  o=tanh(o/22.0);',
    '  gl_FragColor=vec4(o.rgb*2.6,1.0);',
    '}'
  ].join('\n');

  function compile(gl, type, src) {
    var s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    return s;
  }

  /* Detect headless / automated environment — bail before touching WebGL.
     SwiftShader (software GL in headless Chrome) blocks on getContext. */
  var _headless = (function () {
    try {
      if (navigator.webdriver) return true;
      if (/Headless|HeadlessChrome/.test(navigator.userAgent)) return true;
      // Playwright/CDP sets this to 0 or omits plugins entirely
      if (navigator.plugins && navigator.plugins.length === 0 &&
          /Chrome/.test(navigator.userAgent) && !/Electron/.test(navigator.userAgent)) return true;
      // Permission API probe: automation contexts deny 'notifications' synchronously
      if (window.Notification && window.Notification.permission === 'denied') return false; // real user too
      return false;
    } catch (e) { return false; }
  }());

  function initShaderBg(host) {
    if (host._shaderBgInit) return;
    host._shaderBgInit = true;

    // Skip shader entirely in headless / automated browsers, or if page
    // loaded while hidden (preview tools, background tabs on first paint).
    if (_headless || document.hidden) return;

    var canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;pointer-events:none;z-index:0;border-radius:inherit;';
    if (!host.style.position || host.style.position === 'static') host.style.position = 'relative';
    host.insertBefore(canvas, host.firstChild);

    var gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
    if (!gl) { canvas.remove(); return; }

    var prog = gl.createProgram();
    gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER,   VERT));
    gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(prog);
    gl.useProgram(prog);

    /* fullscreen quad */
    var buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1, 1,-1, -1,1, 1,1]), gl.STATIC_DRAW);
    var loc = gl.getAttribLocation(prog, 'a_pos');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

    var uTime = gl.getUniformLocation(prog, 'iTime');
    var uRes  = gl.getUniformLocation(prog, 'iResolution');

    var t = 0, rafId = null, stopped = false;

    function resize() {
      var w = host.clientWidth  || 1;
      var h = host.clientHeight || 1;
      canvas.width  = w;
      canvas.height = h;
      gl.viewport(0, 0, w, h);
      gl.uniform2f(uRes, w, h);
    }

    var FRAME_MS = 32; // ~30 fps cap — keeps event loop free for CDP/Playwright
    var lastFrame = 0;

    function frame(now) {
      if (stopped || document.hidden) { rafId = null; return; }
      if (now - lastFrame < FRAME_MS) { rafId = requestAnimationFrame(frame); return; }
      lastFrame = now;
      t += 0.016;
      gl.uniform1f(uTime, t);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      rafId = requestAnimationFrame(frame);
    }

    function onVisibility() {
      if (!document.hidden && !rafId && !stopped) requestAnimationFrame(frame);
    }

    resize();
    requestAnimationFrame(frame);

    document.addEventListener('visibilitychange', onVisibility);

    var ro = new ResizeObserver(resize);
    ro.observe(host);

    if (window.IntersectionObserver) {
      new IntersectionObserver(function (entries) {
        entries.forEach(function (e) {
          if (e.isIntersecting) { if (!rafId && !stopped) frame(); }
          else { cancelAnimationFrame(rafId); rafId = null; }
        });
      }).observe(host);
    }

    host._shaderBgDestroy = function () {
      stopped = true;
      cancelAnimationFrame(rafId);
      document.removeEventListener('visibilitychange', onVisibility);
      ro.disconnect();
      gl.deleteProgram(prog);
      gl.deleteBuffer(buf);
      canvas.remove();
    };
  }

  function boot() {
    var targets = document.querySelectorAll('[data-shader-bg]');
    if (targets.length) { targets.forEach(initShaderBg); }
    else { var pw = document.querySelector('.page-wrapper'); if (pw) initShaderBg(pw); }
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', boot); }
  else { boot(); }
})();
