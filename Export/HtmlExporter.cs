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
        // Always embed all nodes/edges — the JS "Show External" checkbox toggles visibility live.
        var nodes = graph.Nodes.Values.ToList();
        var edges = graph.Edges.ToList();

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

  /* Hierarchy view */
  #hier-view { position: absolute; inset: 0; overflow: hidden; display: none; }
  #hier-svg  { width: 100%; height: 100%; cursor: grab; }
  #hier-svg.hier-panning { cursor: grabbing; }
  .h-box { cursor: pointer; }
  .h-box:hover .h-header-rect { filter: brightness(1.25); }

  /* ── Context menu ──────────────────────────────────────────────────────── */
  #ctx-menu {
    position: fixed; z-index: 9000; display: none;
    background: #151925; border: 1px solid #2d3348;
    border-radius: 9px; box-shadow: 0 6px 28px #000c;
    min-width: 210px; padding: 4px 0; font-size: 13px;
    color: #e2e8f0; user-select: none;
  }
  #ctx-menu.visible { display: block; }
  .ctx-header {
    padding: 6px 14px 5px; font-size: 11px; color: #64748b; font-weight: 700;
    text-transform: uppercase; letter-spacing: .06em;
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 220px;
    border-bottom: 1px solid #2d3348; margin-bottom: 3px;
  }
  .ctx-sep { height: 1px; background: #2d3348; margin: 3px 0; }
  .ctx-item {
    padding: 7px 32px 7px 14px; cursor: pointer; display: flex;
    align-items: center; gap: 8px; white-space: nowrap; position: relative;
  }
  .ctx-item:hover { background: #1e2536; }
  .ctx-item .ctx-arrow { position: absolute; right: 12px; font-size: 9px; color: #475569; }
  .ctx-sub {
    display: none; position: absolute; left: calc(100% + 2px); top: -4px;
    background: #151925; border: 1px solid #2d3348;
    border-radius: 9px; box-shadow: 0 6px 28px #000c;
    min-width: 210px; padding: 4px 0; z-index: 9001;
  }
  .ctx-item:hover > .ctx-sub { display: block; }
  .ctx-sub-header {
    padding: 5px 14px 2px; font-size: 10px; color: #475569;
    text-transform: uppercase; letter-spacing: .06em;
  }
</style>
</head>
<body>
<div id="canvas-wrap">
  <canvas id="gc"></canvas>
  <div id="hier-view"><svg id="hier-svg"></svg></div>
  <div id="loading-overlay">
    <div id="loading-card">
      <div id="loading-spinner"></div>
      <div id="loading-text">Calculating layout…</div>
      <div id="loading-sub" id="loading-sub">{{title}}</div>
    </div>
  </div>
  <div id="toolbar">
    <select id="view-mode" onchange="switchView(this.value)" title="Switch visualization style" style="font-weight:600">
      <option value="force">⚡ Force Graph</option>
      <option value="hierarchy">▦ Hierarchy</option>
    </select>
    <button class="force-only" onclick="resetZoom()">⟳ Reset</button>
    <button class="force-only" onclick="toggleLabels()">Labels</button>
    <button id="freeze-btn" class="force-only" onclick="toggleFreeze()" title="Freeze/resume force layout">❄ Freeze</button>
    <label class="force-only">
      Force: <input type="range" id="force-slider" min="50" max="600" value="250" oninput="updateForce(+this.value)">
    </label>
    <button class="hier-only" style="display:none" onclick="resetHierZoom()">⟳ Reset</button>
    <button class="hier-only" style="display:none" onclick="hierExpandAll(2)">↕ 2 Levels</button>
    <button class="hier-only" style="display:none" onclick="hierExpandAll(3)">↕ 3 Levels</button>
    <button class="hier-only" style="display:none" onclick="hierCollapseAll()">⟵ Collapse</button>
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

  <!-- Context menu -->
  <div id="ctx-menu">
    <div class="ctx-header" id="ctx-node-label">–</div>
    <div class="ctx-item" id="ctx-hl-item">
      <span>⬤ Highlight References</span>
    </div>
    <div class="ctx-sep"></div>
    <div class="ctx-item">
      <span>⬡ Filter To</span><span class="ctx-arrow">▶</span>
      <div class="ctx-sub">
        <div class="ctx-sub-header">Filter To</div>
        <div class="ctx-item" data-action="filter-node">This node only</div>
        <div class="ctx-item" data-action="filter-neighbours">This node + neighbours</div>
        <div class="ctx-item" data-action="filter-kind">All of kind: <em id="ctx-kind-label" style="font-style:italic;color:#94a3b8"></em></div>
        <div class="ctx-sep"></div>
        <div class="ctx-sub-header">Hierarchy</div>
        <div class="ctx-item" data-action="hier-subtree">This subtree only</div>
        <div class="ctx-item" data-action="hier-expand-path">Expand path to root</div>
      </div>
    </div>
    <div class="ctx-item">
      <span>↗ Expand To</span><span class="ctx-arrow">▶</span>
      <div class="ctx-sub">
        <div class="ctx-sub-header">By arrow direction</div>
        <div class="ctx-item" data-action="expand-incoming">← Incoming arrows</div>
        <div class="ctx-item" data-action="expand-outgoing">→ Outgoing arrows</div>
        <div class="ctx-item" data-action="expand-all">↔ All arrows</div>
        <div class="ctx-sep"></div>
        <div class="ctx-sub-header">Functional</div>
        <div class="ctx-item" data-action="expand-functional">Calls &amp; dependencies (via members)</div>
      </div>
    </div>
    <div class="ctx-sep"></div>
    <div class="ctx-item" id="ctx-clear-item">✕ Clear Filter / Reset View</div>
  </div>
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

const RADII = {Solution:18,Project:16,Namespace:12,Class:11,Interface:11,Struct:10,Enum:10,Method:7,Property:6,Field:5,NuGetPackage:13,ExternalType:7};
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
  Method:'#64748b', Property:'#475569', Field:'#94a3b8', NuGetPackage:'#f97316', ExternalType:'#374151',
};
const EDGE_COLOR = {
  Calls:'#475569', Contains:'#1e3a5f', Inherits:'#7c3aed', Implements:'#d97706',
  ProjectReference:'#3b82f6', PackageReference:'#f97316', EntryPoint:'#f59e0b',
  Accesses:'#334155', UsesAttribute:'#8b5cf6',
};
const RADIUS = {
  Solution:18, Project:16, Namespace:12, Class:11, Interface:11, Struct:10,
  Enum:10, Method:7, Property:6, Field:5, NuGetPackage:13, ExternalType:7,
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
    edgeKinds: new Set(['ProjectReference','PackageReference','EntryPoint','Inherits','Implements','Contains','UsesAttribute']) },
  { maxScale: Infinity, kinds: null, edgeKinds: null }, // all visible
];
// Minimum scale at which each node kind first appears (for focusNode auto-zoom)
const KIND_MIN_SCALE = {
  Solution:0, Project:0, NuGetPackage:0.12, Namespace:0.12,
  Class:0.22, Interface:0.22, Struct:0.22, Enum:0.22,
  Method:0.45, Property:0.45, Field:0.45, ExternalType:0.45,
};

// ── Mutable state ─────────────────────────────────────────────────────────────
let labelsVisible = true;
let frozen = false;
let selectedId = null;
let hoveredId = null;
let hlMode = false;          // highlight-references mode active
let hlNodeIds = new Set();   // node IDs that are highlighted
let hlEdgeKeys = new Set();  // "srcId→tgtId" edge keys that are highlighted
let ctxMenuNodeId = null;    // node that was right-clicked
let ctxFilterActive = false; // bypass LOD kind-filter while a ctx filter is applied
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
  ctx.lineWidth = 1 / s;

  const edgeBuckets = {}, arrowBuckets = {};
  const hlEdgeBuckets = {}, hlArrowBuckets = {};
  for (const e of lodEdges) {
    const src = e.source, tgt = e.target;
    if (typeof src !== 'object' || !src.x) continue;
    if (Math.max(src.x, tgt.x) < vx0 || Math.min(src.x, tgt.x) > vx1 ||
        Math.max(src.y, tgt.y) < vy0 || Math.min(src.y, tgt.y) > vy1) continue;
    const col = EDGE_COLOR[e.kind] || '#475569';
    const isHl = hlMode && hlEdgeKeys.has(src.id + '→' + tgt.id);
    if (isHl) {
      (hlEdgeBuckets[col] = hlEdgeBuckets[col] || []).push(e);
      if (showArrows) (hlArrowBuckets[col] = hlArrowBuckets[col] || []).push(e);
    } else {
      (edgeBuckets[col] = edgeBuckets[col] || []).push(e);
      if (showArrows) (arrowBuckets[col] = arrowBuckets[col] || []).push(e);
    }
  }

  // Dim (background) edges
  ctx.globalAlpha = hlMode ? 0.07 : 0.4;
  for (const [col, bucket] of Object.entries(edgeBuckets)) {
    ctx.strokeStyle = col;
    ctx.beginPath();
    for (const e of bucket) {
      ctx.moveTo(e.source.x, e.source.y);
      ctx.lineTo(e.target.x, e.target.y);
    }
    ctx.stroke();
  }

  // Highlighted edges (bright + thicker)
  if (hlMode) {
    ctx.globalAlpha = 0.9;
    ctx.lineWidth = 2.5 / s;
    for (const [col, bucket] of Object.entries(hlEdgeBuckets)) {
      ctx.strokeStyle = col;
      ctx.beginPath();
      for (const e of bucket) {
        ctx.moveTo(e.source.x, e.source.y);
        ctx.lineTo(e.target.x, e.target.y);
      }
      ctx.stroke();
    }
    ctx.lineWidth = 1 / s;
  }

  if (showArrows) {
    const HL = 9 / s, HW = 4 / s;
    function drawArrowBuckets(buckets, alpha) {
      ctx.globalAlpha = alpha;
      for (const [col, bucket] of Object.entries(buckets)) {
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
    drawArrowBuckets(arrowBuckets, hlMode ? 0.08 : 0.65);
    if (hlMode) drawArrowBuckets(hlArrowBuckets, 0.9);
  }
  ctx.globalAlpha = 1;

  // ── Nodes ──────────────────────────────────────────────────────────────────
  ctx.lineWidth = 1.5 / s;
  const plainBuckets = {};
  const dimBuckets = {};
  const specialNodes = [];

  for (const n of lodNodes) {
    if (!n.x || n.x < vx0 || n.x > vx1 || n.y < vy0 || n.y > vy1) continue;
    if (n.id === selectedId || n.id === hoveredId || n.isEntryPoint ||
        (hlMode && hlNodeIds.has(n.id))) {
      specialNodes.push(n);
    } else if (hlMode) {
      const col = COLOR[n.kind] || '#64748b';
      (dimBuckets[col] = dimBuckets[col] || []).push(n);
    } else {
      const col = COLOR[n.kind] || '#64748b';
      (plainBuckets[col] = plainBuckets[col] || []).push(n);
    }
  }

  ctx.strokeStyle = '#0f1117';

  // Dimmed non-highlighted nodes (in highlight mode)
  if (hlMode) {
    ctx.globalAlpha = 0.12;
    for (const [col, bucket] of Object.entries(dimBuckets)) {
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
    ctx.globalAlpha = 1;
  }

  // Normal nodes
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
    const isHl = hlMode && hlNodeIds.has(n.id);
    ctx.fillStyle = COLOR[n.kind] || '#64748b';
    ctx.strokeStyle = isSel ? '#ffffff' : (isHl ? '#60a5fa' : (n.isEntryPoint ? '#f59e0b' : '#0f1117'));
    ctx.lineWidth = (isSel || isHov ? 3 : 2.5) / s;
    if (isSel || isHov || isHl) {
      ctx.shadowColor = isSel ? '#ffffff88' : (isHl ? '#60a5fa88' : '#94a3b888');
      ctx.shadowBlur = 12/s;
    }
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
  // When a context filter is active, always show all node kinds regardless of zoom
  if (!tier.kinds || ctxFilterActive) {
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
    hideCtxMenu();
    const [cx,cy] = d3.pointer(ev);
    const n = nodeAtCanvas(cx,cy);
    if (n) { selectNode(n.id); }
    else {
      clearHighlight();
      const e = edgeAtCanvas(cx,cy);
      if (e) showEdgeTip(e, ev.clientX, ev.clientY);
      else { selectedId = null; clearPanel(); markDirty(); }
    }
  })
  .on('contextmenu.graph', ev => {
    ev.preventDefault();
    const [cx,cy] = d3.pointer(ev);
    const n = nodeAtCanvas(cx,cy);
    if (n) showCtxMenu(n.id, ev.clientX, ev.clientY);
    else hideCtxMenu();
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
  if (currentView === 'hierarchy') {
    if (hierInitDone) { layoutHierarchy(); renderHierarchy(); }
    return;
  }
  ctxFilterActive = false; // normal toolbar filter overrides any ctx filter
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
                   Method:640, Property:640, Field:640, ExternalType:720};

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

// ── View mode ─────────────────────────────────────────────────────────────────
let currentView = 'force';
function switchView(mode) {
  if (currentView === mode) return;
  currentView = mode;
  const gc = document.getElementById('gc');
  const loadOv = document.getElementById('loading-overlay');
  const hierView = document.getElementById('hier-view');
  const showForce = mode === 'force';
  gc.style.display = showForce ? '' : 'none';
  if (!showForce) loadOv.style.display = 'none';
  else if (simRunning) loadOv.classList.remove('hidden');
  hierView.style.display = showForce ? 'none' : 'block';
  document.querySelectorAll('.force-only').forEach(el => el.style.display = showForce ? '' : 'none');
  document.querySelectorAll('.hier-only').forEach(el => el.style.display = showForce ? 'none' : '');
  if (!showForce) {
    if (!hierInitDone) { initHierarchy(); hierInitDone = true; }
    layoutHierarchy();
    renderHierarchy();
  } else {
    markDirty();
  }
}

// ── Hierarchy constants ───────────────────────────────────────────────────────
const HIER_H  = 30;   // header height
const HIER_MW = 180;  // min box width
const HIER_P  = 10;   // padding inside container
const HIER_G  = 5;    // gap between siblings
const HIER_RG = 20;   // gap between roots
const HIER_KIND_ORDER = ['Solution','Project','Namespace','Class','Interface','Struct','Enum',
                          'Method','Property','NuGetPackage','ExternalType'];
const HIER_OPEN_KINDS  = new Set(['Solution','Project']);
const HIER_STRUCTURAL  = new Set(['Solution','Project','Namespace','Class','Interface',
                                   'Struct','Enum','NuGetPackage','ExternalType']);
let hierInitDone = false;
let hierTree   = {};  // id→{id,children[],parent}
let hierState  = {};  // id→{collapsed:bool}
let hierLayout = {};  // id→{x,y,w,h}
let hierRoots  = [];
let showHierExt = false;
let hierZoom = null, hierTransform = d3.zoomIdentity;

// ── Hierarchy: build tree ─────────────────────────────────────────────────────
function initHierarchy() {
  const childMap = {}, parentMap = {};
  RAW_EDGES.forEach(e => {
    if (e.kind !== 'Contains') return;
    const s = e.source?.id ?? e.source, t = e.target?.id ?? e.target;
    if (!childMap[s]) childMap[s] = [];
    childMap[s].push(t);
    parentMap[t] = s;
  });
  hierTree = {};
  RAW_NODES.forEach(n => {
    hierTree[n.id] = { id: n.id, children: (childMap[n.id]||[]).slice(), parent: parentMap[n.id]||null };
  });
  const kindRank = k => { const i=HIER_KIND_ORDER.indexOf(k); return i<0?99:i; };
  Object.values(hierTree).forEach(hn => {
    hn.children.sort((a,b) => {
      const na=nodeById[a], nb=nodeById[b];
      const d=kindRank(na?.kind)-kindRank(nb?.kind);
      return d!==0?d:(na?.label||'').localeCompare(nb?.label||'');
    });
  });
  hierRoots = RAW_NODES
    .filter(n => !parentMap[n.id] && (HIER_STRUCTURAL.has(n.kind)||(childMap[n.id]||[]).length>0))
    .map(n => n.id);
  hierRoots.sort((a,b) => {
    const na=nodeById[a], nb=nodeById[b];
    const d=kindRank(na?.kind)-kindRank(nb?.kind);
    return d!==0?d:(na?.label||'').localeCompare(nb?.label||'');
  });
  hierState = {};
  RAW_NODES.forEach(n => { hierState[n.id]={collapsed:!HIER_OPEN_KINDS.has(n.kind)}; });
}

// ── Hierarchy: layout ─────────────────────────────────────────────────────────
function hierVisKids(id) {
  return (hierTree[id]?.children||[]).filter(cid => {
    const cn=nodeById[cid];
    return cn && (showHierExt || cn.meta?.isExternal!=='true');
  });
}

function hierSize(id) {
  const n=nodeById[id];
  const label=n?.label||id;
  const baseW=Math.max(HIER_MW, Math.min(label.length*7+56, 320));
  const kids=hierVisKids(id);
  if ((hierState[id]?.collapsed??true)||kids.length===0) return {w:baseW,h:HIER_H};
  const cs=kids.map(cid=>hierSize(cid));
  const maxCW=cs.reduce((m,s)=>Math.max(m,s.w),0);
  const totCH=cs.reduce((s,c)=>s+c.h,0)+Math.max(0,kids.length-1)*HIER_G;
  return {w:Math.max(baseW,maxCW+HIER_P*2), h:HIER_H+HIER_P+totCH+HIER_P};
}

function hierPos(id,x,y,w) {
  const lay={x,y,w,h:HIER_H}; hierLayout[id]=lay;
  const kids=hierVisKids(id);
  if ((hierState[id]?.collapsed??true)||kids.length===0) return lay;
  const cw=Math.max(w-HIER_P*2,HIER_MW);
  let cy=y+HIER_H+HIER_P;
  kids.forEach(cid=>{ const cl=hierPos(cid,x+HIER_P,cy,cw); cy+=cl.h+HIER_G; });
  lay.h=cy-y-HIER_G+HIER_P; return lay;
}

function layoutHierarchy() {
  hierLayout={};
  showHierExt=document.getElementById('ext-toggle').checked;
  let x=HIER_P;
  const visRoots=hierRoots.filter(id=>showHierExt||nodeById[id]?.meta?.isExternal!=='true');
  visRoots.forEach(id=>{ const sz=hierSize(id); hierPos(id,x,HIER_P,sz.w); x+=sz.w+HIER_RG; });
}

// ── Hierarchy: visible ancestor ───────────────────────────────────────────────
function hierVisAnc(id) {
  let shallowest=null, cur=hierTree[id]?.parent;
  while (cur) { if (hierState[cur]?.collapsed) shallowest=cur; cur=hierTree[cur]?.parent; }
  return shallowest??id;
}

// ── Hierarchy: render ─────────────────────────────────────────────────────────
const SVGNS='http://www.w3.org/2000/svg';
function hEl(tag,attrs,txt) {
  const el=document.createElementNS(SVGNS,tag);
  if (attrs) Object.entries(attrs).forEach(([k,v])=>el.setAttribute(k,String(v)));
  if (txt!=null) el.textContent=txt;
  return el;
}

function renderHierarchy() {
  const svg=document.getElementById('hier-svg');
  if (!svg.querySelector('defs')) {
    const defs=hEl('defs',{});
    defs.innerHTML=`<marker id="ha" markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto">`+
      `<path d="M0,0 L0,7 L7,3.5Z" fill="#475569" fill-opacity="0.8"/></marker>`;
    svg.appendChild(defs);
  }
  ['hier-g-edges','hier-g-boxes'].forEach(id=>{ const e=document.getElementById(id); if(e)e.remove(); });
  const gEdges=hEl('g',{id:'hier-g-edges'}), gBoxes=hEl('g',{id:'hier-g-boxes'});
  svg.appendChild(gEdges); svg.appendChild(gBoxes);
  const tf=hierTransform.toString();
  gEdges.setAttribute('transform',tf); gBoxes.setAttribute('transform',tf);

  // Collect rendered ids
  const rendered=new Set();
  function collectRendered(id) {
    if (!hierLayout[id]) return;
    rendered.add(id);
    if (!(hierState[id]?.collapsed??true)) hierVisKids(id).forEach(cid=>collectRendered(cid));
  }
  hierRoots.forEach(id=>collectRendered(id));

  // Draw boxes recursively
  function drawBox(id) {
    const lay=hierLayout[id]; if(!lay) return;
    const n=nodeById[id];
    const color=COLOR[n?.kind]||'#64748b';
    const kids=hierVisKids(id);
    const hasKids=kids.length>0;
    const collapsed=hierState[id]?.collapsed??true;
    const g=hEl('g',{class:'h-box'}); g.dataset.id=id;
    // Container fill (expanded)
    if (!collapsed&&hasKids) {
      g.appendChild(hEl('rect',{x:lay.x,y:lay.y,width:lay.w,height:lay.h,rx:8,
        fill:color+'0e',stroke:color+'33','stroke-width':'1'}));
    }
    // Header
    g.appendChild(hEl('rect',{class:'h-header-rect',
      x:lay.x,y:lay.y,width:lay.w,height:HIER_H,
      rx:(!collapsed&&hasKids)?0:7,
      fill:color+'26',stroke:color+'99','stroke-width':'1'}));
    // Top-rounded cap redrawn over the opened header to fix corners
    if (!collapsed&&hasKids) {
      g.appendChild(hEl('path',{
        d:`M${lay.x+7},${lay.y} Q${lay.x},${lay.y} ${lay.x},${lay.y+7}`+
          ` L${lay.x},${lay.y+HIER_H} L${lay.x+lay.w},${lay.y+HIER_H}`+
          ` L${lay.x+lay.w},${lay.y+7} Q${lay.x+lay.w},${lay.y} ${lay.x+lay.w-7},${lay.y} Z`,
        fill:color+'26',stroke:color+'99','stroke-width':'1','pointer-events':'none'}));
    }
    // Selection ring
    if (id===selectedId) {
      g.appendChild(hEl('rect',{x:lay.x-2,y:lay.y-2,width:lay.w+4,height:HIER_H+4,
        rx:9,fill:'none',stroke:'#3b82f6','stroke-width':'2',style:'pointer-events:none'}));
    }
    // Entry-point glow
    if (n?.isEntryPoint) {
      g.appendChild(hEl('rect',{x:lay.x-1,y:lay.y-1,width:lay.w+2,height:HIER_H+2,
        rx:8,fill:'none',stroke:'#f59e0b','stroke-width':'1.5',style:'pointer-events:none'}));
    }
    // Toggle chevron
    if (hasKids) {
      g.appendChild(hEl('text',{x:lay.x+11,y:lay.y+HIER_H/2,'dominant-baseline':'central',
        'font-size':'9',fill:color,'font-family':'monospace',style:'pointer-events:none'},
        collapsed?'▶':'▼'));
    }
    // Label
    g.appendChild(hEl('text',{x:lay.x+(hasKids?26:12),y:lay.y+HIER_H/2,
      'dominant-baseline':'central','font-size':'12',
      'font-family':"'Segoe UI',system-ui,sans-serif",
      fill:n?.isEntryPoint?'#fcd34d':'#e2e8f0',
      'font-weight':n?.isEntryPoint?'700':'400',
      style:'pointer-events:none'},n?.label||id));
    // Kind badge
    g.appendChild(hEl('text',{x:lay.x+lay.w-7,y:lay.y+HIER_H/2,
      'dominant-baseline':'central','text-anchor':'end','font-size':'9',
      'font-family':"'Segoe UI',system-ui,sans-serif",fill:color+'99',
      style:'pointer-events:none'},n?.kind||''));
    g.style.cursor='pointer';
    if (hlMode && !hlNodeIds.has(id)) g.style.opacity = '0.15';
    g.addEventListener('click',ev=>{
      ev.stopPropagation();
      hideCtxMenu();
      if (hasKids) { hierState[id].collapsed=!hierState[id].collapsed; layoutHierarchy(); renderHierarchy(); }
      else { selectNode(id); renderHierarchy(); }
    });
    g.addEventListener('contextmenu',ev=>{
      ev.preventDefault(); ev.stopPropagation();
      showCtxMenu(id, ev.clientX, ev.clientY);
    });
    gBoxes.appendChild(g);
    if (!collapsed) kids.forEach(cid=>drawBox(cid));
  }
  hierRoots.forEach(id=>drawBox(id));

  // Draw non-Contains edges (terminated at visible ancestors)
  const drawn=new Set();
  RAW_EDGES.forEach(e=>{
    if (e.kind==='Contains') return;
    const src=hierVisAnc(e.source?.id??e.source);
    const tgt=hierVisAnc(e.target?.id??e.target);
    if (src===tgt||!rendered.has(src)||!rendered.has(tgt)) return;
    const key=src+'\u21d2'+tgt;
    if (drawn.has(key)) return;
    drawn.add(key);
    const sL=hierLayout[src], tL=hierLayout[tgt];
    if (!sL||!tL) return;
    const ec=EDGE_COLOR[e.kind]||'#475569';
    const goRight=(sL.x+sL.w/2)<(tL.x+tL.w/2);
    const x1=goRight?sL.x+sL.w:sL.x, y1=sL.y+HIER_H/2;
    const x2=goRight?tL.x:tL.x+tL.w, y2=tL.y+HIER_H/2;
    const dx=Math.max(Math.abs(x2-x1)*0.45,30);
    const cx1=goRight?x1+dx:x1-dx, cx2=goRight?x2-dx:x2+dx;
    const eKey=(e.source?.id??e.source)+'→'+(e.target?.id??e.target);
    const edgeOpacity = hlMode ? (hlEdgeKeys.has(eKey) ? '0.9' : '0.05') : '0.45';
    const edgeWidth = hlMode && hlEdgeKeys.has(eKey) ? '2.2' : '1.2';
    gEdges.appendChild(hEl('path',{
      d:`M${x1},${y1} C${cx1},${y1} ${cx2},${y2} ${x2},${y2}`,
      stroke:ec,'stroke-width':edgeWidth,fill:'none',
      'stroke-opacity':edgeOpacity,'marker-end':'url(#ha)'}));
  });

  // Attach zoom (once)
  if (!hierZoom) {
    hierZoom=d3.zoom().scaleExtent([0.03,8]).on('zoom',({transform})=>{
      hierTransform=transform;
      document.getElementById('hier-g-edges')?.setAttribute('transform',transform);
      document.getElementById('hier-g-boxes')?.setAttribute('transform',transform);
    });
    d3.select('#hier-svg').call(hierZoom).on('dblclick.zoom',null)
      .on('contextmenu.hier', ev => { ev.preventDefault(); hideCtxMenu(); })
      .on('click.hier-clear', ev => {
        // Clicks on boxes are stopped before they reach SVG, so anything
        // that reaches here is a click on empty SVG canvas.
        hideCtxMenu(); clearHighlight();
      });
  }
}

function resetHierZoom() {
  if (hierZoom) d3.select('#hier-svg').transition().duration(500).call(hierZoom.transform,d3.zoomIdentity);
}

function hierExpandAll(depth) {
  function walk(id,d) {
    hierState[id]={collapsed:d>=depth};
    if (d<depth) hierVisKids(id).forEach(cid=>walk(cid,d+1));
  }
  hierRoots.forEach(id=>walk(id,0));
  layoutHierarchy(); renderHierarchy();
}

function hierCollapseAll() {
  Object.values(hierState).forEach(s=>s.collapsed=true);
  hierRoots.forEach(id=>{ if(hierState[id]) hierState[id].collapsed=false; });
  layoutHierarchy(); renderHierarchy();
}

// ── Helpers ───────────────────────────────────────────────────────────────────
function escHtml(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
// Encode all HTML-special chars so the value is safe inside a data-* attribute.
function escAttr(s) { return escHtml(String(s)); }
function truncate(s,n) { return s.length>n ? s.slice(0,n)+'…' : s; }

// ── Context menu ──────────────────────────────────────────────────────────────
function showCtxMenu(nodeId, clientX, clientY) {
  ctxMenuNodeId = nodeId;
  const n = nodeById[nodeId];
  if (!n) return;
  const menu = document.getElementById('ctx-menu');
  document.getElementById('ctx-node-label').textContent = n.label + ' · ' + n.kind;
  const kindLbl = document.getElementById('ctx-kind-label');
  if (kindLbl) kindLbl.textContent = n.kind;

  menu.classList.add('visible');
  // Initial position
  menu.style.left = clientX + 'px';
  menu.style.top = clientY + 'px';
  // Adjust if overflowing viewport
  const rect = menu.getBoundingClientRect();
  if (rect.right > window.innerWidth - 8)  menu.style.left = Math.max(4, window.innerWidth - rect.width - 8) + 'px';
  if (rect.bottom > window.innerHeight - 8) menu.style.top = Math.max(4, window.innerHeight - rect.height - 8) + 'px';
}

function hideCtxMenu() {
  document.getElementById('ctx-menu').classList.remove('visible');
}

function clearHighlight() {
  hlMode = false;
  hlNodeIds = new Set();
  hlEdgeKeys = new Set();
  if (currentView === 'hierarchy') renderHierarchy();
  else markDirty();
}

function ctxHighlightRefs() {
  if (!ctxMenuNodeId) return;
  hideCtxMenu();
  hlMode = true;
  hlNodeIds = new Set([ctxMenuNodeId]);
  hlEdgeKeys = new Set();
  // Collect all incident edges from lookup maps (keys are original string IDs)
  const outEdges = edgesFrom[ctxMenuNodeId] || [];
  const inEdges  = edgesTo[ctxMenuNodeId]   || [];
  for (const e of outEdges) {
    const src = e.source?.id ?? e.source, tgt = e.target?.id ?? e.target;
    hlNodeIds.add(tgt); hlEdgeKeys.add(src + '→' + tgt);
  }
  for (const e of inEdges) {
    const src = e.source?.id ?? e.source, tgt = e.target?.id ?? e.target;
    hlNodeIds.add(src); hlEdgeKeys.add(src + '→' + tgt);
  }
  if (currentView === 'hierarchy') renderHierarchy();
  else markDirty();
}

// ── Filter-To actions ─────────────────────────────────────────────────────────
function ctxFilterNode() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  if (currentView === 'hierarchy') {
    ctxHierSubtree(); return;
  }
  const n = nodeById[ctxMenuNodeId];
  if (!n) return;
  nodes = [n]; edges = []; rebuild();
}

function ctxFilterNeighbours() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  if (currentView === 'hierarchy') { ctxHierExpandPath(); return; }
  // All arrows to/from this node — same as Expand All but non-additive (replaces)
  const ids = new Set([ctxMenuNodeId]);
  (edgesTo[ctxMenuNodeId]   || []).forEach(e => ids.add(e.source?.id ?? e.source));
  (edgesFrom[ctxMenuNodeId] || []).forEach(e => ids.add(e.target?.id ?? e.target));
  ctxApplyNodeFilter(ids, false);
}

function ctxFilterKind() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  const n = nodeById[ctxMenuNodeId];
  if (!n) return;
  document.getElementById('kind-filter').value = n.kind;
  applyFilter();
}

function ctxHierSubtree() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  // Collapse everything, then expand only ctxMenuNodeId and its ancestors
  Object.values(hierState).forEach(s => s.collapsed = true);
  hierRoots.forEach(id => { if (hierState[id]) hierState[id].collapsed = false; });
  // Expand ancestors
  let cur = hierTree[ctxMenuNodeId];
  while (cur && cur.parent) {
    if (hierState[cur.parent]) hierState[cur.parent].collapsed = false;
    cur = hierTree[cur.parent];
  }
  // Expand the target itself
  if (hierState[ctxMenuNodeId]) hierState[ctxMenuNodeId].collapsed = false;
  layoutHierarchy(); renderHierarchy();
}

function ctxHierExpandPath() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  // Expand ancestor chain so the node is visible
  let cur = hierTree[ctxMenuNodeId];
  while (cur && cur.parent) {
    if (hierState[cur.parent]) hierState[cur.parent].collapsed = false;
    cur = hierTree[cur.parent];
  }
  layoutHierarchy(); renderHierarchy();
}

// ── Expand-To actions ────────────────────────────────────────────────────────
// Helper: collect all IDs reachable via outgoing Contains edges (full member subtree).
function _containedSubtree(startId) {
  const ids = new Set([startId]);
  const queue = [startId];
  while (queue.length) {
    const id = queue.shift();
    (edgesFrom[id] || []).forEach(e => {
      if (e.kind === 'Contains') {
        const cid = e.target?.id ?? e.target;
        if (!ids.has(cid)) { ids.add(cid); queue.push(cid); }
      }
    });
  }
  return ids;
}

// Simple directional: exactly the edges whose arrows you see on screen.
function ctxExpandIncoming() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  const ids = new Set([ctxMenuNodeId]);
  (edgesTo[ctxMenuNodeId] || []).forEach(e => ids.add(e.source?.id ?? e.source));
  ctxApplyNodeFilter(ids);
}

function ctxExpandOutgoing() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  const ids = new Set([ctxMenuNodeId]);
  (edgesFrom[ctxMenuNodeId] || []).forEach(e => ids.add(e.target?.id ?? e.target));
  ctxApplyNodeFilter(ids);
}

function ctxExpandAll() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  const ids = new Set([ctxMenuNodeId]);
  (edgesTo[ctxMenuNodeId]   || []).forEach(e => ids.add(e.source?.id ?? e.source));
  (edgesFrom[ctxMenuNodeId] || []).forEach(e => ids.add(e.target?.id ?? e.target));
  ctxApplyNodeFilter(ids);
}

// Functional: walks into contained members, surfaces external Calls/Accesses/
// Inherits/Implements connections. Useful for class/namespace-level navigation
// where the user wants "what does this type interact with" rather than raw arrows.
const FUNC_KINDS = new Set(['Calls','Accesses','Inherits','Implements']);

function ctxExpandFunctional() {
  if (!ctxMenuNodeId) return; hideCtxMenu();
  const members = _containedSubtree(ctxMenuNodeId);
  const ids = new Set([ctxMenuNodeId]);
  members.forEach(mid => {
    (edgesFrom[mid] || []).forEach(e => {
      if (!FUNC_KINDS.has(e.kind)) return;
      const tgt = e.target?.id ?? e.target;
      if (!members.has(tgt)) { ids.add(mid); ids.add(tgt); }
    });
    (edgesTo[mid] || []).forEach(e => {
      if (!FUNC_KINDS.has(e.kind)) return;
      const src = e.source?.id ?? e.source;
      if (!members.has(src)) { ids.add(mid); ids.add(src); }
    });
  });
  ctxApplyNodeFilter(ids);
}

// Zoom canvas to fit a list of positioned nodes.
function showToast(msg, durationMs) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.style.display = 'block';
  clearTimeout(showToast._t);
  showToast._t = setTimeout(() => { el.style.display = 'none'; }, durationMs ?? 2800);
}

function zoomFitNodes(nodeList, padding) {
  if (!nodeList.length) return;
  const positioned = nodeList.filter(n => n.x !== undefined && !isNaN(n.x));
  if (!positioned.length) return;
  let x0=Infinity,y0=Infinity,x1=-Infinity,y1=-Infinity;
  for (const n of positioned) {
    const r = RADIUS[n.kind] || 8;
    if (n.x-r < x0) x0=n.x-r; if (n.x+r > x1) x1=n.x+r;
    if (n.y-r < y0) y0=n.y-r; if (n.y+r > y1) y1=n.y+r;
  }
  const W = canvas.clientWidth, H = canvas.clientHeight;
  const pad = padding ?? 60;
  const k = Math.min((W-pad*2)/(x1-x0||1), (H-pad*2)/(y1-y0||1), 4);
  const mx = (x0+x1)/2, my = (y0+y1)/2;
  d3.select(canvas).transition().duration(400)
    .call(zoom.transform, d3.zoomIdentity.translate(W/2-k*mx, H/2-k*my).scale(k));
}

// Apply a set of node IDs — ADDITIVE: unions with whatever is currently visible.
function ctxApplyNodeFilter(nodeIds, additive = true) {
  if (currentView === 'hierarchy') {
    // Collapse everything first, then expand only the paths to the result nodes
    Object.values(hierState).forEach(s => s.collapsed = true);
    hierRoots.forEach(id => { if (hierState[id]) hierState[id].collapsed = false; });
    nodeIds.forEach(id => {
      if (hierState[id]) hierState[id].collapsed = false;
      let cur = hierTree[id];
      while (cur && cur.parent) {
        if (hierState[cur.parent]) hierState[cur.parent].collapsed = false;
        cur = hierTree[cur.parent];
      }
    });
    layoutHierarchy(); renderHierarchy();
    return;
  }
  ctxFilterActive = true;
  // Additive: union with current nodes; or replace if additive=false
  const alreadyVisible = new Set(nodes.map(n => n.id));
  const allIds = additive ? new Set([...alreadyVisible, ...nodeIds]) : new Set(nodeIds);
  // Check whether anything new is being added
  const addedCount = [...nodeIds].filter(id => !alreadyVisible.has(id)).length;
  if (additive && addedCount === 0) {
    showToast('No new nodes to add — all already visible.');
    ctxFilterActive = nodes.length < RAW_NODES.length;
    return;
  }
  const nodeIndex = {};
  nodes = RAW_NODES.filter(n => allIds.has(n.id));
  nodes.forEach(n => nodeIndex[n.id] = n);
  edges = RAW_EDGES
    .filter(e => {
      const src = e.source?.id ?? e.source, tgt = e.target?.id ?? e.target;
      return allIds.has(src) && allIds.has(tgt);
    })
    .map(e => ({
      ...e,
      source: nodeIndex[e.source?.id ?? e.source],
      target: nodeIndex[e.target?.id ?? e.target]
    }));
  rebuild();
  // Fit only the newly added nodes into view
  const newNodes = nodes.filter(n => !alreadyVisible.has(n.id));
  setTimeout(() => zoomFitNodes(newNodes.length > 0 ? newNodes : nodes), 250);
  setTimeout(() => zoomFitNodes(newNodes.length > 0 ? newNodes : nodes), 800);
}

// ── Context menu event wiring ─────────────────────────────────────────────────
(function bindCtxMenu() {
  document.getElementById('ctx-hl-item').addEventListener('click', ctxHighlightRefs);
  document.getElementById('ctx-clear-item').addEventListener('click', () => {
    hideCtxMenu(); clearHighlight();
    ctxFilterActive = false;
    document.getElementById('kind-filter').value = '';
    document.getElementById('search-input').value = '';
    applyFilter();
  });
  document.querySelectorAll('[data-action]').forEach(el => {
    el.addEventListener('click', ev => {
      ev.stopPropagation();
      const action = el.dataset.action;
      if (action === 'filter-node')        ctxFilterNode();
      else if (action === 'filter-neighbours') ctxFilterNeighbours();
      else if (action === 'filter-kind')    ctxFilterKind();
      else if (action === 'hier-subtree')   ctxHierSubtree();
      else if (action === 'hier-expand-path') ctxHierExpandPath();
      else if (action === 'expand-incoming')   ctxExpandIncoming();
      else if (action === 'expand-outgoing')   ctxExpandOutgoing();
      else if (action === 'expand-all')        ctxExpandAll();
      else if (action === 'expand-functional') ctxExpandFunctional();
    });
  });
  // Close menu when clicking anywhere outside it
  document.addEventListener('click', ev => {
    if (!document.getElementById('ctx-menu').contains(ev.target)) hideCtxMenu();
  });
  // Close menu on Escape
  document.getElementById('ctx-menu').addEventListener('keydown', ev => {
    if (ev.key === 'Escape') hideCtxMenu();
  });
})();

// ── Keyboard ──────────────────────────────────────────────────────────────────
document.addEventListener('keydown', ev => {
  if (ev.key==='f'||ev.key==='F') document.getElementById('search-input').focus();
  if (ev.key==='l'||ev.key==='L') toggleLabels();
  if (ev.key==='z'||ev.key==='Z') toggleFreeze();
  if (ev.key==='Enter' && selectedId) focusSelected();
  if (ev.key==='Escape') { hideCtxMenu(); clearHighlight(); selectedId=null; clearPanel(); markDirty(); }
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
