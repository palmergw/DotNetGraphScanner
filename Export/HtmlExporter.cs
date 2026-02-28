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
  <div id="toolbar">
    <button onclick="resetZoom()">⟳ Reset View</button>
    <button onclick="toggleLabels()">Labels</button>
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
    <input id="search-input" type="text" placeholder="🔍 Search nodes…" oninput="applyFilter()"/>
  </div>
  <div id="legend"><h4>Node Kinds</h4></div>
  <div id="perf"></div>
  <div id="edge-tip"></div>
  <div id="toast"></div>
</div>

<div id="panel">
  <div id="panel-header">
    <h1>SELECTED NODE</h1>
    <h2 id="panel-title">–</h2>
  </div>
  <div id="panel-body">
    <p id="no-selection">Click a node to inspect</p>
  </div>
</div>

<script>
const RAW_NODES = {{nodesJson}};
const RAW_EDGES = {{edgesJson}};

// ── Color / size tables ───────────────────────────────────────────────────────
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
  Solution:18, Project:16, Namespace:12,
  Class:11, Interface:11, Struct:10, Enum:10,
  Method:7, Property:6, NuGetPackage:13, ExternalType:7,
};

// ── State ─────────────────────────────────────────────────────────────────────
let labelsVisible = true;
let selectedId = null;
let hoveredId = null;
let simulation = null;
let forceManyBodyStrength = -250;
let transform = d3.zoomIdentity;
let dirty = true;
let rafId = null;
let quadtree = null;

// ── Lookup maps ───────────────────────────────────────────────────────────────
const nodeById = Object.fromEntries(RAW_NODES.map(n => [n.id, n]));
const edgesFrom = {}, edgesTo = {};
RAW_EDGES.forEach(e => {
  (edgesFrom[e.source] = edgesFrom[e.source] || []).push(e);
  (edgesTo[e.target]   = edgesTo[e.target]   || []).push(e);
});

// ── Canvas setup ──────────────────────────────────────────────────────────────
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

// ── Zoom (D3 applied to canvas element) ───────────────────────────────────────
const zoom = d3.zoom()
  .scaleExtent([0.02, 10])
  .on('zoom', ev => { transform = ev.transform; markDirty(); });
d3.select(canvas).call(zoom);

// ── RAF render loop ───────────────────────────────────────────────────────────
function markDirty() {
  dirty = true;
  if (!rafId) rafId = requestAnimationFrame(drawFrame);
}

let lastFrameTime = 0, fps = 0, frameCount = 0;
const perfEl = document.getElementById('perf');

function drawFrame(ts) {
  rafId = null;
  if (!dirty) return;
  dirty = false;

  // FPS counter (update every 30 frames)
  frameCount++;
  if (frameCount % 30 === 0) {
    fps = Math.round(30000 / (ts - lastFrameTime));
    lastFrameTime = ts;
    perfEl.textContent = `${fps} fps · ${nodes.length} nodes · ${edges.length} edges`;
  }

  const W = canvas.width, H = canvas.height;
  const s = transform.k, tx = transform.x * dpr, ty = transform.y * dpr;

  // ── Background ─────────────────────────────────────────────────────────────
  ctx.fillStyle = '#0f1117';
  ctx.fillRect(0, 0, W, H);

  ctx.save();
  ctx.translate(tx, ty);
  ctx.scale(s * dpr, s * dpr);

  // Viewport bounds in world space (for culling)
  const vx0 = -tx / (s * dpr), vy0 = -ty / (s * dpr);
  const vx1 = vx0 + W / (s * dpr), vy1 = vy0 + H / (s * dpr);
  const pad = 60;

  const showArrows = s > 0.3;
  const showLabels = labelsVisible && s > 0.45;

  // ── Edges (batched by color) ───────────────────────────────────────────────
  ctx.globalAlpha = 0.4;
  ctx.lineWidth = 1 / s;

  // Collect visible edges per color
  const edgeBuckets = {};
  const arrowBuckets = {};
  for (const e of edges) {
    const src = e.source, tgt = e.target;
    if (typeof src !== 'object' || !src.x) continue;
    const minX = Math.min(src.x, tgt.x), maxX = Math.max(src.x, tgt.x);
    const minY = Math.min(src.y, tgt.y), maxY = Math.max(src.y, tgt.y);
    if (maxX < vx0 - pad || minX > vx1 + pad || maxY < vy0 - pad || minY > vy1 + pad) continue;
    const col = EDGE_COLOR[e.kind] || '#475569';
    (edgeBuckets[col] = edgeBuckets[col] || []).push(e);
    if (showArrows) (arrowBuckets[col] = arrowBuckets[col] || []).push(e);
  }

  // Draw edge lines in batches
  for (const [col, bucket] of Object.entries(edgeBuckets)) {
    ctx.strokeStyle = col;
    ctx.beginPath();
    for (const e of bucket) {
      ctx.moveTo(e.source.x, e.source.y);
      ctx.lineTo(e.target.x, e.target.y);
    }
    ctx.stroke();
  }

  // Draw arrowheads (filled triangles) in batches
  if (showArrows) {
    ctx.globalAlpha = 0.65;
    const HL = 9 / s, HW = 4 / s; // head length / half-width
    for (const [col, bucket] of Object.entries(arrowBuckets)) {
      ctx.fillStyle = col;
      ctx.beginPath();
      for (const e of bucket) {
        const sx = e.source.x, sy = e.source.y;
        const tx2 = e.target.x, ty2 = e.target.y;
        const dx = tx2 - sx, dy = ty2 - sy;
        const len = Math.sqrt(dx * dx + dy * dy);
        if (len < 1) continue;
        const r = (RADIUS[e.target.kind] || 8) + 2;
        const t2 = Math.max(0, len - r) / len;
        const ex = sx + dx * t2, ey = sy + dy * t2; // tip
        const bx = ex - dx / len * HL, by = ey - dy / len * HL; // base
        const px = -dy / len * HW, py = dx / len * HW; // perpendicular
        ctx.moveTo(ex, ey);
        ctx.lineTo(bx + px, by + py);
        ctx.lineTo(bx - px, by - py);
        ctx.closePath();
      }
      ctx.fill();
    }
  }

  ctx.globalAlpha = 1;

  // ── Nodes (batched by color, special cases drawn separately) ──────────────
  ctx.lineWidth = 1.5 / s;

  // Separate nodes needing special stroke from plain ones
  const plainBuckets = {};
  const specialNodes = [];

  for (const n of nodes) {
    if (!n.x) continue;
    if (n.x < vx0 - pad || n.x > vx1 + pad || n.y < vy0 - pad || n.y > vy1 + pad) continue;
    if (n.id === selectedId || n.id === hoveredId || n.isEntryPoint) {
      specialNodes.push(n);
    } else {
      const col = COLOR[n.kind] || '#64748b';
      (plainBuckets[col] = plainBuckets[col] || []).push(n);
    }
  }

  // Plain nodes batched by fill color
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

  // Special nodes drawn individually
  for (const n of specialNodes) {
    const r = RADIUS[n.kind] || 8;
    const isSelected = n.id === selectedId;
    const isHovered  = n.id === hoveredId;

    ctx.fillStyle = COLOR[n.kind] || '#64748b';
    ctx.strokeStyle = isSelected ? '#ffffff' : (n.isEntryPoint ? '#f59e0b' : '#0f1117');
    ctx.lineWidth = (isSelected || isHovered ? 3 : 2.5) / s;

    // Glow ring for selected
    if (isSelected || isHovered) {
      ctx.shadowColor = isSelected ? '#ffffff88' : '#94a3b888';
      ctx.shadowBlur = 12 / s;
    }

    ctx.beginPath();
    ctx.arc(n.x, n.y, r, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    ctx.shadowBlur = 0;
    ctx.lineWidth = 1.5 / s;
  }

  // ── Labels (only when zoomed in enough) ───────────────────────────────────
  if (showLabels) {
    const fontSize = Math.min(12, 11 / s);
    ctx.font = `${fontSize}px "Segoe UI", system-ui, sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.fillStyle = '#94a3b8';

    for (const n of nodes) {
      if (!n.x) continue;
      if (n.x < vx0 - 80 || n.x > vx1 + 80 || n.y < vy0 - 40 || n.y > vy1 + 40) continue;
      const r = RADIUS[n.kind] || 8;
      if (n.id === selectedId) ctx.fillStyle = '#f1f5f9';
      else if (n.isEntryPoint) ctx.fillStyle = '#fcd34d';
      else ctx.fillStyle = '#94a3b8';
      ctx.fillText(truncate(n.label, 24), n.x, n.y + r + 2.5 / s);
    }
  }

  ctx.restore();

  // If simulation still running, keep rendering
  if (simulation && simulation.alpha() > simulation.alphaMin()) markDirty();
}

// ── Quadtree hit-test ─────────────────────────────────────────────────────────
function rebuildQuadtree() {
  if (nodes.length) quadtree = d3.quadtree(nodes, n => n.x, n => n.y);
}

function nodeAtCanvas(cx, cy) {
  if (!quadtree) return null;
  const wx = (cx - transform.x) / transform.k;
  const wy = (cy - transform.y) / transform.k;
  const radius = 20 / transform.k;
  const found = quadtree.find(wx, wy, radius);
  if (!found) return null;
  const r = (RADIUS[found.kind] || 8) + 4 / transform.k;
  return (Math.hypot(found.x - wx, found.y - wy) <= r) ? found : null;
}

function edgeAtCanvas(cx, cy) {
  const wx = (cx - transform.x) / transform.k;
  const wy = (cy - transform.y) / transform.k;
  const threshold = 6 / transform.k;
  let best = null, bestDist = threshold;
  for (const e of edges) {
    const s = e.source, t = e.target;
    if (typeof s !== 'object' || !s.x) continue;
    const dx = t.x - s.x, dy = t.y - s.y;
    const len2 = dx*dx + dy*dy;
    if (len2 === 0) continue;
    const u = Math.max(0, Math.min(1, ((wx-s.x)*dx + (wy-s.y)*dy) / len2));
    const px = s.x + u*dx - wx, py = s.y + u*dy - wy;
    const dist = Math.sqrt(px*px + py*py);
    if (dist < bestDist) { bestDist = dist; best = e; }
  }
  return best;
}

// ── Canvas pointer events ─────────────────────────────────────────────────────
let dragNode = null;

d3.select(canvas)
  .on('mousemove.graph', function(ev) {
    const [cx, cy] = d3.pointer(ev);
    const n = nodeAtCanvas(cx, cy);
    const prev = hoveredId;
    hoveredId = n ? n.id : null;
    canvas.style.cursor = n ? 'pointer' : 'grab';
    if (hoveredId !== prev) markDirty();
  })
  .on('click.graph', function(ev) {
    if (ev.defaultPrevented) return; // was a drag
    const [cx, cy] = d3.pointer(ev);
    const n = nodeAtCanvas(cx, cy);
    if (n) {
      selectNode(n.id);
    } else {
      const e = edgeAtCanvas(cx, cy);
      if (e) showEdgeTooltip(e, ev.clientX, ev.clientY);
      else {
        selectedId = null;
        clearPanel();
        markDirty();
      }
    }
  });

// Override D3 zoom drag to also support node dragging
(function setupNodeDrag() {
  const dragHandler = d3.drag()
    .filter(ev => {
      const [cx, cy] = d3.pointer(ev, canvas);
      return !!nodeAtCanvas(cx, cy);
    })
    .on('start', function(ev) {
      const [cx, cy] = d3.pointer(ev, canvas);
      dragNode = nodeAtCanvas(cx, cy);
      if (dragNode) {
        if (!ev.active) simulation.alphaTarget(0.15).restart();
        dragNode.fx = dragNode.x;
        dragNode.fy = dragNode.y;
        canvas.classList.add('dragging');
      }
    })
    .on('drag', function(ev) {
      if (!dragNode) return;
      dragNode.fx = (ev.x - transform.x) / transform.k;
      dragNode.fy = (ev.y - transform.y) / transform.k;
      markDirty();
    })
    .on('end', function(ev) {
      if (dragNode) {
        if (!ev.active) simulation.alphaTarget(0);
        dragNode.fx = null;
        dragNode.fy = null;
        dragNode = null;
        canvas.classList.remove('dragging');
      }
    });

  // Apply both zoom and drag — drag gets priority when pointer is over a node
  d3.select(canvas)
    .call(dragHandler)
    .call(zoom);
})();

// ── Populate kind filter & legend ─────────────────────────────────────────────
const kinds = [...new Set(RAW_NODES.map(n => n.kind))].sort();
const kf = document.getElementById('kind-filter');
kinds.forEach(k => { const o = document.createElement('option'); o.value = k; o.textContent = k; kf.appendChild(o); });

const legend = document.getElementById('legend');
kinds.forEach(k => {
  const item = document.createElement('div'); item.className = 'legend-item';
  item.innerHTML = `<div class="dot" style="background:${COLOR[k]||'#64748b'}"></div><span>${k}</span>`;
  legend.appendChild(item);
});
{
  const item = document.createElement('div'); item.className = 'legend-item';
  item.innerHTML = `<div class="dot entry" style="background:transparent"></div><span>Entry Point</span>`;
  legend.appendChild(item);
}

// ── Filter + rebuild ──────────────────────────────────────────────────────────
let nodes = [], edges = [];

function applyFilter() {
  const kindVal  = document.getElementById('kind-filter').value;
  const search   = document.getElementById('search-input').value.toLowerCase();
  const showExt  = document.getElementById('ext-toggle').checked;

  nodes = RAW_NODES.filter(n => {
    if (kindVal && n.kind !== kindVal) return false;
    if (!showExt && n.meta?.isExternal === 'true') return false;
    if (search && !n.label.toLowerCase().includes(search) &&
        !(n.meta?.fullName||'').toLowerCase().includes(search)) return false;
    return true;
  });
  const visIds = new Set(nodes.map(n => n.id));
  edges = RAW_EDGES.filter(e => visIds.has(e.source) && visIds.has(e.target));
  rebuild();
}

function rebuild() {
  // Preserve computed positions across filter changes
  nodes.forEach(n => {
    if (n._x !== undefined) { n.x = n._x; n.y = n._y; }
  });

  if (simulation) simulation.stop();

  // Tune alpha decay based on graph size for faster convergence on large graphs
  const nodeCount = nodes.length;
  const alphaDecay = nodeCount > 2000 ? 0.04
                   : nodeCount > 500  ? 0.028
                   : 0.02;
  const velocityDecay = nodeCount > 1000 ? 0.5 : 0.4;
  const linkIter = nodeCount > 1000 ? 1 : 3;

  simulation = d3.forceSimulation(nodes)
    .alphaDecay(alphaDecay)
    .velocityDecay(velocityDecay)
    .force('link', d3.forceLink(edges)
      .id(d => d.id)
      .distance(d => d.kind === 'Contains' ? 80 : d.kind === 'Calls' ? 55 : 100)
      .strength(0.4)
      .iterations(linkIter))
    .force('charge', d3.forceManyBody()
      .strength(forceManyBodyStrength)
      .theta(nodeCount > 1000 ? 0.9 : 0.8)
      .distanceMax(600))
    .force('center', d3.forceCenter(canvas.clientWidth / 2, canvas.clientHeight / 2).strength(0.05))
    .force('collision', d3.forceCollide().radius(d => (RADIUS[d.kind] || 8) + 2).strength(0.7))
    .on('tick', () => { rebuildQuadtree(); markDirty(); })
    .on('end',  () => { rebuildQuadtree(); markDirty(); });

  rebuildQuadtree();
  markDirty();
}

// ── Selection / panel ─────────────────────────────────────────────────────────
function selectNode(id) {
  selectedId = id;
  markDirty();
  const n = nodeById[id];
  if (!n) return;

  document.getElementById('panel-title').textContent = n.label;
  const body = document.getElementById('panel-body');

  const outgoing = (edgesFrom[id] || []).filter(e => e.kind !== 'Contains');
  const incoming = (edgesTo[id]   || []).filter(e => e.kind !== 'Contains');

  const entryTag  = n.isEntryPoint ? `<span class="tag" style="background:#78350f;color:#fcd34d">⚡ Entry Point</span>` : '';
  const col = COLOR[n.kind] || '#64748b';
  const kindTag   = `<span class="tag" style="background:${col}22;color:${col};border:1px solid ${col}55">${n.kind}</span>`;

  let html = `<div>${kindTag}${entryTag}</div><table>`;
  html += `<tr><td>ID</td><td style="word-break:break-all;font-size:11px;color:#64748b">${escHtml(id)}</td></tr>`;
  Object.entries(n.meta || {}).forEach(([k, v]) => {
    html += `<tr><td>${escHtml(k)}</td><td style="word-break:break-all">${escHtml(v)}</td></tr>`;
  });
  html += `</table>`;

  const renderNeighbours = (list, label, idKey) => {
    if (!list.length) return '';
    let h = `<div class="neighbours-section"><h3>${label} (${list.length})</h3>`;
    list.slice(0, 80).forEach(e => {
      const peer = nodeById[e[idKey]];
      if (!peer) return;
      const c2 = COLOR[peer.kind] || '#64748b';
      h += `<div class="neighbour-item" onclick="selectNode('${escAttr(e[idKey])}')"><div class="dot" style="background:${c2}"></div><span style="color:#64748b;font-size:11px;min-width:70px">${e.kind}</span><span>${escHtml(peer.label)}</span></div>`;
    });
    if (list.length > 80) h += `<div style="color:#64748b;font-size:11px;padding:4px 0">… and ${list.length - 80} more</div>`;
    h += `</div>`;
    return h;
  };

  html += renderNeighbours(outgoing, 'Calls / Dependencies', 'target');
  html += renderNeighbours(incoming, 'Called By', 'source');
  body.innerHTML = html;
}

function showEdgeTooltip(e, clientX, clientY) {
  const wrap = canvas.parentElement.getBoundingClientRect();
  const s = nodeById[e.source?.id || e.source];
  const t = nodeById[e.target?.id || e.target];
  const tip = document.getElementById('edge-tip');
  tip.textContent = `${s?.label||'?'} → [${e.kind}] → ${t?.label||'?'}${e.meta?.line ? ' @ line '+e.meta.line : ''}`;
  tip.style.left = (clientX - wrap.left + 10) + 'px';
  tip.style.top  = (clientY - wrap.top  + 10) + 'px';
  tip.style.display = 'block';
  clearTimeout(tip._tid);
  tip._tid = setTimeout(() => tip.style.display = 'none', 3000);
}

function clearPanel() {
  document.getElementById('panel-title').textContent = '–';
  document.getElementById('panel-body').innerHTML = '<p id="no-selection">Click a node to inspect</p>';
}

// ── Toolbar callbacks ─────────────────────────────────────────────────────────
function toggleLabels() { labelsVisible = !labelsVisible; markDirty(); }
function resetZoom() {
  d3.select(canvas).transition().duration(500)
    .call(zoom.transform, d3.zoomIdentity);
}
function updateForce(v) {
  forceManyBodyStrength = -v;
  if (simulation) {
    simulation.force('charge', d3.forceManyBody()
      .strength(forceManyBodyStrength)
      .theta(nodes.length > 1000 ? 0.9 : 0.8)
      .distanceMax(600));
    simulation.alpha(0.3).restart();
  }
}

// ── Panel sidebar CSS (still DOM, not canvas) ─────────────────────────────────
// (styles stay in the <style> block above)

// ── Helpers ───────────────────────────────────────────────────────────────────
function escHtml(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
function escAttr(s) { return String(s).replace(/'/g,"\\'"); }
function truncate(s, n) { return s.length > n ? s.slice(0, n) + '…' : s; }

// ── Keyboard shortcuts ────────────────────────────────────────────────────────
document.addEventListener('keydown', ev => {
  if (ev.key === 'f' || ev.key === 'F') document.getElementById('search-input').focus();
  if (ev.key === 'l' || ev.key === 'L') toggleLabels();
  if (ev.key === 'Escape') { selectedId = null; clearPanel(); markDirty(); }
});

// ── Init ─────────────────────────────────────────────────────────────────────
resizeCanvas();
applyFilter();
</script>
</body>
</html>
""";
}
