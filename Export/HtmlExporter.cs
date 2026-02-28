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
  #canvas { flex: 1; position: relative; }
  svg { width: 100%; height: 100%; }

  /* Nodes */
  circle { stroke-width: 1.5px; cursor: pointer; transition: r .15s; }
  circle:hover { stroke-width: 3px; }
  circle.entry { stroke: #f59e0b !important; stroke-width: 3px; }
  circle.selected { stroke: #ffffff !important; stroke-width: 3px; }
  text.node-label { font-size: 11px; fill: #cbd5e1; pointer-events: none; text-anchor: middle; dominant-baseline: middle; }

  /* Edges */
  line { stroke-opacity: 0.45; stroke-width: 1px; }
  line:hover { stroke-opacity: 1; }

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
<div id="canvas">
  <svg id="svg">
    <defs>
      <marker id="arrowhead" markerWidth="8" markerHeight="6" refX="14" refY="3" orient="auto">
        <polygon points="0 0, 8 3, 0 6" fill="#475569"/>
      </marker>
    </defs>
    <g id="zoom-layer">
      <g id="edges-layer"></g>
      <g id="nodes-layer"></g>
    </g>
  </svg>
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

const COLOR = {
  Solution:     '#6366f1',
  Project:      '#3b82f6',
  Namespace:    '#22d3ee',
  Class:        '#10b981',
  Interface:    '#f59e0b',
  Struct:       '#84cc16',
  Enum:         '#a78bfa',
  Method:       '#64748b',
  Property:     '#475569',
  NuGetPackage: '#f97316',
  ExternalType: '#374151',
};

const RADIUS = {
  Solution: 18, Project: 16, Namespace: 12,
  Class: 11, Interface: 11, Struct: 10, Enum: 10,
  Method: 7, Property: 6, NuGetPackage: 13, ExternalType: 7,
};

let labelsVisible = true;
let selectedId = null;
let simulation;
let forceManyBodyStrength = -250;

// ── Build lookup maps ────────────────────────────────────────────────────────
const nodeById = Object.fromEntries(RAW_NODES.map(n => [n.id, n]));
const edgesFrom = {};
const edgesTo   = {};
RAW_EDGES.forEach(e => {
  (edgesFrom[e.source] = edgesFrom[e.source] || []).push(e);
  (edgesTo[e.target]   = edgesTo[e.target]   || []).push(e);
});

// ── Populate kind filter ─────────────────────────────────────────────────────
const kinds = [...new Set(RAW_NODES.map(n => n.kind))].sort();
const kf = document.getElementById('kind-filter');
kinds.forEach(k => { const o = document.createElement('option'); o.value = k; o.textContent = k; kf.appendChild(o); });

// ── Legend ───────────────────────────────────────────────────────────────────
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

// ── D3 setup ─────────────────────────────────────────────────────────────────
const svg = d3.select('#svg');
const zoomLayer = svg.select('#zoom-layer');
const edgesLayer = zoomLayer.select('#edges-layer');
const nodesLayer = zoomLayer.select('#nodes-layer');

const zoom = d3.zoom().scaleExtent([0.05, 5]).on('zoom', e => zoomLayer.attr('transform', e.transform));
svg.call(zoom);

let nodes = [], edges = [];
let nodeElems, edgeElems, labelElems;

function applyFilter() {
  const kindVal = document.getElementById('kind-filter').value;
  const searchVal = document.getElementById('search-input').value.toLowerCase();
  const showExt = document.getElementById('ext-toggle').checked;

  nodes = RAW_NODES.filter(n => {
    if (kindVal && n.kind !== kindVal) return false;
    if (!showExt && n.meta && n.meta.isExternal === 'true') return false;
    if (searchVal && !n.label.toLowerCase().includes(searchVal) && !(n.meta?.fullName||'').toLowerCase().includes(searchVal)) return false;
    return true;
  });
  const visibleIds = new Set(nodes.map(n => n.id));
  edges = RAW_EDGES.filter(e => visibleIds.has(e.source) && visibleIds.has(e.target));
  rebuild();
}

function rebuild() {
  edgesLayer.selectAll('*').remove();
  nodesLayer.selectAll('*').remove();

  // Keep previous positions
  nodes.forEach(n => {
    if (n._x !== undefined) { n.fx = null; n.fy = null; n.x = n._x; n.y = n._y; }
  });

  edgeElems = edgesLayer.selectAll('line')
    .data(edges).enter().append('line')
    .attr('stroke', d => {
      const c = { Calls:'#475569', Contains:'#1e3a5f', Inherits:'#7c3aed', Implements:'#d97706',
                  ProjectReference:'#3b82f6', PackageReference:'#f97316', EntryPoint:'#f59e0b' };
      return c[d.kind] || '#475569';
    })
    .attr('marker-end', 'url(#arrowhead)')
    .on('click', (ev, d) => showEdgeInfo(d));

  const nodeG = nodesLayer.selectAll('g.node')
    .data(nodes, d => d.id).enter().append('g').attr('class', 'node')
    .call(d3.drag()
      .on('start', (ev, d) => { if (!ev.active) simulation.alphaTarget(0.3).restart(); d.fx = d.x; d.fy = d.y; })
      .on('drag',  (ev, d) => { d.fx = ev.x; d.fy = ev.y; })
      .on('end',   (ev, d) => { if (!ev.active) simulation.alphaTarget(0); d.fx = null; d.fy = null; }))
    .on('click', (ev, d) => { ev.stopPropagation(); selectNode(d.id); });

  nodeG.append('circle')
    .attr('r', d => RADIUS[d.kind] || 8)
    .attr('fill', d => COLOR[d.kind] || '#64748b')
    .attr('stroke', d => d.isEntryPoint ? '#f59e0b' : '#0f1117')
    .classed('entry', d => d.isEntryPoint);

  labelElems = nodeG.append('text').attr('class', 'node-label')
    .attr('dy', d => (RADIUS[d.kind] || 8) + 11)
    .text(d => truncate(d.label, 22))
    .style('display', labelsVisible ? 'block' : 'none');

  nodeElems = nodesLayer.selectAll('g.node');

  svg.on('click', () => { selectedId = null; clearPanel(); nodeElems.select('circle').classed('selected', false); });

  simulation = d3.forceSimulation(nodes)
    .force('link', d3.forceLink(edges).id(d => d.id).distance(d => {
      const k = d.kind;
      if (k === 'Contains') return 80;
      if (k === 'Calls') return 60;
      return 100;
    }).strength(0.5))
    .force('charge', d3.forceManyBody().strength(forceManyBodyStrength))
    .force('center', d3.forceCenter(svg.node().clientWidth / 2, svg.node().clientHeight / 2))
    .force('collision', d3.forceCollide().radius(d => (RADIUS[d.kind] || 8) + 4))
    .on('tick', ticked);
}

function ticked() {
  edgeElems
    .attr('x1', d => d.source.x).attr('y1', d => d.source.y)
    .attr('x2', d => d.target.x).attr('y2', d => d.target.y);
  nodeElems.attr('transform', d => `translate(${d.x},${d.y})`);
  nodes.forEach(n => { n._x = n.x; n._y = n.y; });
}

// ── Selection / panel ─────────────────────────────────────────────────────────
function selectNode(id) {
  selectedId = id;
  const n = nodeById[id];
  if (!n) return;

  nodeElems.select('circle').classed('selected', d => d.id === id);

  document.getElementById('panel-title').textContent = n.label;
  const body = document.getElementById('panel-body');

  const outgoing = (edgesFrom[id] || []).filter(e => e.kind !== 'Contains');
  const incoming = (edgesTo[id]   || []).filter(e => e.kind !== 'Contains');

  const entryTag = n.isEntryPoint ? `<span class="tag" style="background:#78350f;color:#fcd34d">⚡ Entry Point</span>` : '';
  const kindColor = COLOR[n.kind] || '#64748b';
  const kindTag = `<span class="tag" style="background:${kindColor}22;color:${kindColor};border:1px solid ${kindColor}55">${n.kind}</span>`;

  let html = `<div>${kindTag}${entryTag}</div>`;
  html += `<table>`;
  html += `<tr><td>ID</td><td style="word-break:break-all;font-size:11px;color:#64748b">${escHtml(id)}</td></tr>`;
  Object.entries(n.meta || {}).forEach(([k, v]) => {
    if (k === 'isExternal' && v !== 'true') return;
    html += `<tr><td>${escHtml(k)}</td><td style="word-break:break-all">${escHtml(v)}</td></tr>`;
  });
  html += `</table>`;

  if (outgoing.length) {
    html += `<div class="neighbours-section"><h3>Calls / Dependencies (${outgoing.length})</h3>`;
    outgoing.slice(0, 60).forEach(e => {
      const t = nodeById[e.target];
      if (!t) return;
      const c = COLOR[t.kind] || '#64748b';
      html += `<div class="neighbour-item" onclick="selectNode('${escAttr(e.target)}')"><div class="dot" style="background:${c}"></div><span style="color:#64748b;font-size:11px;min-width:70px">${e.kind}</span><span>${escHtml(t.label)}</span></div>`;
    });
    if (outgoing.length > 60) html += `<div style="color:#64748b;font-size:11px;padding:4px 0">… and ${outgoing.length - 60} more</div>`;
    html += `</div>`;
  }

  if (incoming.length) {
    html += `<div class="neighbours-section"><h3>Called By (${incoming.length})</h3>`;
    incoming.slice(0, 60).forEach(e => {
      const s = nodeById[e.source];
      if (!s) return;
      const c = COLOR[s.kind] || '#64748b';
      html += `<div class="neighbour-item" onclick="selectNode('${escAttr(e.source)}')"><div class="dot" style="background:${c}"></div><span style="color:#64748b;font-size:11px;min-width:70px">${e.kind}</span><span>${escHtml(s.label)}</span></div>`;
    });
    if (incoming.length > 60) html += `<div style="color:#64748b;font-size:11px;padding:4px 0">… and ${incoming.length - 60} more</div>`;
    html += `</div>`;
  }

  body.innerHTML = html;
}

function showEdgeInfo(e) {
  const s = nodeById[e.source?.id || e.source];
  const t = nodeById[e.target?.id || e.target];
  const toast = document.getElementById('toast');
  toast.textContent = `${s?.label || '?'} → [${e.kind}] → ${t?.label || '?'}${e.meta?.line ? ' @ line ' + e.meta.line : ''}`;
  toast.style.display = 'block';
  setTimeout(() => toast.style.display = 'none', 3000);
}

function clearPanel() {
  document.getElementById('panel-title').textContent = '–';
  document.getElementById('panel-body').innerHTML = '<p id="no-selection">Click a node to inspect</p>';
}

function toggleLabels() { labelsVisible = !labelsVisible; nodesLayer.selectAll('text.node-label').style('display', labelsVisible ? 'block' : 'none'); }
function resetZoom() { svg.transition().duration(500).call(zoom.transform, d3.zoomIdentity); }
function updateForce(v) { forceManyBodyStrength = -v; if (simulation) { simulation.force('charge', d3.forceManyBody().strength(forceManyBodyStrength)); simulation.alpha(0.3).restart(); } }

function escHtml(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
function escAttr(s) { return String(s).replace(/'/g,"\\'"); }
function truncate(s, n) { return s.length > n ? s.slice(0, n) + '…' : s; }

// ── Keyboard shortcuts ────────────────────────────────────────────────────────
document.addEventListener('keydown', e => {
  if (e.key === 'f' || e.key === 'F') document.getElementById('search-input').focus();
  if (e.key === 'l' || e.key === 'L') toggleLabels();
  if (e.key === 'Escape') { selectedId = null; clearPanel(); }
});

// ── Init ─────────────────────────────────────────────────────────────────────
applyFilter();
</script>
</body>
</html>
""";
}
