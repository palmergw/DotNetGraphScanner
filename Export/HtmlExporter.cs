using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetGraphScanner.Graph;

namespace DotNetGraphScanner.Export;

/// <summary>
/// Produces a self-contained interactive HTML file with a D3.js force-directed
/// graph. Nodes are colour-coded by kind; entry-point nodes are highlighted.
/// The panel on the right shows node/edge metadata on click.
/// External nodes can optionally be hidden via the UI toggle.
/// </summary>
public sealed class HtmlExporter : IGraphExporter
{
    private readonly bool _includeExternalNodes;

    public HtmlExporter(bool includeExternalNodes = false)
    {
        _includeExternalNodes = includeExternalNodes;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public Task ExportAsync(GraphModel graph, string outputPath, CancellationToken ct = default)
    {
        // Filter nodes/edges if not including external
        var nodes = graph.Nodes.Values
            .Where(n => _includeExternalNodes ||
                        !(n.Meta.TryGetValue("isExternal", out var ext) && ext == "true"))
            .ToList();

        var nodeIds = nodes.Select(n => n.Id).ToHashSet();
        var edges = graph.Edges
            .Where(e => nodeIds.Contains(e.SourceId) && nodeIds.Contains(e.TargetId))
            .ToList();

        var nodesJson = JsonSerializer.Serialize(nodes.Select(n => new
        {
            id = n.Id,
            label = n.Label,
            kind = n.Kind.ToString(),
            isEntryPoint = n.IsEntryPoint,
            meta = n.Meta
        }), JsonOpts);

        var edgesJson = JsonSerializer.Serialize(edges.Select(e => new
        {
            id = e.Id,
            source = e.SourceId,
            target = e.TargetId,
            kind = e.Kind.ToString(),
            label = e.Label,
            meta = e.Meta
        }), JsonOpts);

        var html = BuildHtml(nodesJson, edgesJson,
            Path.GetFileNameWithoutExtension(outputPath));
        File.WriteAllText(outputPath, html, Encoding.UTF8);
        Console.WriteLine($"  HTML  → {outputPath}");
        return Task.CompletedTask;
    }

    public static async Task RenderFromJsonAsync(
        string jsonPath, string htmlPath, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(jsonPath, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Re-serialise the two arrays as compact JSON strings so they can be
        // embedded verbatim in the HTML template.
        var nodesJson = root.TryGetProperty("nodes", out var nEl)
            ? nEl.GetRawText() : "[]";
        var edgesJson = root.TryGetProperty("edges", out var eEl)
            ? eEl.GetRawText() : "[]";

        var title = Path.GetFileNameWithoutExtension(htmlPath);
        var html  = BuildHtml(nodesJson, edgesJson, title);
        await File.WriteAllTextAsync(htmlPath, html, System.Text.Encoding.UTF8, ct);
        Console.WriteLine($"  HTML  → {htmlPath}");
    }

    private static string BuildHtml(string nodesJson, string edgesJson, string title) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<title>{{title}} – Dependency Graph</title>
<script src="https://cdn.jsdelivr.net/npm/d3@7/dist/d3.min.js"></script>
<style>
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0f1117; color: #e2e8f0; display: flex; height: 100vh; overflow: hidden; }
  #canvas-wrap { flex: 1; position: relative; overflow: hidden; }
  #gc { position: absolute; inset: 0; width: 100%; height: 100%; cursor: grab; will-change: transform; display: block; }
  #gc.dragging { cursor: grabbing; }

  /* Loading overlay */
  #loading-overlay { position: absolute; inset: 0; background: #0f1117e0; display: flex; align-items: center; justify-content: center; z-index: 500; transition: opacity .3s; }
  #loading-overlay.hidden { opacity: 0; pointer-events: none; }
  #loading-card { text-align: center; }
  @keyframes spin { to { transform: rotate(360deg); } }
  #loading-spinner { width: 36px; height: 36px; border: 3px solid #2d3348; border-top-color: #3b82f6; border-radius: 50%; animation: spin .8s linear infinite; margin: 0 auto 14px; }
  #loading-text { font-size: 15px; font-weight: 600; color: #e2e8f0; }
  #loading-sub { font-size: 11px; color: #64748b; margin-top: 5px; }

  /* Performance indicator */
  #perf { position: absolute; bottom: 12px; right: 360px; font-size: 11px; color: #334155; z-index: 5; pointer-events: none; }

  /* Sidebar */
  #panel { width: 340px; background: #1e2130; border-left: 1px solid #2d3348; display: flex; flex-direction: column; overflow: hidden; }
  #panel-header { padding: 14px 18px; border-bottom: 1px solid #2d3348; }
  #panel-header h1 { font-size: 14px; font-weight: 600; color: #94a3b8; letter-spacing: .05em; text-transform: uppercase; }
  #panel-header h2 { font-size: 18px; font-weight: 700; margin-top: 4px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  #panel-body { flex: 1; overflow-y: auto; padding: 14px 18px; font-size: 13px; }
  #panel-body table { width: 100%; border-collapse: collapse; margin-top: 10px; }
  #panel-body td { padding: 5px 0; vertical-align: top; border-bottom: 1px solid #2d3348; }
  #panel-body td:first-child { color: #94a3b8; width: 110px; padding-right: 10px; }
  #panel-body pre { background: #0f1117; border-radius: 6px; padding: 8px; overflow-x: auto; font-size: 11px; color: #7dd3fc; margin-top: 10px; }
  #panel-body .tag { display: inline-block; padding: 2px 8px; border-radius: 12px; font-size: 11px; font-weight: 600; margin-right: 4px; margin-bottom: 4px; }
  #panel-body .neighbours-section { margin-top: 14px; }
  #panel-body .neighbours-section h3 { font-size: 12px; font-weight: 600; color: #94a3b8; text-transform: uppercase; letter-spacing: .05em; margin-bottom: 6px; }
  #panel-body .neighbour-item { display: flex; align-items: center; gap: 8px; padding: 4px 0; border-bottom: 1px solid #1e2130; cursor: pointer; }
  #panel-body .neighbour-item:hover { color: #7dd3fc; }
  #panel-body .neighbour-item .dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
  #no-selection { color: #4b5563; font-size: 13px; margin-top: 20px; text-align: center; }

  /* Toolbar */
  #toolbar { position: absolute; top: 12px; left: 12px; display: flex; gap: 8px; flex-wrap: wrap; z-index: 10; }
  #toolbar button, #toolbar label, #toolbar select {
    background: #1e2130; border: 1px solid #2d3348; color: #e2e8f0;
    padding: 6px 12px; border-radius: 6px; font-size: 12px; cursor: pointer;
  }
  #toolbar button:hover { background: #2d3348; }
  #toolbar input[type=range] { width: 80px; cursor: pointer; }
  #filter-box { position: absolute; top: 12px; right: 360px; z-index: 10; }
  #filter-box input { background: #1e2130; border: 1px solid #2d3348; color: #e2e8f0; padding: 6px 12px; border-radius: 6px; font-size: 12px; width: 220px; }

  /* Tooltip */
  #edge-tip { position: absolute; background: #1e2130; border: 1px solid #3b82f6; border-radius: 6px; padding: 6px 10px; font-size: 12px; pointer-events: none; display: none; z-index: 30; max-width: 300px; word-break: break-all; }

  /* Legend */
  #legend { position: absolute; bottom: 12px; left: 12px; background: #1e2130cc; border: 1px solid #2d3348; border-radius: 8px; padding: 10px 14px; font-size: 12px; z-index: 10; }
  #legend h4 { font-size: 11px; font-weight: 600; color: #94a3b8; text-transform: uppercase; letter-spacing: .05em; margin-bottom: 8px; }
  .legend-item { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
  .legend-item .dot { width: 12px; height: 12px; border-radius: 50%; border: 2px solid transparent; }
  .legend-item .dot.entry { border-color: #f59e0b; }

  /* Toast */
  #toast { position: absolute; bottom: 16px; right: 360px; background: #1e4d6b; border: 1px solid #3b82f6; border-radius: 6px; padding: 8px 14px; font-size: 12px; display: none; z-index: 20; }

  ::-webkit-scrollbar { width: 6px; } ::-webkit-scrollbar-track { background: #1e2130; }
  ::-webkit-scrollbar-thumb { background: #2d3348; border-radius: 3px; }
</style>
</head>
<body>
<div id="canvas-wrap">
  <canvas id="gc"></canvas>
  <div id="loading-overlay">
    <div id="loading-card">
      <div id="loading-spinner"></div>
      <div id="loading-text">Calculating layout…</div>
      <div id="loading-sub" id="loading-sub">{{title}}</div>
    </div>
  </div>
  <div id="toolbar">
    <button onclick="resetZoom()">⟳ Reset View</button>
    <button onclick="toggleLabels()">Labels</button>
    <button id="freeze-btn" onclick="toggleFreeze()" title="Freeze/resume force layout">❄ Freeze</button>
    <label>
      Force: <input type="range" id="force-slider" min="50" max="600" value="250" oninput="updateForce(+this.value)">
    </label>
    <select id="kind-filter" onchange="applyFilter()">
      <option value="">All Kinds</option>
    </select>
    <label>
      <input type="checkbox" id="ext-toggle" onchange="applyFilter()"> Show External
    </label>
  </div>
  <div id="filter-box">
    <input id="search-input" type="text" placeholder="🔍 Search nodes…" oninput="debouncedFilter()"/>
  </div>
  <div id="legend"><h4>Node Kinds</h4></div>
  <div id="perf"></div>
  <div id="edge-tip"></div>
  <div id="toast"></div>
</div>

<div id="panel">
  <div id="panel-header">
    <h1>SELECTED NODE</h1>
    <div style="display:flex;align-items:center;gap:8px;margin-top:4px">
      <h2 id="panel-title" style="flex:1;min-width:0;font-size:18px;font-weight:700;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">–</h2>
      <button id="focus-btn" onclick="focusSelected()" title="Pan and zoom to this node" style="background:#1e2130;border:1px solid #2d3348;color:#94a3b8;padding:4px 8px;border-radius:5px;font-size:13px;cursor:pointer;flex-shrink:0">↗</button>
    </div>
  </div>
  <div id="panel-body">
    <p id="no-selection">Click a node to inspect</p>
  </div>
</div>

<!-- Web Worker source (D3 simulation runs off the main thread) -->
<script type="text/plain" id="worker-src">
'use strict';
try { importScripts('https://cdn.jsdelivr.net/npm/d3@7/dist/d3.min.js'); }
catch(e) { postMessage({type:'workerError',msg:String(e)}); }

const RADII = {Solution:18,Project:16,Namespace:12,Class:11,Interface:11,Struct:10,Enum:10,Method:7,Property:6,NuGetPackage:13,ExternalType:7};
let sim=null, nodeArr=[], gen=0, lastSent=0;
const SEND_MS = 40; // ~25fps position updates to main thread

function broadcast(isEnd) {
  const xy = new Float32Array(nodeArr.length * 2);
  for (let i=0; i<nodeArr.length; i++) { xy[2*i]=nodeArr[i].x||0; xy[2*i+1]=nodeArr[i].y||0; }
  postMessage({type:isEnd?'end':'tick', gen, xy, alpha:sim?sim.alpha():0}, [xy.buffer]);
  lastSent = Date.now();
}

self.onmessage = function({data:msg}) {
  switch(msg.type) {
    case 'init': {
      if (sim) { sim.stop(); sim=null; }
      gen = msg.gen;
      nodeArr = msg.nodes.map(n => ({id:n.id, kind:n.kind, x:n.x, y:n.y}));
      const edgeArr = msg.edges.map(e => ({source:e.source, target:e.target, kind:e.kind}));
      const c = msg.cfg;
      sim = d3.forceSimulation(nodeArr)
        .alphaDecay(c.alphaDecay).velocityDecay(c.velocityDecay)
        .force('link', d3.forceLink(edgeArr).id(d=>d.id)
          .distance(e=>e.kind==='Contains'?80:e.kind==='Calls'?55:100).strength(0.4).iterations(c.linkIter))
        .force('charge', d3.forceManyBody().strength(c.strength).theta(c.theta).distanceMax(600))
        .force('center', d3.forceCenter(c.w/2,c.h/2).strength(0.05))
        .force('collide', d3.forceCollide().radius(d=>(RADII[d.kind]||8)+2).strength(0.7))
        .on('tick', ()=>{ if(Date.now()-lastSent>=SEND_MS) broadcast(false); })
        .on('end',  ()=>broadcast(true));
      break;
    }
    case 'pin':   { const n=nodeArr[msg.idx]; if(n){n.fx=msg.x;n.fy=msg.y;} if(sim)sim.alphaTarget(0.05).restart(); break; }
    case 'move':  { const n=nodeArr[msg.idx]; if(n){n.fx=msg.x;n.fy=msg.y;} break; }
    case 'unpin': { const n=nodeArr[msg.idx]; if(n){n.fx=null;n.fy=null;} if(sim)sim.alphaTarget(0); break; }
    case 'reheat': if(sim)sim.alpha(msg.alpha||0.3).alphaTarget(0).restart(); break;
    case 'stop':   if(sim)sim.stop(); break;
  }
};
</script>

<script>
const RAW_NODES = {{nodesJson}};
const RAW_EDGES = {{edgesJson}};

// ── Constants ─────────────────────────────────────────────────────────────────
const COLOR = {
  Solution:'#6366f1', Project:'#3b82f6', Namespace:'#22d3ee',
  Class:'#10b981', Interface:'#f59e0b', Struct:'#84cc16', Enum:'#a78bfa',
  Method:'#64748b', Property:'#475569', NuGetPackage:'#f97316', ExternalType:'#374151',
};
const EDGE_COLOR = {
  Calls:'#475569', Contains:'#1e3a5f', Inherits:'#7c3aed', Implements:'#d97706',
  ProjectReference:'#3b82f6', PackageReference:'#f97316', EntryPoint:'#f59e0b',
};
const RADIUS = {
  Solution:18, Project:16, Namespace:12, Class:11, Interface:11, Struct:10,
  Enum:10, Method:7, Property:6, NuGetPackage:13, ExternalType:7,
};

// LOD tiers: as you zoom out, progressively fewer node/edge kinds are drawn.
// This keeps the overview of a 17k-node graph rendering in microseconds instead of milliseconds.
const LOD_TIERS = [
  { maxScale: 0.12,
    kinds: new Set(['Solution','Project']),
    edgeKinds: new Set(['ProjectReference','PackageReference','EntryPoint']) },
  { maxScale: 0.22,
    kinds: new Set(['Solution','Project','Namespace','NuGetPackage']),
    edgeKinds: new Set(['ProjectReference','PackageReference','EntryPoint','Inherits','Implements']) },
  { maxScale: 0.45,
    kinds: new Set(['Solution','Project','Namespace','NuGetPackage','Class','Interface','Struct','Enum']),
    edgeKinds: new Set(['ProjectReference','PackageReference','EntryPoint','Inherits','Implements','Contains']) },
  { maxScale: Infinity, kinds: null, edgeKinds: null }, // all visible
];
// Minimum scale at which each node kind first appears (for focusNode auto-zoom)
const KIND_MIN_SCALE = {
  Solution:0, Project:0, NuGetPackage:0.12, Namespace:0.12,
  Class:0.22, Interface:0.22, Struct:0.22, Enum:0.22,
  Method:0.45, Property:0.45, ExternalType:0.45,
};

// ── Mutable state ─────────────────────────────────────────────────────────────
let labelsVisible = true;
let frozen = false;
let selectedId = null;
let hoveredId = null;
let transform = d3.zoomIdentity;
let dirty = true;
let rafId = null;
let quadtree = null;
let lodTier = 3;          // current LOD tier index
let lodNodes = [];        // cache: LOD-filtered subset of nodes
let lodEdges = [];        // cache: LOD-filtered subset of edges
let positionsVersion = 0; // incremented when worker sends new positions
let treeVersion = -1;     // version at which quadtree was last built

// Worker / fallback sim state
let worker = null;
let workerOk = false;
let workerGen = 0;
let mainSim = null;       // fallback D3 sim on main thread
let simRunning = false;

// ── Lookup maps ───────────────────────────────────────────────────────────────
const nodeById = Object.fromEntries(RAW_NODES.map(n => [n.id, n]));
const edgesFrom = {}, edgesTo = {};
RAW_EDGES.forEach(e => {
  (edgesFrom[e.source] = edgesFrom[e.source] || []).push(e);
  (edgesTo[e.target]   = edgesTo[e.target]   || []).push(e);
});

// ── Canvas ────────────────────────────────────────────────────────────────────
const canvas = document.getElementById('gc');
const ctx = canvas.getContext('2d', { alpha: false });
const dpr = window.devicePixelRatio || 1;

function resizeCanvas() {
  const wrap = canvas.parentElement;
  canvas.width  = wrap.clientWidth  * dpr;
  canvas.height = wrap.clientHeight * dpr;
  canvas.style.width  = wrap.clientWidth  + 'px';
  canvas.style.height = wrap.clientHeight + 'px';
  markDirty();
}
window.addEventListener('resize', resizeCanvas);

// ── Zoom ──────────────────────────────────────────────────────────────────────
const zoom = d3.zoom()
  .scaleExtent([0.02, 10])
  .on('zoom', ev => { transform = ev.transform; markDirty(); });
d3.select(canvas).call(zoom);

// ── Loading overlay ───────────────────────────────────────────────────────────
const overlay = document.getElementById('loading-overlay');

let _loadingTimer = null;
function showLoading(nodeCount) {
  clearTimeout(_loadingTimer);
  overlay.classList.add('hidden'); // always reset first
  if (nodeCount < 300) return;
  // Only reveal after 500 ms — fast simulations finish before then and the
  // overlay never appears, preventing a distracting flash for small graphs.
  _loadingTimer = setTimeout(() => {
    document.getElementById('loading-sub').textContent =
      `${nodeCount.toLocaleString()} nodes – layout in progress`;
    overlay.classList.remove('hidden');
  }, 500);
}
function hideLoading() {
  clearTimeout(_loadingTimer);
  overlay.classList.add('hidden');
}

// ── RAF render loop ───────────────────────────────────────────────────────────
function markDirty() {
  dirty = true;
  if (!rafId) rafId = requestAnimationFrame(drawFrame);
}

// Fallback: manual tick budget when web worker is unavailable
function scheduleMainTick() {
  if (!simRunning || !mainSim) return;
  requestAnimationFrame(mainTickFrame);
}
function mainTickFrame() {
  if (!simRunning || !mainSim) return;
  // When frozen, stop the sim immediately and dismiss the overlay rather than
  // ticking — positions should not change while frozen.
  if (frozen) {
    mainSim.stop();
    simRunning = false;
    hideLoading();
    return;
  }
  const t0 = performance.now();
  while (performance.now() - t0 < 10 && mainSim.alpha() > mainSim.alphaMin()) {
    mainSim.tick();
  }
  positionsVersion++;
  maybeRebuildLod();
  maybeRebuildTree();
  markDirty();
  if (mainSim.alpha() > mainSim.alphaMin()) {
    scheduleMainTick();
  } else {
    simRunning = false;
    hideLoading();
  }
}

let lastFrameTime = 0, fps = 0, frameCount = 0;
const perfEl = document.getElementById('perf');

function drawFrame(ts) {
  rafId = null;
  if (!dirty) return;
  dirty = false;

  frameCount++;
  if (frameCount % 45 === 0) {
    fps = Math.round(45000 / (ts - lastFrameTime));
    lastFrameTime = ts;
    const lodLabel = lodTier < 3 ? ` · LOD ${lodTier}` : '';
    perfEl.textContent = `${fps} fps · ${lodNodes.length}/${nodes.length} nodes · ${lodEdges.length}/${edges.length} edges${lodLabel}`;
  }

  const W = canvas.width, H = canvas.height;
  const s = transform.k, tx = transform.x * dpr, ty = transform.y * dpr;

  // Recompute LOD tier and rebuild caches if zoom changed
  const newTier = LOD_TIERS.findIndex(t => s < t.maxScale);
  if (newTier !== lodTier) {
    lodTier = newTier;
    rebuildLod();
    rebuildTree();
  }

  // ── Background ─────────────────────────────────────────────────────────────
  ctx.fillStyle = '#0f1117';
  ctx.fillRect(0, 0, W, H);

  ctx.save();
  ctx.translate(tx, ty);
  ctx.scale(s * dpr, s * dpr);

  // Viewport in world space
  const vx0 = -tx / (s * dpr) - 80, vy0 = -ty / (s * dpr) - 80;
  const vx1 = vx0 + W / (s * dpr) + 160, vy1 = vy0 + H / (s * dpr) + 160;

  const showArrows = s > 0.3;
  const showLabels = labelsVisible && s > 0.45;

  // ── Edges ──────────────────────────────────────────────────────────────────
  ctx.globalAlpha = 0.4;
  ctx.lineWidth = 1 / s;

  const edgeBuckets = {};
  const arrowBuckets = {};
  for (const e of lodEdges) {
    const src = e.source, tgt = e.target;
    if (typeof src !== 'object' || !src.x) continue;
    if (Math.max(src.x, tgt.x) < vx0 || Math.min(src.x, tgt.x) > vx1 ||
        Math.max(src.y, tgt.y) < vy0 || Math.min(src.y, tgt.y) > vy1) continue;
    const col = EDGE_COLOR[e.kind] || '#475569';
    (edgeBuckets[col] = edgeBuckets[col] || []).push(e);
    if (showArrows) (arrowBuckets[col] = arrowBuckets[col] || []).push(e);
  }

  for (const [col, bucket] of Object.entries(edgeBuckets)) {
    ctx.strokeStyle = col;
    ctx.beginPath();
    for (const e of bucket) {
      ctx.moveTo(e.source.x, e.source.y);
      ctx.lineTo(e.target.x, e.target.y);
    }
    ctx.stroke();
  }

  if (showArrows) {
    ctx.globalAlpha = 0.65;
    const HL = 9 / s, HW = 4 / s;
    for (const [col, bucket] of Object.entries(arrowBuckets)) {
      ctx.fillStyle = col;
      ctx.beginPath();
      for (const e of bucket) {
        const sx = e.source.x, sy = e.source.y, ex2 = e.target.x, ey2 = e.target.y;
        const dx = ex2 - sx, dy = ey2 - sy, len = Math.sqrt(dx*dx+dy*dy);
        if (len < 1) continue;
        const r = (RADIUS[e.target.kind] || 8) + 2;
        const t2 = Math.max(0, len-r)/len;
        const px2 = sx+dx*t2, py2 = sy+dy*t2;
        const bx = px2-dx/len*HL, by = py2-dy/len*HL;
        const ox = -dy/len*HW, oy = dx/len*HW;
        ctx.moveTo(px2, py2); ctx.lineTo(bx+ox,by+oy); ctx.lineTo(bx-ox,by-oy); ctx.closePath();
      }
      ctx.fill();
    }
  }
  ctx.globalAlpha = 1;

  // ── Nodes ──────────────────────────────────────────────────────────────────
  ctx.lineWidth = 1.5 / s;
  const plainBuckets = {};
  const specialNodes = [];

  for (const n of lodNodes) {
    if (!n.x || n.x < vx0 || n.x > vx1 || n.y < vy0 || n.y > vy1) continue;
    if (n.id === selectedId || n.id === hoveredId || n.isEntryPoint) {
      specialNodes.push(n);
    } else {
      const col = COLOR[n.kind] || '#64748b';
      (plainBuckets[col] = plainBuckets[col] || []).push(n);
    }
  }

  ctx.strokeStyle = '#0f1117';
  for (const [col, bucket] of Object.entries(plainBuckets)) {
    ctx.fillStyle = col;
    ctx.beginPath();
    for (const n of bucket) {
      const r = RADIUS[n.kind] || 8;
      ctx.moveTo(n.x + r, n.y);
      ctx.arc(n.x, n.y, r, 0, Math.PI * 2);
    }
    ctx.fill();
    ctx.stroke();
  }

  for (const n of specialNodes) {
    const r = RADIUS[n.kind] || 8;
    const isSel = n.id === selectedId, isHov = n.id === hoveredId;
    ctx.fillStyle = COLOR[n.kind] || '#64748b';
    ctx.strokeStyle = isSel ? '#ffffff' : (n.isEntryPoint ? '#f59e0b' : '#0f1117');
    ctx.lineWidth = (isSel || isHov ? 3 : 2.5) / s;
    if (isSel || isHov) { ctx.shadowColor = isSel ? '#ffffff88' : '#94a3b888'; ctx.shadowBlur = 12/s; }
    ctx.beginPath(); ctx.arc(n.x, n.y, r, 0, Math.PI*2); ctx.fill(); ctx.stroke();
    ctx.shadowBlur = 0; ctx.lineWidth = 1.5 / s;
  }

  // ── Labels ─────────────────────────────────────────────────────────────────
  if (showLabels) {
    const fontSize = Math.min(12, 11/s);
    ctx.font = `${fontSize}px "Segoe UI", system-ui, sans-serif`;
    ctx.textAlign = 'center'; ctx.textBaseline = 'top';
    for (const n of lodNodes) {
      if (!n.x || n.x < vx0 || n.x > vx1 || n.y < vy0 || n.y > vy1) continue;
      ctx.fillStyle = n.id === selectedId ? '#f1f5f9' : (n.isEntryPoint ? '#fcd34d' : '#94a3b8');
      ctx.fillText(truncate(n.label, 24), n.x, n.y + (RADIUS[n.kind]||8) + 2.5/s);
    }
  }

  ctx.restore();

  if (simRunning) markDirty();
}

// ── LOD cache management ──────────────────────────────────────────────────────
function rebuildLod() {
  const tier = LOD_TIERS[lodTier];
  if (!tier.kinds) {
    lodNodes = nodes;
    lodEdges = edges;
  } else {
    lodNodes = nodes.filter(n => tier.kinds.has(n.kind));
    const vis = new Set(lodNodes.map(n => n.id));
    lodEdges = edges.filter(e => {
      if (!tier.edgeKinds.has(e.kind)) return false;
      const sid = typeof e.source === 'object' ? e.source.id : e.source;
      const tid = typeof e.target === 'object' ? e.target.id : e.target;
      return vis.has(sid) && vis.has(tid);
    });
  }
}

function maybeRebuildLod() {
  const newTier = LOD_TIERS.findIndex(t => transform.k < t.maxScale);
  if (newTier !== lodTier) { lodTier = newTier; rebuildLod(); }
}

// ── Quadtree (built from LOD-visible nodes only) ──────────────────────────────
let qtBuildTimer = null;
function rebuildTree() {
  quadtree = d3.quadtree(lodNodes, n => n.x, n => n.y);
  treeVersion = positionsVersion;
}
// Throttled: don't rebuild more than once per animation frame
function maybeRebuildTree() {
  if (positionsVersion === treeVersion) return;
  if (!qtBuildTimer) qtBuildTimer = requestAnimationFrame(() => { qtBuildTimer = null; rebuildTree(); });
}

function nodeAtCanvas(cx, cy) {
  if (!quadtree) return null;
  const wx = (cx-transform.x)/transform.k, wy = (cy-transform.y)/transform.k;
  const r = 20/transform.k;
  const n = quadtree.find(wx, wy, r);
  if (!n) return null;
  return Math.hypot(n.x-wx, n.y-wy) <= (RADIUS[n.kind]||8)+4/transform.k ? n : null;
}

function edgeAtCanvas(cx, cy) {
  const wx = (cx-transform.x)/transform.k, wy = (cy-transform.y)/transform.k;
  const thr = 6/transform.k;
  let best = null, bestD = thr;
  for (const e of lodEdges) {
    const s = e.source, t = e.target;
    if (typeof s !== 'object' || !s.x) continue;
    const dx = t.x-s.x, dy = t.y-s.y, len2 = dx*dx+dy*dy;
    if (!len2) continue;
    const u = Math.max(0, Math.min(1, ((wx-s.x)*dx+(wy-s.y)*dy)/len2));
    const d = Math.hypot(s.x+u*dx-wx, s.y+u*dy-wy);
    if (d < bestD) { bestD = d; best = e; }
  }
  return best;
}

// ── Web Worker setup ──────────────────────────────────────────────────────────
function initWorker() {
  try {
    const src = document.getElementById('worker-src').textContent;
    const blob = new Blob([src], {type:'text/javascript'});
    worker = new Worker(URL.createObjectURL(blob));
    worker.onmessage = onWorkerMessage;
    worker.onerror = ev => {
      console.warn('Worker error:', ev.message);
      workerOk = false; worker = null;
      // fall back to budgeted main-thread simulation
      if (simRunning) startMainSim();
    };
    workerOk = true;
  } catch(e) {
    console.warn('Web Worker unavailable, using main-thread fallback:', e);
    workerOk = false;
  }
}

function onWorkerMessage({data: msg}) {
  if (msg.type === 'workerError') {
    console.warn('Worker reported error:', msg.msg);
    workerOk = false; worker = null; if (simRunning) startMainSim(); return;
  }
  if (msg.type !== 'tick' && msg.type !== 'end') return;
  if (msg.gen !== workerGen) return;
  if (msg.type === 'end') {
    // Always process 'end' — even when frozen — so the loading overlay is dismissed.
    simRunning = false;
    hideLoading();
    return;
  }
  // 'tick' — discard position updates while frozen (stop msg may be in-flight).
  if (frozen) return;
  const xy = msg.xy;
  for (let i = 0; i < nodes.length; i++) { nodes[i].x = xy[2*i]; nodes[i].y = xy[2*i+1]; }
  positionsVersion++;
  maybeRebuildLod();
  maybeRebuildTree();
  markDirty();
}

// ── Pointer events ────────────────────────────────────────────────────────────
let dragNode = null;

d3.select(canvas)
  .on('mousemove.graph', ev => {
    const [cx,cy] = d3.pointer(ev);
    const n = nodeAtCanvas(cx,cy);
    const prev = hoveredId;
    hoveredId = n ? n.id : null;
    canvas.style.cursor = n ? 'pointer' : 'grab';
    if (hoveredId !== prev) markDirty();
  })
  .on('click.graph', ev => {
    if (ev.defaultPrevented) return;
    const [cx,cy] = d3.pointer(ev);
    const n = nodeAtCanvas(cx,cy);
    if (n) { selectNode(n.id); }
    else {
      const e = edgeAtCanvas(cx,cy);
      if (e) showEdgeTip(e, ev.clientX, ev.clientY);
      else { selectedId = null; clearPanel(); markDirty(); }
    }
  });

(function setupNodeDrag() {
  const drag = d3.drag()
    .filter(ev => !!nodeAtCanvas(...d3.pointer(ev, canvas)))
    .on('start', ev => {
      dragNode = nodeAtCanvas(...d3.pointer(ev, canvas));
      if (!dragNode) return;
      canvas.classList.add('dragging');
      if (workerOk && worker) {
        worker.postMessage({type:'pin', idx:dragNode._wi, x:dragNode.x, y:dragNode.y});
      } else if (mainSim) {
        dragNode.fx = dragNode.x; dragNode.fy = dragNode.y;
        mainSim.alphaTarget(0.05).restart(); simRunning = true; scheduleMainTick();
      }
    })
    .on('drag', ev => {
      if (!dragNode) return;
      const wx = (ev.x-transform.x)/transform.k, wy = (ev.y-transform.y)/transform.k;
      dragNode.x = wx; dragNode.y = wy; // immediate visual feedback
      if (workerOk && worker) {
        worker.postMessage({type:'move', idx:dragNode._wi, x:wx, y:wy});
      } else if (mainSim) {
        dragNode.fx = wx; dragNode.fy = wy;
      }
      positionsVersion++; maybeRebuildTree(); markDirty();
    })
    .on('end', ev => {
      if (!dragNode) return;
      if (workerOk && worker) {
        worker.postMessage({type:'unpin', idx:dragNode._wi});
      } else if (mainSim) {
        dragNode.fx = null; dragNode.fy = null;
        mainSim.alphaTarget(0);
      }
      dragNode = null; canvas.classList.remove('dragging');
    });
  d3.select(canvas).call(drag).call(zoom);
})();

// ── Filter + rebuild ──────────────────────────────────────────────────────────
let nodes = [], edges = [];

let _filterTimer = null;
function debouncedFilter() {
  clearTimeout(_filterTimer);
  _filterTimer = setTimeout(applyFilter, 280);
}

function applyFilter() {
  const kindVal = document.getElementById('kind-filter').value;
  const search  = document.getElementById('search-input').value.toLowerCase();
  const showExt = document.getElementById('ext-toggle').checked;
  nodes = RAW_NODES.filter(n => {
    if (kindVal && n.kind !== kindVal) return false;
    if (!showExt && n.meta?.isExternal === 'true') return false;
    if (search && !n.label.toLowerCase().includes(search) &&
        !(n.meta?.fullName||'').toLowerCase().includes(search)) return false;
    return true;
  });
  const vis = new Set(nodes.map(n => n.id));
  // Resolve source/target to node objects so the Canvas drawing code can
  // read .x/.y directly without a per-edge lookup at draw time.
  const nodeIndex = Object.fromEntries(nodes.map(n => [n.id, n]));
  edges = RAW_EDGES
    .filter(e => vis.has(e.source) && vis.has(e.target))
    .map(e => ({...e, source: nodeIndex[e.source], target: nodeIndex[e.target]}));
  rebuild();
}

const KIND_RING = {Solution:0, Project:150, NuGetPackage:250, Namespace:320,
                   Class:480, Interface:480, Struct:480, Enum:480,
                   Method:640, Property:640, ExternalType:720};

function rebuild() {
  // Preserve positions for nodes we've already laid out
  const cx = canvas.clientWidth / 2, cy = canvas.clientHeight / 2;
  const kindCounters = {};
  const kindTotals = {};
  nodes.forEach(n => kindTotals[n.kind] = (kindTotals[n.kind]||0)+1);
  nodes.forEach((n, i) => {
    n._wi = i; // worker index
    if (n.x !== undefined && !isNaN(n.x)) return;
    // Seed position in a ring by kind for faster convergence
    const r = KIND_RING[n.kind] ?? 500;
    const total = kindTotals[n.kind] || 1;
    const idx = kindCounters[n.kind] = (kindCounters[n.kind]||0) + 1;
    const angle = (2 * Math.PI * (idx-1)) / total;
    n.x = cx + r * Math.cos(angle) + (Math.random()-.5)*40;
    n.y = cy + r * Math.sin(angle) + (Math.random()-.5)*40;
  });

  const nc = nodes.length;
  const cfg = {
    alphaDecay:  nc > 2000 ? 0.04  : nc > 500 ? 0.028 : 0.02,
    velocityDecay: nc > 1000 ? 0.5 : 0.4,
    linkIter:    nc > 1000 ? 1 : 3,
    strength:    forceManyBodyStrength,
    theta:       nc > 1000 ? 0.9 : 0.8,
    w:           canvas.clientWidth,
    h:           canvas.clientHeight,
  };

  if (workerOk && worker) {
    workerGen++;
    worker.postMessage({
      type:'init', gen:workerGen,
      nodes: nodes.map(n => ({id:n.id, kind:n.kind, x:n.x||0, y:n.y||0})),
      // source/target may already be node objects after applyFilter resolves them;
      // always send plain string IDs to the worker.
      edges: edges.map(e => ({
        source: e.source?.id ?? e.source,
        target: e.target?.id ?? e.target,
        kind: e.kind
      })),
      cfg
    });
    simRunning = true;
  } else {
    startMainSim(cfg);
  }

  showLoading(nc);
  // Compute current LOD tier from present zoom level, then rebuild caches
  lodTier = LOD_TIERS.findIndex(t => transform.k < t.maxScale);
  if (lodTier < 0) lodTier = LOD_TIERS.length - 1;
  rebuildLod();
  rebuildTree();
  markDirty();
}

function startMainSim(cfg) {
  if (mainSim) mainSim.stop();
  const nc = nodes.length;
  const c = cfg || {
    alphaDecay: nc > 2000 ? 0.04 : nc > 500 ? 0.028 : 0.02,
    velocityDecay: nc > 1000 ? 0.5 : 0.4,
    linkIter: nc > 1000 ? 1 : 3,
    strength: forceManyBodyStrength,
    theta: nc > 1000 ? 0.9 : 0.8,
    w: canvas.clientWidth, h: canvas.clientHeight,
  };
  mainSim = d3.forceSimulation(nodes)
    .alphaDecay(c.alphaDecay).velocityDecay(c.velocityDecay)
    .force('link', d3.forceLink(edges).id(d=>d.id)
      .distance(e=>e.kind==='Contains'?80:e.kind==='Calls'?55:100).strength(0.4).iterations(c.linkIter))
    .force('charge', d3.forceManyBody().strength(c.strength).theta(c.theta).distanceMax(600))
    .force('center', d3.forceCenter(c.w/2,c.h/2).strength(0.05))
    .force('collide', d3.forceCollide().radius(d=>(RADIUS[d.kind]||8)+2).strength(0.7))
    .stop();
  simRunning = true;
  scheduleMainTick();
}

// ── Panel / selection ─────────────────────────────────────────────────────────
// Fields that are surfaced with dedicated UI — omit from the raw meta table.
const PROMOTED_META = new Set(['httpMethod','routeTemplate','functionName']);

// HTTP method → pill colour
const HTTP_COLOR = {
  GET:'#16a34a', POST:'#2563eb', PUT:'#d97706', DELETE:'#dc2626',
  PATCH:'#7c3aed', HEAD:'#0891b2', OPTIONS:'#64748b',
};

function selectNode(id) {
  selectedId = id;
  markDirty();
  const n = nodeById[id];
  if (!n) return;
  document.getElementById('panel-title').textContent = n.label;
  const outgoing = (edgesFrom[id]||[]).filter(e => e.kind !== 'Contains');
  const incoming = (edgesTo[id]||[]).filter(e => e.kind !== 'Contains');
  const col = COLOR[n.kind] || '#64748b';

  // ── Tag row ──
  let html = `<div>
    <span class="tag" style="background:${col}22;color:${col};border:1px solid ${col}55">${n.kind}</span>
    ${n.isEntryPoint ? '<span class="tag" style="background:#78350f;color:#fcd34d">⚡ Entry Point</span>' : ''}
  </div>`;

  // ── Route banner ──
  const route  = n.meta?.routeTemplate;
  const verb   = n.meta?.httpMethod;
  const fnName = n.meta?.functionName;
  if (route || verb || fnName) {
    html += `<div style="margin:8px 0 4px;display:flex;align-items:center;gap:6px;flex-wrap:wrap">`;
    if (verb) {
      const vc = HTTP_COLOR[verb] || '#64748b';
      html += `<span style="background:${vc};color:#fff;font-size:11px;font-weight:700;`+
              `padding:3px 8px;border-radius:4px;letter-spacing:.05em">${escHtml(verb)}</span>`;
    }
    if (route) {
      html += `<code style="background:#1e293b;color:#e2e8f0;font-size:12px;padding:3px 8px;`+
              `border-radius:4px;word-break:break-all">${escHtml(route)}</code>`;
    }
    if (fnName) {
      html += `<span style="background:#1e293b;color:#e2e8f0;font-size:12px;padding:3px 8px;`+
              `border-radius:4px">ƒ ${escHtml(fnName)}</span>`;
    }
    html += `</div>`;
  }

  // ── Meta table (skip promoted fields) ──
  html += `<table>`;
  html += `<tr><td>ID</td><td style="word-break:break-all;font-size:11px;color:#64748b">${escHtml(id)}</td></tr>`;
  Object.entries(n.meta||{}).forEach(([k,v]) => {
    if (PROMOTED_META.has(k)) return;
    html += `<tr><td>${escHtml(k)}</td><td style="word-break:break-all">${escHtml(v)}</td></tr>`;
  });
  html += `</table>`;
  const renderNb = (list, label, idKey) => {
    if (!list.length) return '';
    let h = `<div class="neighbours-section"><h3>${label} (${list.length})</h3>`;
    list.slice(0,80).forEach(e => {
      const peerId = e[idKey];
      const peer = nodeById[peerId]; if (!peer) return;
      const c2 = COLOR[peer.kind]||'#64748b';
      h += `<div class="neighbour-item" data-nid="${escAttr(peerId)}" style="cursor:pointer">` +
           `<div class="dot" style="background:${c2}"></div>` +
           `<span style="color:#64748b;font-size:11px;min-width:70px">${escHtml(e.kind)}</span>` +
           `<span>${escHtml(peer.label)}</span></div>`;
    });
    if (list.length > 80) h += `<div style="color:#64748b;font-size:11px;padding:4px 0">… and ${list.length-80} more</div>`;
    return h + '</div>';
  };
  html += renderNb(outgoing, 'Calls / Dependencies', 'target');
  html += renderNb(incoming, 'Called By', 'source');
  document.getElementById('panel-body').innerHTML = html;
}

function clearPanel() {
  document.getElementById('panel-title').textContent = '–';
  document.getElementById('panel-body').innerHTML = '<p id="no-selection">Click a node to inspect</p>';
}

// Delegated click handler for neighbour items — avoids any ID-in-attribute escaping issues.
document.getElementById('panel-body').addEventListener('click', ev => {
  const item = ev.target.closest('.neighbour-item[data-nid]');
  if (!item) return;
  selectNode(item.dataset.nid);
});

function focusSelected() {
  if (!selectedId) return;
  const n = nodeById[selectedId];
  if (!n || !n.x) return;
  const minScale = Math.max(KIND_MIN_SCALE[n.kind] || 0, 0.5);
  const targetScale = Math.max(transform.k, minScale);
  const cx = canvas.clientWidth/2, cy = canvas.clientHeight/2;
  d3.select(canvas).transition().duration(600)
    .call(zoom.transform, d3.zoomIdentity.translate(cx - n.x*targetScale, cy - n.y*targetScale).scale(targetScale));
}

function showEdgeTip(e, clientX, clientY) {
  const wr = canvas.parentElement.getBoundingClientRect();
  const s = nodeById[e.source?.id || e.source], t = nodeById[e.target?.id || e.target];
  const tip = document.getElementById('edge-tip');
  tip.textContent = `${s?.label||'?'} → [${e.kind}] → ${t?.label||'?'}${e.meta?.line?' @ line '+e.meta.line:''}`;
  tip.style.left = (clientX-wr.left+10)+'px'; tip.style.top = (clientY-wr.top+10)+'px';
  tip.style.display = 'block';
  clearTimeout(tip._tid); tip._tid = setTimeout(()=>tip.style.display='none', 3000);
}

// ── Toolbar callbacks ─────────────────────────────────────────────────────────
let forceManyBodyStrength = -250;

function toggleLabels() { labelsVisible = !labelsVisible; markDirty(); }

function toggleFreeze() {
  frozen = !frozen;
  const btn = document.getElementById('freeze-btn');
  if (frozen) {
    btn.textContent = '▶ Resume'; btn.style.color = '#f59e0b';
    if (workerOk && worker) worker.postMessage({type:'stop'});
    else if (mainSim) mainSim.stop();
    simRunning = false;
  } else {
    btn.textContent = '❄ Freeze'; btn.style.color = '';
    if (workerOk && worker) worker.postMessage({type:'reheat', alpha:0.1});
    else if (mainSim) { mainSim.alpha(0.1).restart(); mainSim.stop(); simRunning = true; scheduleMainTick(); }
    simRunning = true; markDirty();
  }
}

function resetZoom() {
  d3.select(canvas).transition().duration(500).call(zoom.transform, d3.zoomIdentity);
}

function updateForce(v) {
  forceManyBodyStrength = -v;
  const nc = nodes.length, theta = nc > 1000 ? 0.9 : 0.8;
  if (workerOk && worker) {
    // Restart worker with same nodes/edges but new strength;  full reinit fastest
    if (nodes.length) {
      workerGen++;
      worker.postMessage({
        type:'init', gen:workerGen,
        nodes: nodes.map(n => ({id:n.id, kind:n.kind, x:n.x||0, y:n.y||0})),
        edges: edges.map(e => ({source:e.source, target:e.target, kind:e.kind})),
        cfg: { alphaDecay: nc>2000?0.04:nc>500?0.028:0.02, velocityDecay:nc>1000?0.5:0.4,
               linkIter:nc>1000?1:3, strength:forceManyBodyStrength, theta, w:canvas.clientWidth, h:canvas.clientHeight },
        edges: edges.map(e => ({
          source: e.source?.id ?? e.source,
          target: e.target?.id ?? e.target,
          kind: e.kind
        }))
      });
      simRunning = true;
    }
  } else if (mainSim) {
    mainSim.force('charge', d3.forceManyBody().strength(forceManyBodyStrength).theta(theta).distanceMax(600));
    mainSim.alpha(0.3).restart(); mainSim.stop(); simRunning = true; scheduleMainTick();
  }
}

// ── Legend + kind filter ──────────────────────────────────────────────────────
const kinds = [...new Set(RAW_NODES.map(n => n.kind))].sort();
const kf = document.getElementById('kind-filter');
kinds.forEach(k => { const o=document.createElement('option'); o.value=k; o.textContent=k; kf.appendChild(o); });
const legend = document.getElementById('legend');
kinds.forEach(k => {
  const item = document.createElement('div'); item.className='legend-item';
  item.innerHTML = `<div class="dot" style="background:${COLOR[k]||'#64748b'}"></div><span>${k}</span>`;
  legend.appendChild(item);
});
{ const item=document.createElement('div'); item.className='legend-item';
  item.innerHTML=`<div class="dot entry" style="background:transparent"></div><span>Entry Point</span>`;
  legend.appendChild(item); }

// ── Helpers ───────────────────────────────────────────────────────────────────
function escHtml(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
// Encode all HTML-special chars so the value is safe inside a data-* attribute.
function escAttr(s) { return escHtml(String(s)); }
function truncate(s,n) { return s.length>n ? s.slice(0,n)+'…' : s; }

// ── Keyboard ──────────────────────────────────────────────────────────────────
document.addEventListener('keydown', ev => {
  if (ev.key==='f'||ev.key==='F') document.getElementById('search-input').focus();
  if (ev.key==='l'||ev.key==='L') toggleLabels();
  if (ev.key==='z'||ev.key==='Z') toggleFreeze();
  if (ev.key==='Enter' && selectedId) focusSelected();
  if (ev.key==='Escape') { selectedId=null; clearPanel(); markDirty(); }
});

// ── Init ──────────────────────────────────────────────────────────────────────
resizeCanvas();
initWorker();
applyFilter();
</script>
</body>
</html>
""";
}
