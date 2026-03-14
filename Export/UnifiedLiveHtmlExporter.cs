using System.Text;

namespace DotNetGraphScanner.Export;

/// <summary>
/// Generates a self-contained HTML page that connects to a Neo4j-compatible
/// database at runtime and exposes three views in a shared live explorer:
///   • API Dependencies  – cross-API entry-point / outbound-call map
///   • Code Graph        – per-API force-directed structural code graph
///   • Impact Explorer   – find HTTP endpoints affected by changed files/functions
/// </summary>
public sealed class UnifiedLiveHtmlExporter
{
    private readonly string _boltUrl;
    private readonly string _defaultUser;

    public UnifiedLiveHtmlExporter(
        string boltUrl     = "bolt://127.0.0.1:7687",
        string defaultUser = "neo4j")
    {
        _boltUrl     = boltUrl;
        _defaultUser = defaultUser;
    }

    public Task ExportAsync(string outputPath, CancellationToken ct = default)
    {
        var html = BuildHtml(_boltUrl, _defaultUser);
        File.WriteAllText(outputPath, html, Encoding.UTF8);
        Console.WriteLine($"  Unified Live Explorer → {outputPath}");
        return Task.CompletedTask;
    }

    private static string BuildHtml(string boltUrl, string defaultUser) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<title>dotnet-graph-scanner – Live Explorer</title>
<script src="https://cdn.jsdelivr.net/npm/neo4j-driver@5/lib/browser/neo4j-web.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/d3@7/dist/d3.min.js"></script>
<style>
*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0d1117; color: #c9d1d9; height: 100vh; display: flex; flex-direction: column; overflow: hidden; }

/* ── Connection bar ───────────────────────────────────────────── */
#conn-bar { background: #161b22; border-bottom: 1px solid #21262d; padding: 8px 16px; display: flex; align-items: center; gap: 10px; flex-shrink: 0; flex-wrap: wrap; }
#conn-bar label { font-size: 12px; color: #8b949e; white-space: nowrap; }
#conn-bar input { background: #0d1117; border: 1px solid #30363d; border-radius: 6px; color: #c9d1d9; font-size: 12px; padding: 5px 9px; width: 200px; }
#conn-bar input:focus { outline: none; border-color: #388bfd; }
#conn-bar input[type=password] { width: 110px; letter-spacing: 2px; }
.conn-btn { padding: 5px 14px; border-radius: 6px; border: none; font-size: 12px; font-weight: 600; cursor: pointer; }
#btn-connect { background: #238636; color: #fff; }
#btn-connect:hover { background: #2ea043; }
#btn-refresh { background: #1f6feb; color: #fff; }
#btn-refresh:hover { background: #388bfd; }
#btn-clear { background: #21262d; color: #8b949e; border: 1px solid #30363d; }
#btn-clear:hover { background: #30363d; color: #c9d1d9; }
#conn-status { font-size: 12px; white-space: nowrap; }
#conn-status.ok { color: #3fb950; }
#conn-status.err { color: #f85149; }
#conn-status.loading { color: #e3b341; }
#conn-bar .spacer { flex: 1; }
#view-mode { background: #0d1117; border: 1px solid #388bfd; border-radius: 6px; color: #c9d1d9; font-size: 12px; font-weight: 700; padding: 5px 10px; cursor: pointer; }
#view-mode:focus { outline: none; }

/* ── Shared section base ─────────────────────────────────────── */
.view-section { flex: 1; display: flex; flex-direction: column; overflow: hidden; }

/* ── API Dependencies view ───────────────────────────────────── */
#deps-header { padding: 10px 24px; border-bottom: 1px solid #21262d; display: flex; align-items: center; gap: 16px; flex-shrink: 0; }
#deps-header h1 { font-size: 15px; font-weight: 700; color: #f0f6fc; }
.subtitle { font-size: 11px; color: #8b949e; }
.legend { display: flex; align-items: center; gap: 16px; margin-left: auto; font-size: 12px; }
.legend-item { display: flex; align-items: center; gap: 6px; }
.pill-ep-sample { width:12px; height:12px; border-radius:3px; background:#0f291e; border:1px solid #238636; }
.pill-out-sample { width:12px; height:12px; border-radius:3px; background:#2d1c00; border:1px solid #d29922; }

#deps-empty-state { flex:1; display:flex; align-items:center; justify-content:center; }
#deps-empty-card { text-align:center; max-width:400px; }
#deps-empty-card h2 { font-size:16px; font-weight:600; color:#8b949e; margin-bottom:8px; }
#deps-empty-card p { font-size:13px; color:#484f58; }
@keyframes spin { to { transform: rotate(360deg); } }
#deps-spinner { width:32px; height:32px; border:3px solid #21262d; border-top-color:#1f6feb; border-radius:50%; animation:spin .7s linear infinite; margin:0 auto 14px; display:none; }
#deps-spinner.visible { display:block; }

#deps-canvas-wrap { flex:1; overflow:hidden; position:relative; padding:40px; display:none; cursor:grab; user-select:none; }
#deps-canvas-wrap.dragging { cursor:grabbing; }
#deps-canvas { display:block; pointer-events:none; }
#deps-canvas * { pointer-events:auto; }

.api-card { fill:#161b22; stroke:#30363d; stroke-width:1; rx:10; ry:10; }
.api-card.dim { opacity:0.35; }
.api-name { fill:#f0f6fc; font-size:14px; font-weight:700; user-select:none; }
.api-name.dim { opacity:0.35; }
.col-label { fill:#8b949e; font-size:10px; font-weight:600; text-transform:uppercase; letter-spacing:.06em; user-select:none; }
.api-scanned { fill:#484f58; font-size:9px; user-select:none; }

.pill-ep { fill:#0f291e; stroke:#238636; stroke-width:1; rx:5; cursor:pointer; }
.pill-ep:hover { fill:#1a4731; }
.pill-ep.active { fill:#1a4731; stroke:#3fb950; stroke-width:2; }
.pill-ep.dim { opacity:0.25; }
.pill-ep-text { fill:#3fb950; font-size:11px; font-weight:600; pointer-events:none; user-select:none; }
.pill-ep-text.dim { opacity:0.25; }
.pill-ep-badge { fill:#238636; rx:3; pointer-events:none; }
.pill-ep-badge.dim { opacity:0.25; }
.pill-ep-badge-text { fill:#f0f6fc; font-size:9px; font-weight:700; pointer-events:none; user-select:none; }
.pill-ep-badge-text.dim { opacity:0.25; }

.pill-out { fill:#2d1c00; stroke:#d29922; stroke-width:1; rx:5; cursor:pointer; }
.pill-out:hover { fill:#3d2600; }
.pill-out.active { fill:#3d2600; stroke:#f97316; stroke-width:2; }
.pill-out.dim { opacity:0.25; }
.pill-out-text { fill:#e3b341; font-size:11px; font-weight:600; pointer-events:none; user-select:none; }
.pill-out-text.dim { opacity:0.25; }
.pill-out-arrow { fill:#d29922; pointer-events:none; }
.pill-out-arrow.dim { opacity:0.25; }

.connector { fill:none; stroke:#30363d; stroke-width:1.5; opacity:0.9; stroke-dasharray:5 3; }
.connector.active { stroke:#58a6ff; stroke-width:2; opacity:1; stroke-dasharray:none; }
.connector.dim { stroke:#30363d; opacity:0.15; stroke-dasharray:5 3; }
.connector-dot { fill:#30363d; }
.connector-dot.active { fill:#58a6ff; }
.connector-dot.dim { opacity:0.15; }
.card-divider { stroke:#21262d; stroke-width:1; }

#deps-impact-panel { position:fixed; bottom:0; left:0; right:0; background:#161b22; border-top:1px solid #30363d; padding:14px 24px; transition:transform .2s; transform:translateY(100%); max-height:220px; overflow-y:auto; z-index:100; }
#deps-impact-panel.visible { transform:translateY(0); }
#deps-impact-panel h2 { font-size:13px; font-weight:700; margin-bottom:10px; color:#f0f6fc; }
.close-btn { position:absolute; top:14px; right:24px; background:none; border:none; color:#8b949e; cursor:pointer; font-size:18px; line-height:1; }
.close-btn:hover { color:#f0f6fc; }
.impact-section { margin-bottom:10px; }
.impact-section h3 { font-size:11px; font-weight:600; color:#8b949e; text-transform:uppercase; letter-spacing:.06em; margin-bottom:6px; }
.impact-tag { display:inline-block; margin:2px 4px; padding:3px 10px; border-radius:12px; font-size:11px; font-weight:600; }
.impact-tag.ep { background:#0f291e; border:1px solid #238636; color:#3fb950; }
.impact-tag.out { background:#2d1c00; border:1px solid #d29922; color:#e3b341; }
.impact-empty { color:#8b949e; font-size:12px; font-style:italic; }

/* ── Code Graph view ─────────────────────────────────────────── */
#view-code-graph { flex-direction: column; }
#cg-toolbar { background:#161b22; border-bottom:1px solid #21262d; padding:8px 16px; display:flex; align-items:center; gap:10px; flex-shrink:0; flex-wrap:wrap; }
#cg-toolbar label { font-size:12px; color:#8b949e; }
#cg-toolbar select, #cg-toolbar input { background:#0d1117; border:1px solid #30363d; border-radius:6px; color:#c9d1d9; font-size:12px; padding:5px 9px; }
#cg-toolbar input { width:180px; }
#cg-toolbar input:focus, #cg-toolbar select:focus { outline:none; border-color:#388bfd; }
#cg-status { font-size:12px; color:#8b949e; }
#cg-empty { flex:1; display:flex; align-items:center; justify-content:center; color:#484f58; font-size:14px; }
#cg-canvas { flex:1; display:block; background:#0d1117; cursor:grab; }
#cg-canvas.dragging { cursor:grabbing; }
#cg-detail { position:absolute; top:50px; right:0; width:260px; background:#161b22; border-left:1px solid #21262d; padding:14px; overflow-y:auto; max-height:calc(100vh - 100px); display:none; z-index:10; }
#cg-detail h3 { font-size:13px; font-weight:700; color:#f0f6fc; margin-bottom:8px; word-break:break-all; }
#cg-detail .meta-row { font-size:11px; color:#8b949e; padding:2px 0; word-break:break-all; }
#cg-detail .meta-row span { color:#c9d1d9; }
#cg-detail .close-btn { top:8px; right:8px; }

/* ── Code Graph – hierarchy view ────────────────────────────── */
#cg-hier-wrap { flex:1; overflow:hidden; background:#0d1117; display:none; }
#cg-hier-svg  { width:100%; height:100%; cursor:grab; }
.cg-h-box { cursor:pointer; }
.cg-h-box:hover .cg-h-hdr { filter:brightness(1.3); }
#cg-toolbar button { background:#21262d; border:1px solid #30363d; color:#c9d1d9; border-radius:6px; font-size:12px; padding:5px 10px; cursor:pointer; }
#cg-toolbar button:hover { background:#30363d; }

/* ── Impact Explorer view ────────────────────────────────────── */
#view-impact { flex-direction: column; }
#imp-toolbar { background:#161b22; border-bottom:1px solid #21262d; padding:12px 16px; display:flex; align-items:center; gap:10px; flex-shrink:0; flex-wrap:wrap; }
#imp-toolbar label { font-size:12px; color:#8b949e; white-space: nowrap; }
#imp-toolbar select, #imp-toolbar input { background:#0d1117; border:1px solid #30363d; border-radius:6px; color:#c9d1d9; font-size:12px; padding:5px 9px; }
#imp-input { width:280px; }
#imp-toolbar input:focus, #imp-toolbar select:focus { outline:none; border-color:#388bfd; }
#imp-run { padding:5px 16px; border-radius:6px; border:none; background:#1f6feb; color:#fff; font-size:12px; font-weight:600; cursor:pointer; }
#imp-run:hover { background:#388bfd; }
#imp-hint { padding:10px 20px; font-size:12px; color:#8b949e; background:#161b22; border-bottom:1px solid #21262d; display:none; }
#imp-hint code { background:#0d1117; padding:2px 6px; border-radius:4px; color:#79c0ff; font-family:monospace; font-size:11px; }
#imp-body { flex:1; overflow-y:auto; padding:20px; }
#imp-empty { color:#484f58; font-size:14px; text-align:center; padding-top:40px; }
#imp-results { width:100%; border-collapse:collapse; display:none; }
#imp-results th { text-align:left; padding:8px 12px; font-size:11px; font-weight:600; color:#8b949e; text-transform:uppercase; letter-spacing:.05em; border-bottom:1px solid #21262d; }
#imp-results td { padding:8px 12px; font-size:12px; border-bottom:1px solid #161b22; vertical-align:top; }
#imp-results tr:hover td { background:#161b22; }
.imp-method { display:inline-block; padding:2px 7px; border-radius:4px; font-size:10px; font-weight:700; background:#1f3a5f; color:#79c0ff; margin-right:4px; }
.imp-none { color:#484f58; font-size:13px; text-align:center; padding:32px; }

::-webkit-scrollbar { width:6px; }
::-webkit-scrollbar-track { background:#1e2130; }
::-webkit-scrollbar-thumb { background:#2d3348; border-radius:3px; }
</style>
</head>
<body>

<!-- ── Shared connection bar ─────────────────────────────────── -->
<div id="conn-bar">
  <label>Bolt URL</label>
  <input type="text"     id="inp-url"  value="{{boltUrl}}"/>
  <label>User</label>
  <input type="text"     id="inp-user" value="{{defaultUser}}" style="width:80px"/>
  <label>Password</label>
  <input type="password" id="inp-pass" value="" placeholder="(none)"/>
  <button class="conn-btn" id="btn-connect" onclick="doConnect()">Connect</button>
  <button class="conn-btn" id="btn-refresh" onclick="doRefresh()" style="display:none">Refresh</button>
  <button class="conn-btn" id="btn-clear"   onclick="doClear()"   style="display:none">Disconnect</button>
  <span id="conn-status"></span>
  <div class="spacer"></div>
  <select id="view-mode" onchange="switchView(this.value)" title="Switch view">
    <option value="api-deps">🔗 API Dependencies</option>
    <option value="code-graph">🕸 Code Graph</option>
    <option value="impact">🎯 Impact Explorer</option>
  </select>
</div>

<!-- ── View 1 : API Dependencies ─────────────────────────────── -->
<div id="view-api-deps" class="view-section">
  <div id="deps-header">
    <h1>Cross-API Live Map</h1>
    <span class="subtitle" id="deps-api-summary">Not connected</span>
    <div class="legend">
      <div class="legend-item"><div class="pill-ep-sample"></div> Entry point (inbound)</div>
      <div class="legend-item"><div class="pill-out-sample"></div> Outbound API call</div>
    </div>
  </div>
  <div id="deps-empty-state">
    <div id="deps-empty-card">
      <div id="deps-spinner"></div>
      <h2 id="deps-empty-title">Enter connection details above</h2>
      <p id="deps-empty-sub">Connect to a Neo4j-compatible database that contains scan results pushed with <code>scan --push</code>.</p>
    </div>
  </div>
  <div id="deps-canvas-wrap">
    <svg id="deps-canvas" xmlns="http://www.w3.org/2000/svg"></svg>
  </div>
  <div id="deps-impact-panel">
    <button class="close-btn" onclick="closeDepsPanel()">✕</button>
    <h2 id="deps-panel-title"></h2>
    <div id="deps-panel-body"></div>
  </div>
</div>

<!-- ── View 2 : Code Graph ────────────────────────────────────── -->
<div id="view-code-graph" class="view-section" style="display:none;position:relative">
  <div id="cg-toolbar">
    <label>API</label>
    <select id="cg-api-select" onchange="loadCodeGraph(this.value)">
      <option value="">— select an API —</option>
    </select>
    <label>Kind</label>
    <select id="cg-kind-filter" onchange="applyCodeGraphFilter()">
      <option value="">All kinds</option>
      <option value="Method">Method</option>
      <option value="Class">Class</option>
      <option value="Interface">Interface</option>
      <option value="Struct">Struct</option>
      <option value="Namespace">Namespace</option>
      <option value="Project">Project</option>
      <option value="NuGetPackage">NuGet Package</option>
      <option value="ExternalType">External Type</option>
    </select>
    <input id="cg-search" placeholder="Search labels…" oninput="applyCodeGraphFilter()" autocomplete="off"/>
    <label>View</label>
    <select id="cg-graph-mode" onchange="switchCgMode(this.value)">
      <option value="force">⚡ Force</option>
      <option value="hierarchy">▦ Hierarchy</option>
    </select>
    <button id="cg-btn-h2" onclick="cgHierExpandAll(2)" style="display:none">↕ 2 Levels</button>
    <button id="cg-btn-h3" onclick="cgHierExpandAll(3)" style="display:none">↕ 3 Levels</button>
    <button id="cg-btn-hc" onclick="cgHierCollapseAll()" style="display:none">⟵ Collapse</button>
    <span id="cg-status"></span>
  </div>
  <div id="cg-empty">Select an API above to visualise its code graph.</div>
  <canvas id="cg-canvas" style="display:none"></canvas>
  <div id="cg-hier-wrap"><svg id="cg-hier-svg"></svg></div>
  <div id="cg-detail">
    <button class="close-btn" onclick="document.getElementById('cg-detail').style.display='none'">✕</button>
    <h3 id="cg-detail-label"></h3>
    <div id="cg-detail-meta"></div>
  </div>
</div>

<!-- ── View 3 : Impact Explorer ──────────────────────────────── -->
<div id="view-impact" class="view-section" style="display:none">
  <div id="imp-toolbar">
    <label>Mode</label>
    <select id="imp-mode" onchange="updateImpactMode(this.value)">
      <option value="file">File path</option>
      <option value="fn">Function name</option>
      <option value="commit">Commit SHA</option>
      <option value="range">Commit range</option>
    </select>
    <label id="imp-input-label">File path fragment</label>
    <input id="imp-input" placeholder="e.g. Models.cs" autocomplete="off"/>
    <label>API filter</label>
    <select id="imp-api-select">
      <option value="">All APIs</option>
    </select>
    <button id="imp-run" onclick="runImpactQuery()">Find Affected Endpoints</button>
  </div>
  <div id="imp-hint"></div>
  <div id="imp-body">
    <div id="imp-empty">Enter a search term above and click <strong>Find Affected Endpoints</strong>.</div>
    <table id="imp-results">
      <thead><tr><th>API</th><th>Method</th><th>Route</th><th>Label</th></tr></thead>
      <tbody id="imp-tbody"></tbody>
    </table>
  </div>
</div>

<script>
'use strict';

// ══════════════════════════════════════════════════════════════════════════════
// Shared connection state
// ══════════════════════════════════════════════════════════════════════════════
let _driver = null;

function setStatus(msg, cls = '') {
  const el = document.getElementById('conn-status');
  el.textContent = msg;
  el.className = cls;
}

// Each query runs in its own session (Memgraph compatibility — no named databases).
async function runQuery(driver, cypher, params = {}) {
  const session = driver.session();
  try   { return (await session.run(cypher, params)).records; }
  finally { await session.close(); }
}

async function doConnect() {
  const url  = document.getElementById('inp-url').value.trim();
  const user = document.getElementById('inp-user').value.trim();
  const pass = document.getElementById('inp-pass').value;
  if (_driver) { try { await _driver.close(); } catch {} _driver = null; }
  setStatus('Connecting…', 'loading');
  depsShowSpinner(true);
  try {
    _driver = neo4j.driver(url,
      user ? neo4j.auth.basic(user, pass) : neo4j.auth.none());
    await _driver.verifyConnectivity();
    setStatus('● Connected', 'ok');
    document.getElementById('btn-refresh').style.display = '';
    document.getElementById('btn-clear').style.display   = '';
    document.getElementById('btn-connect').textContent   = 'Reconnect';
    await doRefresh();
  } catch (err) {
    setStatus('✕ ' + err.message, 'err');
    _driver = null;
    depsShowSpinner(false);
    depsShowEmpty('Connection failed', err.message);
  }
}

async function doClear() {
  if (_driver) { try { await _driver.close(); } catch {} _driver = null; }
  setStatus('', '');
  document.getElementById('btn-refresh').style.display = 'none';
  document.getElementById('btn-clear').style.display   = 'none';
  document.getElementById('btn-connect').textContent   = 'Connect';
  depsShowEmpty('Disconnected', 'Enter connection details and click Connect.');
  document.getElementById('deps-api-summary').textContent = 'Not connected';
  closeDepsPanel();
  cgReset();
  impResetApiList();
}

async function doRefresh() {
  if (!_driver) return;
  setStatus('Refreshing…', 'loading');
  depsShowSpinner(true);
  closeDepsPanel();
  try {
    // Always reload the API list (used by Code Graph + Impact Explorer)
    await refreshApiList();

    const view = document.getElementById('view-mode').value;
    if (view === 'api-deps' || view === 'api-deps') {
      await refreshApiDeps();
    }
    setStatus('● Connected', 'ok');
  } catch (err) {
    setStatus('✕ ' + err.message, 'err');
    depsShowEmpty('Query failed', err.message);
    depsShowSpinner(false);
  }
}

// ── View switching ────────────────────────────────────────────────────────────
function switchView(mode) {
  document.getElementById('view-api-deps').style.display   = mode === 'api-deps'    ? 'flex' : 'none';
  document.getElementById('view-code-graph').style.display = mode === 'code-graph'  ? 'flex' : 'none';
  document.getElementById('view-impact').style.display     = mode === 'impact'      ? 'flex' : 'none';
  if (mode === 'api-deps' && _driver) { refreshApiDeps().catch(e => setStatus('✕ ' + e.message, 'err')); }
  if (mode === 'code-graph') {
    if (_cgMode === 'force') fitCgCanvas();
    else if (_cgAllNodes.length) cgRenderHierarchy();
  }
}

// ══════════════════════════════════════════════════════════════════════════════
// Shared API list (populates Code Graph and Impact dropdowns)
// ══════════════════════════════════════════════════════════════════════════════
async function refreshApiList() {
  const apiRecs = await runQuery(_driver, 'MATCH (a:Api) RETURN a.name AS name ORDER BY a.name');
  const codeNodeRecs = await runQuery(_driver, 'MATCH (n:CodeNode) WHERE n.apiName IS NOT NULL RETURN DISTINCT n.apiName AS name ORDER BY name');
  const names = Array.from(new Set([
    ...apiRecs.map(r => r.get('name')),
    ...codeNodeRecs.map(r => r.get('name')),
  ])).sort((a, b) => String(a).localeCompare(String(b)));
  populateSelect('cg-api-select',  names, '— select an API —');
  populateSelect('imp-api-select', names, 'All APIs', true);
}

function populateSelect(id, names, placeholder, hasAll = false) {
  const sel = document.getElementById(id);
  const prev = sel.value;
  sel.innerHTML = '';
  if (hasAll) sel.appendChild(new Option(placeholder, ''));
  else        sel.appendChild(new Option(placeholder, ''));
  names.forEach(n => sel.appendChild(new Option(n, n)));
  if (names.includes(prev)) sel.value = prev;
}

function impResetApiList() {
  populateSelect('cg-api-select',  [], '— select an API —');
  populateSelect('imp-api-select', [], 'All APIs', true);
}

// ══════════════════════════════════════════════════════════════════════════════
// View 1 – API Dependencies
// ══════════════════════════════════════════════════════════════════════════════
async function refreshApiDeps() {
  depsShowSpinner(true);
  const data = await loadApiDepsData(_driver);
  if (data.apis.length === 0) {
    depsShowSpinner(false);
    document.getElementById('deps-api-summary').textContent = 'No data';
    depsShowEmpty('No API data found',
      'Run: dotnet run -- scan <project.csproj> --output <dir> --push');
    return;
  }
  document.getElementById('deps-api-summary').textContent =
    data.apis.length + ' API' + (data.apis.length !== 1 ? 's' : '') +
    ' · ' + data.apis.reduce((s, a) => s + a.entryPoints.length, 0) + ' entry points' +
    ' · ' + data.connections.filter(c => c.matchedEntrypointNodeId).length + ' connections resolved' +
    ' · resolution: ' + data.connectionSource +
    ' · impact: ' + data.impactSource;
  initApiDepsRenderer(data);
}

function depsNormalizePath(path) {
  return String(path || '').trim().replace(/^\/+|\/+$/g, '').toLowerCase();
}

function depsRouteTemplatesMatch(template, path) {
  const normTemplate = depsNormalizePath(template);
  const normPath = depsNormalizePath(path);
  if (normTemplate === normPath) return true;

  const tParts = normTemplate.split('/').filter(Boolean);
  const pParts = normPath.split('/').filter(Boolean);
  if (tParts.length !== pParts.length) return false;

  for (let i = 0; i < tParts.length; i++) {
    const t = tParts[i];
    const p = pParts[i];
    if (t === p) continue;
    if ((t.startsWith('{') && t.endsWith('}')) || (p.startsWith('{') && p.endsWith('}'))) continue;
    return false;
  }
  return true;
}

function depsResolveConnectionsFromCards(entryPoints, outboundCalls) {
  const epsByApi = {};
  entryPoints.forEach(ep => {
    const api = ep.apiName || ep.api;
    (epsByApi[api] = epsByApi[api] || []).push(ep);
  });

  return outboundCalls.map(out => {
    const targetApi = out.targetApi;
    const target = String(out.targetRoute || '');
    const parts = target.split(' ', 2).map(p => p.trim()).filter(Boolean);
    const verb = parts.length > 1 ? parts[0].toUpperCase() : 'HTTP';
    const path = parts.length > 1 ? parts[1] : parts[0] || '';
    const candidates = epsByApi[targetApi] || [];
    const match = candidates.find(ep =>
      String(ep.httpMethod || 'HTTP').toUpperCase() === verb &&
      depsRouteTemplatesMatch(ep.route, path));

    return {
      outboundCallNodeId: out.nodeId,
      matchedEntrypointNodeId: match ? match.nodeId : null
    };
  });
}

function depsGetMethodSuffix(fullName) {
  const value = String(fullName || '');
  const parenIdx = value.indexOf('(');
  if (parenIdx < 0) return null;
  const dotBefore = value.lastIndexOf('.', parenIdx - 1);
  return dotBefore >= 0 ? value.slice(dotBefore + 1) : value;
}

function depsBuildCanonicalImpacts(entryPoints, outboundCalls, graphNodes, graphEdges) {
  const nodeById = Object.fromEntries(graphNodes.map(node => [node.id, node]));
  const successors = {};
  const typeToMethods = {};
  const implEdges = [];

  graphEdges.forEach(edge => {
    if (edge.rel === 'CALLS') {
      (successors[edge.src] = successors[edge.src] || new Set()).add(edge.tgt);
      return;
    }

    if (edge.rel === 'CONTAINS') {
      const src = nodeById[edge.src];
      const tgt = nodeById[edge.tgt];
      if (!src || !tgt) return;
      if (!['Interface', 'Class', 'Struct'].includes(src.kind) || tgt.kind !== 'Method') return;
      (typeToMethods[edge.src] = typeToMethods[edge.src] || new Set()).add(edge.tgt);
      return;
    }

    if (edge.rel === 'IMPLEMENTS') implEdges.push(edge);
  });

  implEdges.forEach(edge => {
    const ifaceMethods = Array.from(typeToMethods[edge.tgt] || []);
    const classMethods = Array.from(typeToMethods[edge.src] || []);
    if (!ifaceMethods.length || !classMethods.length) return;

    const classBySig = {};
    classMethods.forEach(methodId => {
      const suffix = depsGetMethodSuffix(nodeById[methodId]?.fullName);
      if (suffix) classBySig[suffix] = methodId;
    });

    ifaceMethods.forEach(methodId => {
      const suffix = depsGetMethodSuffix(nodeById[methodId]?.fullName);
      if (!suffix) return;
      const targetMethodId = classBySig[suffix];
      if (!targetMethodId) return;
      (successors[methodId] = successors[methodId] || new Set()).add(targetMethodId);
    });
  });

  const apiCallIds = new Set(outboundCalls.map(call => call.nodeId));
  return entryPoints.map(ep => {
    const visited = new Set();
    const reachable = new Set();
    const queue = [ep.nodeId];

    while (queue.length) {
      const current = queue.shift();
      if (!current || visited.has(current)) continue;
      visited.add(current);
      if (apiCallIds.has(current)) reachable.add(current);
      (successors[current] || []).forEach(nextId => {
        if (!visited.has(nextId)) queue.push(nextId);
      });
    }

    return {
      entrypointNodeId: ep.nodeId,
      callNodeIds: Array.from(reachable)
    };
  }).filter(impact => impact.callNodeIds.length > 0);
}

async function loadApiDepsData(driver) {
  const apisRecs = await runQuery(driver, 'MATCH (a:Api) RETURN a.name AS name, a.scannedAt AS scannedAt ORDER BY a.name');
  const codeNodeApiRecs = await runQuery(driver, "MATCH (n:CodeNode) WHERE n.apiName IS NOT NULL AND n.scannedAt IS NOT NULL WITH n.apiName AS name, collect(DISTINCT n.scannedAt) AS scannedTimes RETURN name, scannedTimes[0] AS scannedAt ORDER BY name");
  const epsRecs  = await runQuery(driver, "MATCH (e:CodeNode) WHERE e.kind = 'Method' AND e.isEntryPoint = 'True' AND e.httpMethod IS NOT NULL AND e.routeTemplate IS NOT NULL RETURN e.apiName AS apiName, e.id AS nodeId, e.httpMethod AS httpMethod, e.routeTemplate AS route, coalesce(e.label, e.httpMethod + ' ' + e.routeTemplate) AS label ORDER BY apiName, route");
  const outsRecs = await runQuery(driver, "MATCH (o:CodeNode) WHERE o.kind = 'Method' AND o.isApiCall = 'true' AND o.targetApi IS NOT NULL AND o.targetRoute IS NOT NULL RETURN o.apiName AS apiName, o.id AS nodeId, o.targetApi AS targetApi, o.targetRoute AS targetRoute, coalesce(o.label, o.apiName + ' → ' + o.targetApi + ': ' + o.targetRoute) AS label ORDER BY apiName, targetApi, targetRoute");
  const canonicalConnRecs = await runQuery(driver, "MATCH (o:CodeNode)-[:RESOLVES_TO]->(e:CodeNode) WHERE o.kind = 'Method' AND e.kind = 'Method' RETURN o.id AS outId, e.id AS epId");

  const canonicalGraphNodeRecs = await runQuery(driver, "MATCH (n:CodeNode) WHERE n.kind IN ['Method','Class','Interface','Struct'] RETURN n.id AS id, n.kind AS kind, n.fullName AS fullName");
  const canonicalGraphEdgeRecs = await runQuery(driver, "MATCH (s:CodeNode)-[r]->(t:CodeNode) WHERE type(r) IN ['CALLS','CONTAINS','IMPLEMENTS'] RETURN s.id AS src, type(r) AS rel, t.id AS tgt");

  const apiNames = Array.from(new Set([
    ...apisRecs.map(r => r.get('name')),
    ...codeNodeApiRecs.map(r => r.get('name')),
    ...epsRecs.map(r => r.get('apiName')),
    ...outsRecs.map(r => r.get('apiName')),
  ])).sort((a, b) => String(a).localeCompare(String(b)));
  const scannedAtByApi = Object.fromEntries(codeNodeApiRecs.map(r => [r.get('name'), r.get('scannedAt')]));
  apisRecs.forEach(r => { scannedAtByApi[r.get('name')] = r.get('scannedAt'); });
  const epsByApi = {}, outsByApi = {};
  apiNames.forEach(n => { epsByApi[n] = []; outsByApi[n] = []; });
  epsRecs.forEach(r  => { const a = r.get('apiName'); (epsByApi[a]  = epsByApi[a]  || []).push({ nodeId: r.get('nodeId'), httpMethod: r.get('httpMethod'), route: r.get('route'), label: r.get('label') }); });
  outsRecs.forEach(r => { const a = r.get('apiName'); (outsByApi[a] = outsByApi[a] || []).push({ nodeId: r.get('nodeId'), targetApi: r.get('targetApi'), targetRoute: r.get('targetRoute'), label: r.get('label') }); });

  const canonicalImpacts = depsBuildCanonicalImpacts(
    epsRecs.map(r => ({ nodeId: r.get('nodeId') })),
    outsRecs.map(r => ({ nodeId: r.get('nodeId') })),
    canonicalGraphNodeRecs.map(r => ({ id: r.get('id'), kind: r.get('kind'), fullName: r.get('fullName') })),
    canonicalGraphEdgeRecs.map(r => ({ src: r.get('src'), rel: r.get('rel'), tgt: r.get('tgt') })));
  const impacts = canonicalImpacts;
  const impactSource = 'canonical BFS';
  const connectionSource = canonicalConnRecs.length
    ? 'canonical CodeNode RESOLVES_TO'
    : 'client-side route matching';
  const connections = canonicalConnRecs.length
    ? canonicalConnRecs.map(r => ({ outboundCallNodeId: r.get('outId'), matchedEntrypointNodeId: r.get('epId') }))
    : depsResolveConnectionsFromCards(
          epsRecs.map(r => ({ apiName: r.get('apiName'), nodeId: r.get('nodeId'), httpMethod: r.get('httpMethod'), route: r.get('route') })),
          outsRecs.map(r => ({ apiName: r.get('apiName'), nodeId: r.get('nodeId'), targetApi: r.get('targetApi'), targetRoute: r.get('targetRoute') })));
  return { apis: apiNames.map(name => ({ name, scannedAt: scannedAtByApi[name] ?? null, entryPoints: epsByApi[name] || [], outboundCalls: outsByApi[name] || [] })), impacts, connections, connectionSource, impactSource };
}

// ── Renderer (identical logic to static exporter, namespaced to deps-*) ──────
const DEPS = { BOX_W:360, BOX_H_HEADER:64, PILL_H:28, PILL_ROW:38, BOX_PAD:18, COL_GAP:12, API_GAP:180, MARGIN_X:60, MARGIN_Y:50, CARD_RADIUS:10, BADGE_W:38 };
const SVG_NS = 'http://www.w3.org/2000/svg';
let _epById = {}, _outById = {}, _epToCallIds = {}, _callToEpIds = {}, _outConnTo = {}, _epConnFrom = {};
let _depsActiveId = null;

function svgEl(tag, attrs, text) {
  const e = document.createElementNS(SVG_NS, tag);
  Object.entries(attrs || {}).forEach(([k,v]) => e.setAttribute(k, v));
  if (text !== undefined) e.textContent = text;
  return e;
}
function truncate(str, maxW, charPx = 6.5) {
  const max = Math.floor(maxW / charPx);
  return str.length > max ? str.slice(0, max - 1) + '…' : str;
}

function depsShowSpinner(on) { document.getElementById('deps-spinner').classList.toggle('visible', on); }
function depsShowEmpty(title, sub) {
  document.getElementById('deps-empty-state').style.display = '';
  document.getElementById('deps-canvas-wrap').style.display = 'none';
  document.getElementById('deps-empty-title').textContent = title;
  document.getElementById('deps-empty-sub').textContent   = sub;
  depsShowSpinner(false);
}
function depsDepsClearCanvas() {
  const svg = document.getElementById('deps-canvas');
  while (svg.firstChild) svg.removeChild(svg.firstChild);
}

function initApiDepsRenderer(DATA) {
  depsDepsClearCanvas();
  depsShowSpinner(false);
  _epById = {}; _outById = {}; _epToCallIds = {}; _callToEpIds = {}; _outConnTo = {}; _epConnFrom = {};
  _depsActiveId = null;

  DATA.apis.forEach(api => {
    api.entryPoints.forEach(ep => { _epById[ep.nodeId]  = { ...ep,  api: api.name }; });
    api.outboundCalls.forEach(o  => { _outById[o.nodeId]  = { ...o,   api: api.name }; });
  });
  DATA.impacts.forEach(imp => {
    _epToCallIds[imp.entrypointNodeId] = imp.callNodeIds;
    imp.callNodeIds.forEach(cn => { (_callToEpIds[cn] = _callToEpIds[cn] || []).push(imp.entrypointNodeId); });
  });
  DATA.connections.forEach(c => {
    _outConnTo[c.outboundCallNodeId] = c.matchedEntrypointNodeId || null;
    if (c.matchedEntrypointNodeId)
      (_epConnFrom[c.matchedEntrypointNodeId] = _epConnFrom[c.matchedEntrypointNodeId] || []).push(c.outboundCallNodeId);
  });

  const D = DEPS;
  const colW = (D.BOX_W - D.COL_GAP * 3) / 2;
  function boxH(api) { return D.BOX_H_HEADER + D.BOX_PAD + Math.max(api.entryPoints.length, api.outboundCalls.length, 1) * D.PILL_ROW + D.BOX_PAD; }
  const layout = {};
  let curX = D.MARGIN_X, maxH = 0;
  DATA.apis.forEach(api => {
    const h = boxH(api), bx = curX, by = D.MARGIN_Y;
    maxH = Math.max(maxH, h);
    layout[api.name] = {
      x: bx, y: by, w: D.BOX_W, h, scannedAt: api.scannedAt || '',
      epPills:  api.entryPoints.map((ep,i) => ({ nodeId:ep.nodeId, x:bx+D.BOX_PAD,            y:by+D.BOX_H_HEADER+D.BOX_PAD+i*D.PILL_ROW, w:colW,   h:D.PILL_H })),
      outPills: api.outboundCalls.map((o,i)  => ({ nodeId:o.nodeId,  x:bx+D.BOX_PAD+colW+D.COL_GAP, y:by+D.BOX_H_HEADER+D.BOX_PAD+i*D.PILL_ROW, w:colW,   h:D.PILL_H })),
    };
    curX += D.BOX_W + D.API_GAP;
  });

  const pillPos = {};
  DATA.apis.forEach(api => {
    const lay = layout[api.name];
    lay.epPills.forEach(p  => { pillPos[p.nodeId] = { ...p, cx:p.x,       cy:p.y+p.h/2, side:'left'  }; });
    lay.outPills.forEach(p => { pillPos[p.nodeId] = { ...p, cx:p.x+p.w,   cy:p.y+p.h/2, side:'right' }; });
  });

  const svgW = curX - D.API_GAP + D.MARGIN_X, svgH = maxH + D.MARGIN_Y * 2, BUF = 160;
  const svg = document.getElementById('deps-canvas');
  svg.setAttribute('width',   svgW + BUF * 2);
  svg.setAttribute('height',  svgH + BUF * 2);
  svg.setAttribute('viewBox', (-BUF) + ' ' + (-BUF) + ' ' + (svgW + BUF * 2) + ' ' + (svgH + BUF * 2));

  const connLayer = svgEl('g', { id:'conn-layer' });
  const cardLayer = svgEl('g', { id:'card-layer' });
  const pillLayer = svgEl('g', { id:'pill-layer' });
  svg.appendChild(connLayer); svg.appendChild(cardLayer); svg.appendChild(pillLayer);

  DATA.apis.forEach(api => {
    const lay = layout[api.name];
    const g = svgEl('g', { 'data-api': api.name });
    g.appendChild(svgEl('rect', { x:lay.x, y:lay.y, width:lay.w, height:lay.h, rx:D.CARD_RADIUS, class:'api-card', 'data-api':api.name }));
    g.appendChild(svgEl('text', { x:lay.x+D.BOX_W/2, y:lay.y+28, class:'api-name', 'text-anchor':'middle', 'data-api':api.name }, api.name));
    if (lay.scannedAt) {
      const ts = lay.scannedAt.length > 19 ? lay.scannedAt.slice(0,19).replace('T',' ')+' UTC' : lay.scannedAt;
      g.appendChild(svgEl('text', { x:lay.x+D.BOX_W/2, y:lay.y+48, class:'api-scanned', 'text-anchor':'middle' }, 'scanned '+ts));
    }
    const divX = lay.x + D.BOX_PAD + colW + D.COL_GAP / 2;
    g.appendChild(svgEl('line', { x1:divX, y1:lay.y+D.BOX_H_HEADER, x2:divX, y2:lay.y+lay.h, class:'card-divider' }));
    const colLY = lay.y + D.BOX_H_HEADER - 6;
    g.appendChild(svgEl('text', { x:lay.x+D.BOX_PAD+colW/2, y:colLY, class:'col-label', 'text-anchor':'middle' }, 'Entry Points'));
    g.appendChild(svgEl('text', { x:lay.x+D.BOX_PAD+colW+D.COL_GAP+colW/2, y:colLY, class:'col-label', 'text-anchor':'middle' }, 'Outbound Calls'));
    cardLayer.appendChild(g);

    lay.epPills.forEach((p, i) => {
      const ep = api.entryPoints[i];
      const pg = svgEl('g', { 'data-ep':ep.nodeId, style:'cursor:pointer' });
      pg.appendChild(svgEl('rect', { x:p.x, y:p.y, width:p.w, height:p.h, class:'pill-ep', 'data-ep':ep.nodeId }));
      pg.appendChild(svgEl('rect', { x:p.x+4, y:p.y+4, width:D.BADGE_W, height:p.h-8, class:'pill-ep-badge', 'data-ep':ep.nodeId }));
      pg.appendChild(svgEl('text', { x:p.x+4+D.BADGE_W/2, y:p.y+p.h/2+1, class:'pill-ep-badge-text', 'text-anchor':'middle', 'dominant-baseline':'middle', 'data-ep':ep.nodeId }, ep.httpMethod));
      pg.appendChild(svgEl('text', { x:p.x+D.BADGE_W+10, y:p.y+p.h/2+1, class:'pill-ep-text', 'dominant-baseline':'middle', 'data-ep':ep.nodeId }, truncate(ep.route, p.w-D.BADGE_W-20)));
      pg.addEventListener('click', () => onEpClick(ep.nodeId));
      pillLayer.appendChild(pg);
    });

    lay.outPills.forEach((p, i) => {
      const out = api.outboundCalls[i];
      const pg  = svgEl('g', { 'data-out':out.nodeId, style:'cursor:pointer' });
      pg.appendChild(svgEl('rect', { x:p.x, y:p.y, width:p.w, height:p.h, class:'pill-out', 'data-out':out.nodeId }));
      const ax = p.x+p.w-10, ay = p.y+p.h/2;
      pg.appendChild(svgEl('polygon', { points:(ax-5)+','+(ay-5)+' '+(ax+3)+','+ay+' '+(ax-5)+','+(ay+5), class:'pill-out-arrow', 'data-out':out.nodeId }));
      pg.appendChild(svgEl('text', { x:p.x+8, y:p.y+p.h/2+1, class:'pill-out-text', 'dominant-baseline':'middle', 'data-out':out.nodeId }, truncate(out.targetApi+': '+out.targetRoute, p.w-24)));
      pg.addEventListener('click', () => onOutClick(out.nodeId));
      pillLayer.appendChild(pg);
    });
  });

  DATA.connections.forEach(conn => {
    if (!conn.matchedEntrypointNodeId) return;
    const src = pillPos[conn.outboundCallNodeId], tgt = pillPos[conn.matchedEntrypointNodeId];
    if (!src || !tgt) return;
    const x1=src.x+src.w, y1=src.cy, x2=tgt.x, y2=tgt.cy;
    let d;
    if (x2 >= x1) {
      const cp = Math.max(60,(x2-x1)*0.45);
      d = 'M '+x1+' '+y1+' C '+(x1+cp)+' '+y1+', '+(x2-cp)+' '+y2+', '+x2+' '+y2;
    } else {
      const arcY = Math.max(...Object.values(layout).map(l=>l.y+l.h))+40, cp=80;
      d = 'M '+x1+' '+y1+' C '+(x1+cp)+' '+y1+', '+(x1+cp)+' '+arcY+', '+((x1+x2)/2)+' '+arcY+' S '+(x2-cp)+' '+arcY+', '+(x2-cp)+' '+y2+' S '+x2+' '+y2+', '+x2+' '+y2;
    }
    connLayer.appendChild(svgEl('path', { d, class:'connector', 'data-conn-out':conn.outboundCallNodeId, 'data-conn-ep':conn.matchedEntrypointNodeId }));
    connLayer.appendChild(svgEl('circle', { cx:x1, cy:y1, r:3.5, class:'connector-dot', 'data-conn-out':conn.outboundCallNodeId, 'data-conn-ep':conn.matchedEntrypointNodeId }));
    connLayer.appendChild(svgEl('circle', { cx:x2, cy:y2, r:3.5, class:'connector-dot', 'data-conn-out':conn.outboundCallNodeId, 'data-conn-ep':conn.matchedEntrypointNodeId }));
  });

  document.getElementById('deps-empty-state').style.display = 'none';
  document.getElementById('deps-canvas-wrap').style.display = 'flex';
  svg.addEventListener('click', e => { if (!e.target.closest('[data-ep],[data-out]')) { depsClearHighlights(); closeDepsPanel(); } });

  let panX=0, panY=0, scale=1;
  svg.style.transformOrigin = '0 0';
  function applyT() { svg.style.transform = 'translate('+panX+'px,'+panY+'px) scale('+scale+')'; }
  applyT();

  const wrap = document.getElementById('deps-canvas-wrap');
  const nw = wrap.cloneNode(false);
  while (wrap.firstChild) nw.appendChild(wrap.firstChild);
  wrap.parentNode.replaceChild(nw, wrap);
  nw.appendChild(svg);
  nw.addEventListener('mousedown', e => {
    if (e.button !== 0 || e.target.closest('[data-ep],[data-out]')) return;
    e.preventDefault(); nw.classList.add('dragging');
    const sx = e.clientX - panX, sy = e.clientY - panY;
    function onM(ev) { panX = ev.clientX - sx; panY = ev.clientY - sy; applyT(); }
    function onU() { nw.classList.remove('dragging'); window.removeEventListener('mousemove', onM); window.removeEventListener('mouseup', onU); }
    window.addEventListener('mousemove', onM); window.addEventListener('mouseup', onU);
  });
  nw.addEventListener('wheel', e => {
    e.preventDefault();
    const rect = nw.getBoundingClientRect();
    const mx = e.clientX - rect.left, my = e.clientY - rect.top, os = scale;
    scale = Math.min(3, Math.max(0.15, scale * (e.deltaY < 0 ? 1.1 : 0.9)));
    panX = mx - (mx - panX) * (scale / os); panY = my - (my - panY) * (scale / os);
    applyT();
  }, { passive: false });
}

// ── Interaction ───────────────────────────────────────────────────────────────
function depsClearHighlights() {
  _depsActiveId = null;
  document.querySelectorAll('.active').forEach(e => e.classList.remove('active'));
  document.querySelectorAll('.dim').forEach(e => e.classList.remove('dim'));
}
function depsDimAll() {
  document.querySelectorAll('.api-card,.api-name,.col-label,.api-scanned').forEach(e => e.classList.add('dim'));
  document.querySelectorAll('.pill-ep,.pill-ep-text,.pill-ep-badge,.pill-ep-badge-text').forEach(e => e.classList.add('dim'));
  document.querySelectorAll('.pill-out,.pill-out-text,.pill-out-arrow').forEach(e => e.classList.add('dim'));
  document.querySelectorAll('.connector,.connector-dot').forEach(e => e.classList.add('dim'));
}
function depsActivatePill(attr, id) { document.querySelectorAll('['+attr+'="'+id+'"]').forEach(e => { e.classList.remove('dim'); e.classList.add('active'); }); }
function depsActivateConn(outId, epId) { document.querySelectorAll('[data-conn-out="'+outId+'"][data-conn-ep="'+epId+'"]').forEach(e => { e.classList.remove('dim'); e.classList.add('active'); }); }
function depsUnique(values) { return Array.from(new Set(values.filter(Boolean))); }
function depsCollectEpContext(nodeId) {
  const inboundCallIds = depsUnique(_epConnFrom[nodeId] || []);
  const upstreamEpIds = depsUnique(inboundCallIds.flatMap(cid => _callToEpIds[cid] || []));
  const downstreamCallIds = depsUnique(_epToCallIds[nodeId] || []);
  const downstreamEpIds = depsUnique(downstreamCallIds.map(cid => _outConnTo[cid]).filter(Boolean));
  return {
    anchorEpId: nodeId,
    inboundCallIds,
    upstreamEpIds,
    downstreamCallIds,
    downstreamEpIds
  };
}
function depsCollectOutContext(nodeId) {
  const resolvedEpId = _outConnTo[nodeId] || null;
  if (!resolvedEpId) {
    return {
      anchorEpId: null,
      inboundCallIds: [nodeId],
      upstreamEpIds: depsUnique(_callToEpIds[nodeId] || []),
      downstreamCallIds: [],
      downstreamEpIds: []
    };
  }
  const ctx = depsCollectEpContext(resolvedEpId);
  if (!ctx.inboundCallIds.includes(nodeId)) ctx.inboundCallIds = depsUnique([nodeId, ...ctx.inboundCallIds]);
  ctx.upstreamEpIds = depsUnique(ctx.inboundCallIds.flatMap(cid => _callToEpIds[cid] || []));
  return ctx;
}
function depsApplyContext(ctx) {
  if (ctx.anchorEpId) depsActivatePill('data-ep', ctx.anchorEpId);
  ctx.upstreamEpIds.forEach(epId => depsActivatePill('data-ep', epId));
  ctx.inboundCallIds.forEach(cid => {
    depsActivatePill('data-out', cid);
    const targetEpId = _outConnTo[cid];
    if (targetEpId) depsActivateConn(cid, targetEpId);
  });
  ctx.downstreamCallIds.forEach(cid => {
    depsActivatePill('data-out', cid);
    const targetEpId = _outConnTo[cid];
    if (targetEpId) {
      depsActivatePill('data-ep', targetEpId);
      depsActivateConn(cid, targetEpId);
    }
  });
}

function onEpClick(nodeId) {
  if (_depsActiveId === nodeId) { depsClearHighlights(); closeDepsPanel(); return; }
  depsClearHighlights(); _depsActiveId = nodeId; depsDimAll();
  const ctx = depsCollectEpContext(nodeId);
  depsApplyContext(ctx);
  showDepsPanel({
    title: 'Impact: '+_epById[nodeId].api+' · '+_epById[nodeId].httpMethod+' '+_epById[nodeId].route,
    anchorEp: _epById[nodeId],
    inboundCalls: ctx.inboundCallIds.map(id => _outById[id]).filter(Boolean),
    upstreamEps: ctx.upstreamEpIds.map(id => _epById[id]).filter(Boolean),
    downstreamCalls: ctx.downstreamCallIds.map(id => _outById[id]).filter(Boolean)
  });
}
function onOutClick(nodeId) {
  if (_depsActiveId === nodeId) { depsClearHighlights(); closeDepsPanel(); return; }
  depsClearHighlights(); _depsActiveId = nodeId; depsDimAll();
  const ctx = depsCollectOutContext(nodeId);
  depsApplyContext(ctx);
  if (!ctx.anchorEpId) {
    depsActivatePill('data-out', nodeId);
  }
  const out = _outById[nodeId];
  const anchorEp = ctx.anchorEpId ? _epById[ctx.anchorEpId] : null;
  showDepsPanel({
    title: anchorEp
      ? 'Impact: '+anchorEp.api+' · '+anchorEp.httpMethod+' '+anchorEp.route
      : 'Outbound: '+out.targetApi+' · '+out.targetRoute,
    anchorEp,
    inboundCalls: ctx.inboundCallIds.map(id => _outById[id]).filter(Boolean),
    upstreamEps: ctx.upstreamEpIds.map(id => _epById[id]).filter(Boolean),
    downstreamCalls: ctx.downstreamCallIds.map(id => _outById[id]).filter(Boolean),
    selectedOut: out
  });
}
function closeDepsPanel() { document.getElementById('deps-impact-panel').classList.remove('visible'); }
function escHtml(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
function renderTags(items, cls) { return items.map(t => '<span class="impact-tag '+cls+'">'+escHtml(t)+'</span>').join(''); }
function showDepsPanel(selection) {
  document.getElementById('deps-panel-title').textContent = selection.title;
  let html = '';

  if (selection.selectedOut) {
    html += '<div class="impact-section"><h3>Selected outbound call</h3>'+renderTags([selection.selectedOut.api+' → '+selection.selectedOut.targetApi+' '+selection.selectedOut.targetRoute],'out')+'</div>';
  }

  if (selection.inboundCalls.length) {
    const inboundTags = selection.inboundCalls.map(call => call.api+' → '+call.targetApi+' '+call.targetRoute);
    html += '<div class="impact-section"><h3>Entering from outbound calls</h3>'+renderTags(inboundTags,'out')+'</div>';
  } else if (selection.anchorEp) {
    html += '<div class="impact-section"><p class="impact-empty">No upstream outbound call resolves to this entry point.</p></div>';
  }

  if (selection.upstreamEps.length) {
    const upstreamTags = depsUnique(selection.upstreamEps.map(epInfo => epInfo.api+': '+epInfo.httpMethod+' '+epInfo.route));
    html += '<div class="impact-section"><h3>Reached from entry points</h3>'+renderTags(upstreamTags,'ep')+'</div>';
  }

  if (!selection.downstreamCalls.length) {
    html += '<div class="impact-section"><p class="impact-empty">No downstream API dependency is reachable from this entry point.</p></div>';
  } else {
    const byApi = {};
    selection.downstreamCalls.forEach(call => { (byApi[call.targetApi] = byApi[call.targetApi] || []).push(call.targetRoute); });
    html += Object.entries(byApi)
      .map(([api,routes]) => '<div class="impact-section"><h3>Next downstream dependencies on '+escHtml(api)+'</h3>'+renderTags(depsUnique(routes),'out')+'</div>')
      .join('');
  }

  document.getElementById('deps-panel-body').innerHTML = html;
  document.getElementById('deps-impact-panel').classList.add('visible');
}

// ══════════════════════════════════════════════════════════════════════════════
// View 2 – Code Graph  (D3 force simulation on canvas)
// ══════════════════════════════════════════════════════════════════════════════
const CG_COLORS = {
  Method:'#1f6feb', Class:'#2ea043', Interface:'#d29922', Struct:'#a371f7',
  Enum:'#3fb950', Property:'#58a6ff', Field:'#8b949e', Namespace:'#30363d',
  Project:'#f85149', NuGetPackage:'#e3b341', ExternalType:'#484f58', default:'#388bfd'
};

let _cgMode = 'force';
let _cgNodes = [], _cgEdges = [], _cgSim = null, _cgTransform = {x:0,y:0,k:1};
let _cgAllNodes = [], _cgAllEdges = [];
let _cgHierTree = {}, _cgHierState = {}, _cgHierLayout = {}, _cgHierRoots = [], _cgNodeById = {};
let _cgHierZoom = null;

function cgReset() {
  if (_cgSim) { _cgSim.stop(); _cgSim = null; }
  _cgNodes = []; _cgEdges = []; _cgAllNodes = []; _cgAllEdges = [];
  _cgHierTree = {}; _cgHierState = {}; _cgHierLayout = {}; _cgHierRoots = []; _cgNodeById = {};
  document.getElementById('cg-canvas').style.display    = 'none';
  document.getElementById('cg-hier-wrap').style.display = 'none';
  document.getElementById('cg-empty').style.display     = 'flex';
  document.getElementById('cg-detail').style.display    = 'none';
  document.getElementById('cg-status').textContent      = '';
  _cgMode = 'force';
  const modeEl = document.getElementById('cg-graph-mode'); if (modeEl) modeEl.value = 'force';
  ['cg-btn-h2','cg-btn-h3','cg-btn-hc'].forEach(id => { const el = document.getElementById(id); if (el) el.style.display = 'none'; });
}

// ══════════════════════════════════════════════════════════════════════════════
// Code Graph – mode switching
// ══════════════════════════════════════════════════════════════════════════════
function switchCgMode(mode) {
  _cgMode = mode;
  const canvas   = document.getElementById('cg-canvas');
  const hierWrap = document.getElementById('cg-hier-wrap');
  const hierBtns = ['cg-btn-h2','cg-btn-h3','cg-btn-hc'];
  if (mode === 'force') {
    canvas.style.display   = _cgNodes.length ? 'block' : 'none';
    hierWrap.style.display = 'none';
    hierBtns.forEach(id => { const el = document.getElementById(id); if (el) el.style.display = 'none'; });
    fitCgCanvas();
  } else {
    canvas.style.display   = 'none';
    hierBtns.forEach(id => { const el = document.getElementById(id); if (el) el.style.display = ''; });
    if (_cgAllNodes.length) cgRenderHierarchy();
    else { hierWrap.style.display = 'none'; }
  }
}

// ══════════════════════════════════════════════════════════════════════════════
// Code Graph – Hierarchy view
// ══════════════════════════════════════════════════════════════════════════════
const CG_H = { H:28, MW:160, P:8, G:4, RG:16 };
const CG_HIER_OPEN   = new Set(['Solution','Project']);
const CG_HIER_STRUCT = new Set(['Solution','Project','Namespace','Class','Interface','Struct','Enum','NuGetPackage','ExternalType']);
const CG_KIND_ORDER  = ['Solution','Project','Namespace','Class','Interface','Struct','Enum','Method','Property','Field','NuGetPackage','ExternalType'];

function cgBuildHierarchy() {
  _cgNodeById = Object.fromEntries(_cgAllNodes.map(n => [n.id, n]));
  const childMap = {}, parentMap = {};
  _cgAllEdges.forEach(e => {
    if (e.rel !== 'CONTAINS') return;
    (childMap[e.source] = childMap[e.source] || []).push(e.target);
    parentMap[e.target] = e.source;
  });
  _cgHierTree = {};
  _cgAllNodes.forEach(n => {
    _cgHierTree[n.id] = { id: n.id, children: (childMap[n.id] || []).slice(), parent: parentMap[n.id] || null };
  });
  const kr = k => { const i = CG_KIND_ORDER.indexOf(k); return i < 0 ? 99 : i; };
  Object.values(_cgHierTree).forEach(hn => {
    hn.children.sort((a, b) => {
      const na = _cgNodeById[a], nb = _cgNodeById[b];
      const d = kr(na?.kind) - kr(nb?.kind);
      return d !== 0 ? d : (na?.label || '').localeCompare(nb?.label || '');
    });
  });
  _cgHierRoots = _cgAllNodes
    .filter(n => !parentMap[n.id] && (CG_HIER_STRUCT.has(n.kind) || (childMap[n.id] || []).length > 0))
    .map(n => n.id);
  _cgHierRoots.sort((a, b) => {
    const na = _cgNodeById[a], nb = _cgNodeById[b];
    const d = kr(na?.kind) - kr(nb?.kind);
    return d !== 0 ? d : (na?.label || '').localeCompare(nb?.label || '');
  });
  _cgHierState = {};
  _cgAllNodes.forEach(n => { _cgHierState[n.id] = { collapsed: !CG_HIER_OPEN.has(n.kind) }; });
}

function cgHierVisKids(id) {
  return (_cgHierTree[id]?.children || []).filter(cid => _cgNodeById[cid]);
}

function cgHierSize(id) {
  const n = _cgNodeById[id];
  const label = n?.label || id;
  const baseW = Math.max(CG_H.MW, Math.min(label.length * 6.5 + 44, 280));
  const kids = cgHierVisKids(id);
  if ((_cgHierState[id]?.collapsed ?? true) || kids.length === 0) return { w: baseW, h: CG_H.H };
  const cs = kids.map(cid => cgHierSize(cid));
  const maxCW = cs.reduce((m, s) => Math.max(m, s.w), 0);
  const totCH = cs.reduce((s, c) => s + c.h, 0) + Math.max(0, kids.length - 1) * CG_H.G;
  return { w: Math.max(baseW, maxCW + CG_H.P * 2), h: CG_H.H + CG_H.P + totCH + CG_H.P };
}

function cgHierPos(id, x, y, w) {
  const lay = { x, y, w, h: CG_H.H }; _cgHierLayout[id] = lay;
  const kids = cgHierVisKids(id);
  if ((_cgHierState[id]?.collapsed ?? true) || kids.length === 0) return lay;
  const cw = Math.max(w - CG_H.P * 2, CG_H.MW);
  let cy = y + CG_H.H + CG_H.P;
  kids.forEach(cid => { const cl = cgHierPos(cid, x + CG_H.P, cy, cw); cy += cl.h + CG_H.G; });
  lay.h = cy - y - CG_H.G + CG_H.P; return lay;
}

function cgLayoutHierarchy() {
  _cgHierLayout = {};
  let x = CG_H.P;
  _cgHierRoots.forEach(id => { const sz = cgHierSize(id); cgHierPos(id, x, CG_H.P, sz.w); x += sz.w + CG_H.RG; });
}

const CGH_NS = 'http://www.w3.org/2000/svg';
function cgHEl(tag, attrs, txt) {
  const el = document.createElementNS(CGH_NS, tag);
  if (attrs) Object.entries(attrs).forEach(([k,v]) => el.setAttribute(k, String(v)));
  if (txt != null) el.textContent = txt;
  return el;
}

const CGH_EDGE_COLOR = {
  CALLS:'#475569', INHERITS:'#7c3aed', IMPLEMENTS:'#d97706',
  ACCESSES:'#334155', USES_ATTRIBUTE:'#8b5cf6', EXTERNAL_API_CALL:'#f85149'
};

function cgRenderHierarchy() {
  cgLayoutHierarchy();
  const svg = document.getElementById('cg-hier-svg');
  ['cg-hg-edges','cg-hg-boxes'].forEach(id => { const e = document.getElementById(id); if (e) e.remove(); });
  const tf = (svg._hierTf || d3.zoomIdentity).toString();
  const gEdges = cgHEl('g', { id:'cg-hg-edges', transform: tf });
  const gBoxes = cgHEl('g', { id:'cg-hg-boxes', transform: tf });
  svg.appendChild(gEdges); svg.appendChild(gBoxes);

  // Collect visible ancestors for edge collapse
  function visAnc(id) {
    let cur = id;
    while (cur) {
      if (_cgHierLayout[cur] && !(_cgHierState[cur]?.collapsed ?? true)) return cur;
      const p = _cgHierTree[cur]?.parent;
      if (!p) return cur;
      cur = p;
    }
    return id;
  }

  function drawBox(id) {
    const lay = _cgHierLayout[id]; if (!lay) return;
    const n = _cgNodeById[id];
    const color = CG_COLORS[n?.kind] || CG_COLORS.default;
    const kids = cgHierVisKids(id);
    const hasKids = kids.length > 0;
    const collapsed = _cgHierState[id]?.collapsed ?? true;
    const g = cgHEl('g', { class:'cg-h-box' }); g.dataset.id = id;
    if (!collapsed && hasKids) {
      g.appendChild(cgHEl('rect', { x:lay.x, y:lay.y, width:lay.w, height:lay.h, rx:6,
        fill:color+'0d', stroke:color+'33', 'stroke-width':'1' }));
    }
    g.appendChild(cgHEl('rect', { class:'cg-h-hdr', x:lay.x, y:lay.y, width:lay.w, height:CG_H.H,
      rx: (!collapsed && hasKids) ? 0 : 5, fill:color+'22', stroke:color+'88', 'stroke-width':'1' }));
    if (hasKids) {
      g.appendChild(cgHEl('text', { x:lay.x+9, y:lay.y+CG_H.H/2, 'dominant-baseline':'central',
        'font-size':'8', fill:color, 'font-family':'monospace', style:'pointer-events:none' }, collapsed?'▶':'▼'));
    }
    const ep = n?.ep === true;
    g.appendChild(cgHEl('text', { x:lay.x+(hasKids?22:10), y:lay.y+CG_H.H/2,
      'dominant-baseline':'central', 'font-size':'11', 'font-family':"'Segoe UI',system-ui,sans-serif",
      fill:ep?'#f0c040':'#c9d1d9', 'font-weight':ep?'700':'400', style:'pointer-events:none' }, n?.label || id));
    g.appendChild(cgHEl('text', { x:lay.x+lay.w-6, y:lay.y+CG_H.H/2,
      'dominant-baseline':'central', 'text-anchor':'end', 'font-size':'9',
      fill:color+'88', 'font-family':"'Segoe UI',system-ui,sans-serif", style:'pointer-events:none' }, n?.kind || ''));
    g.addEventListener('click', ev => {
      ev.stopPropagation();
      if (hasKids) { _cgHierState[id].collapsed = !_cgHierState[id].collapsed; cgRenderHierarchy(); }
      else if (n) showCgDetail(n);
    });
    gBoxes.appendChild(g);
    if (!collapsed) kids.forEach(cid => drawBox(cid));
  }
  _cgHierRoots.forEach(id => drawBox(id));

  // Non-CONTAINS edges (terminated at visible ancestors)
  const drawn = new Set();
  _cgAllEdges.forEach(e => {
    if (e.rel === 'CONTAINS') return;
    const src = visAnc(e.source), tgt = visAnc(e.target);
    if (src === tgt || !_cgHierLayout[src] || !_cgHierLayout[tgt]) return;
    const key = src + '\u21d2' + tgt;
    if (drawn.has(key)) return; drawn.add(key);
    const sL = _cgHierLayout[src], tL = _cgHierLayout[tgt];
    const ec = CGH_EDGE_COLOR[e.rel] || '#30363d';
    const goRight = (sL.x + sL.w / 2) < (tL.x + tL.w / 2);
    const x1 = goRight ? sL.x + sL.w : sL.x, y1 = sL.y + CG_H.H / 2;
    const x2 = goRight ? tL.x : tL.x + tL.w, y2 = tL.y + CG_H.H / 2;
    const dx = Math.max(Math.abs(x2 - x1) * 0.45, 24);
    gEdges.appendChild(cgHEl('path', {
      d:`M${x1},${y1} C${goRight?x1+dx:x1-dx},${y1} ${goRight?x2-dx:x2+dx},${y2} ${x2},${y2}`,
      stroke:ec, 'stroke-width':'1', fill:'none', 'stroke-opacity':'0.4' }));
  });

  // Zoom setup (once, reuse on re-renders)
  if (!_cgHierZoom) {
    svg._hierTf = d3.zoomIdentity;
    _cgHierZoom = d3.zoom().scaleExtent([0.03, 8]).on('zoom', ({ transform }) => {
      svg._hierTf = transform;
      document.getElementById('cg-hg-edges')?.setAttribute('transform', transform);
      document.getElementById('cg-hg-boxes')?.setAttribute('transform', transform);
    });
    d3.select('#cg-hier-svg').call(_cgHierZoom).on('dblclick.zoom', null);
  }
  document.getElementById('cg-hier-wrap').style.display = 'block';
  document.getElementById('cg-empty').style.display = 'none';
}

function cgHierExpandAll(depth) {
  function walk(id, d) {
    _cgHierState[id] = { collapsed: d >= depth };
    if (d < depth) cgHierVisKids(id).forEach(cid => walk(cid, d + 1));
  }
  _cgHierRoots.forEach(id => walk(id, 0));
  cgRenderHierarchy();
}

function cgHierCollapseAll() {
  Object.values(_cgHierState).forEach(s => s.collapsed = true);
  _cgHierRoots.forEach(id => { if (_cgHierState[id]) _cgHierState[id].collapsed = false; });
  cgRenderHierarchy();
}

async function loadCodeGraph(apiName) {
  if (!apiName || !_driver) return;
  cgReset();
  document.getElementById('cg-status').textContent = 'Loading…';
  document.getElementById('cg-empty').textContent  = 'Loading code graph for ' + apiName + '…';
  document.getElementById('cg-empty').style.display = 'flex';

  try {
    const nodeRecs = await runQuery(_driver,
      'MATCH (n:CodeNode {apiName:$api}) RETURN n.id AS id, n.label AS label, n.kind AS kind, n.isEntryPoint AS ep LIMIT 2000',
      { api: apiName });
    const edgeRecs = await runQuery(_driver,
      'MATCH (s:CodeNode {apiName:$api})-[r]->(t:CodeNode {apiName:$api}) RETURN s.id AS src, t.id AS tgt, type(r) AS rel LIMIT 10000',
      { api: apiName });
    const metaRecs = await runQuery(_driver,
      'MATCH (n:CodeNode {apiName:$api}) RETURN n.id AS id, n.filePath AS fp, n.lineStart AS ln, n.fullName AS fn LIMIT 2000',
      { api: apiName });

    const metaById = {};
    metaRecs.forEach(r => { metaById[r.get('id')] = { filePath: r.get('fp'), lineStart: r.get('ln'), fullName: r.get('fn') }; });

    _cgAllNodes = nodeRecs.map(r => ({
      id:      r.get('id'),
      label:   r.get('label'),
      kind:    r.get('kind') || 'default',
      ep:      r.get('ep') === 'True' || r.get('ep') === true,
      meta:    metaById[r.get('id')] || {}
    }));
    _cgAllEdges = edgeRecs.map(r => ({
      source: r.get('src'),
      target: r.get('tgt'),
      rel:    r.get('rel')
    }));

    document.getElementById('cg-status').textContent =
      _cgAllNodes.length + ' nodes · ' + _cgAllEdges.length + ' edges';

    applyCodeGraphFilter();
    cgBuildHierarchy();
    if (_cgMode === 'hierarchy') cgRenderHierarchy();
  } catch (err) {
    document.getElementById('cg-status').textContent = '✕ ' + err.message;
    document.getElementById('cg-empty').textContent  = 'Query failed: ' + err.message;
  }
}

function applyCodeGraphFilter() {
  const kindFilter   = document.getElementById('cg-kind-filter').value;
  const searchFilter = document.getElementById('cg-search').value.trim().toLowerCase();

  _cgNodes = _cgAllNodes.filter(n =>
    (!kindFilter   || n.kind  === kindFilter) &&
    (!searchFilter || n.label.toLowerCase().includes(searchFilter))
  );
  const visIds = new Set(_cgNodes.map(n => n.id));
  _cgEdges = _cgAllEdges.filter(e => visIds.has(e.source) && visIds.has(e.target));

  if (_cgNodes.length === 0) {
    document.getElementById('cg-canvas').style.display = 'none';
    document.getElementById('cg-empty').style.display  = 'flex';
    document.getElementById('cg-empty').textContent    = 'No nodes match the current filter.';
    return;
  }
  renderCodeGraph();
}

function fitCgCanvas() {
  const canvas = document.getElementById('cg-canvas');
  const wrap   = document.getElementById('view-code-graph');
  canvas.width  = wrap.clientWidth  || window.innerWidth;
  canvas.height = wrap.clientHeight - (document.getElementById('cg-toolbar').offsetHeight || 40);
}

function renderCodeGraph() {
  fitCgCanvas();
  const canvas = document.getElementById('cg-canvas');
  canvas.style.display = 'block';
  document.getElementById('cg-empty').style.display = 'none';

  const w = canvas.width, h = canvas.height;
  const ctx = canvas.getContext('2d');

  // Build node objects for D3 (copy x/y from existing simulation if available)
  const nodeById = {};
  const simNodes = _cgNodes.map(n => {
    const existing = _cgSim ? _cgSim.nodes().find(s => s.id === n.id) : null;
    const node = { id:n.id, label:n.label, kind:n.kind, ep:n.ep, meta:n.meta,
                   x: existing ? existing.x : (Math.random()-0.5)*w*0.5 + w/2,
                   y: existing ? existing.y : (Math.random()-0.5)*h*0.5 + h/2 };
    nodeById[n.id] = node;
    return node;
  });
  const simEdges = _cgEdges
    .filter(e => nodeById[e.source] && nodeById[e.target])
    .map(e => ({ source: nodeById[e.source], target: nodeById[e.target], rel: e.rel }));

  if (_cgSim) _cgSim.stop();
  _cgSim = d3.forceSimulation(simNodes)
    .force('link',   d3.forceLink(simEdges).id(d => d.id).distance(60))
    .force('charge', d3.forceManyBody().strength(-150))
    .force('center', d3.forceCenter(w / 2, h / 2))
    .force('collide', d3.forceCollide(8))
    .alphaDecay(0.03);

  let _tf = { ..._cgTransform };

  function draw() {
    ctx.save();
    ctx.clearRect(0, 0, w, h);
    ctx.translate(_tf.x, _tf.y);
    ctx.scale(_tf.k, _tf.k);

    // Edges
    ctx.lineWidth = 0.6;
    ctx.strokeStyle = '#21262d';
    simEdges.forEach(e => {
      if (!e.source.x) return;
      ctx.beginPath();
      ctx.moveTo(e.source.x, e.source.y);
      ctx.lineTo(e.target.x, e.target.y);
      ctx.stroke();
    });

    // Nodes
    simNodes.forEach(n => {
      if (!n.x) return;
      const r = n.ep ? 7 : 5;
      ctx.beginPath();
      ctx.arc(n.x, n.y, r, 0, Math.PI * 2);
      ctx.fillStyle   = CG_COLORS[n.kind] || CG_COLORS.default;
      ctx.fill();
      if (n.ep) { ctx.strokeStyle = '#f0f6fc'; ctx.lineWidth = 1.5; ctx.stroke(); }

      // Labels when zoomed in
      if (_tf.k > 0.6) {
        ctx.fillStyle   = '#c9d1d9';
        ctx.font        = '10px Segoe UI,system-ui,sans-serif';
        ctx.textBaseline = 'middle';
        ctx.fillText(n.label, n.x + r + 3, n.y);
      }
    });
    ctx.restore();
  }

  _cgSim.on('tick', draw);
  _cgSim.on('end',  draw);

  // Pan + zoom
  const newCanvas = canvas.cloneNode(false);
  Object.assign(newCanvas, { width: w, height: h });
  canvas.parentNode.replaceChild(newCanvas, canvas);
  newCanvas.style.display = 'block';
  newCanvas.id = 'cg-canvas';

  const ctx2 = newCanvas.getContext('2d');
  _cgSim.on('tick', () => { ctx2.clearRect(0,0,w,h); drawOn(ctx2, simNodes, simEdges, _tf, w, h); });
  _cgSim.on('end',  () => { ctx2.clearRect(0,0,w,h); drawOn(ctx2, simNodes, simEdges, _tf, w, h); });

  function drawOn(c, nodes, edges, tf, cw, ch) {
    c.save();
    c.clearRect(0, 0, cw, ch);
    c.translate(tf.x, tf.y);
    c.scale(tf.k, tf.k);
    c.lineWidth = 0.6; c.strokeStyle = '#30363d';
    edges.forEach(e => { if (!e.source.x) return; c.beginPath(); c.moveTo(e.source.x, e.source.y); c.lineTo(e.target.x, e.target.y); c.stroke(); });
    nodes.forEach(n => {
      if (!n.x) return;
      const r = n.ep ? 7 : 5;
      c.beginPath(); c.arc(n.x, n.y, r, 0, Math.PI * 2);
      c.fillStyle = CG_COLORS[n.kind] || CG_COLORS.default; c.fill();
      if (n.ep) { c.strokeStyle = '#f0f6fc'; c.lineWidth = 1.5; c.stroke(); c.lineWidth = 0.6; }
      if (tf.k > 0.6) { c.fillStyle='#c9d1d9'; c.font='10px Segoe UI,sans-serif'; c.textBaseline='middle'; c.fillText(n.label, n.x+r+3, n.y); }
    });
    c.restore();
  }

  // Drag
  let dragNode = null, dragging = false, dragStart = {};
  newCanvas.addEventListener('mousedown', e => {
    const pt = canvasPt(e, newCanvas, _tf);
    dragNode = simNodes.find(n => Math.hypot(n.x - pt.x, n.y - pt.y) < 10);
    if (dragNode) { dragNode.fx = dragNode.x; dragNode.fy = dragNode.y; _cgSim.alphaTarget(0.1).restart(); dragging = true; newCanvas.classList.add('dragging'); }
    else { dragging = true; newCanvas.classList.add('dragging'); dragStart = { x: e.clientX - _tf.x, y: e.clientY - _tf.y }; }
  });
  window.addEventListener('mousemove', e => {
    if (!dragging) return;
    if (dragNode) { const pt = canvasPt(e, newCanvas, _tf); dragNode.fx = pt.x; dragNode.fy = pt.y; }
    else { _tf = { ..._tf, x: e.clientX - dragStart.x, y: e.clientY - dragStart.y }; drawOn(ctx2, simNodes, simEdges, _tf, w, h); }
  });
  window.addEventListener('mouseup', () => {
    if (dragNode) { dragNode.fx = null; dragNode.fy = null; _cgSim.alphaTarget(0); dragNode = null; }
    dragging = false; newCanvas.classList.remove('dragging');
  });
  newCanvas.addEventListener('wheel', e => {
    e.preventDefault();
    const rect = newCanvas.getBoundingClientRect();
    const mx = e.clientX - rect.left, my = e.clientY - rect.top, os = _tf.k;
    const nk = Math.min(4, Math.max(0.1, _tf.k * (e.deltaY < 0 ? 1.1 : 0.9)));
    _tf = { x: mx - (mx - _tf.x) * (nk / os), y: my - (my - _tf.y) * (nk / os), k: nk };
    drawOn(ctx2, simNodes, simEdges, _tf, w, h);
  }, { passive: false });
  newCanvas.addEventListener('click', e => {
    if (dragging) return;
    const pt = canvasPt(e, newCanvas, _tf);
    const hit = simNodes.find(n => Math.hypot(n.x - pt.x, n.y - pt.y) < 10);
    if (hit) showCgDetail(hit);
    else document.getElementById('cg-detail').style.display = 'none';
  });
}

function canvasPt(e, canvas, tf) {
  const r = canvas.getBoundingClientRect();
  return { x: (e.clientX - r.left - tf.x) / tf.k, y: (e.clientY - r.top - tf.y) / tf.k };
}

function showCgDetail(node) {
  document.getElementById('cg-detail-label').textContent = node.label;
  const rows = [
    ['Kind',      node.kind],
    ['Entry Pt',  node.ep ? 'Yes' : 'No'],
    ['File',      node.meta.filePath || '—'],
    ['Line',      node.meta.lineStart || '—'],
    ['Full name', node.meta.fullName  || '—'],
  ];
  document.getElementById('cg-detail-meta').innerHTML =
    rows.map(([k,v]) => '<div class="meta-row">'+escHtml(k)+': <span>'+escHtml(String(v||'—'))+'</span></div>').join('');
  document.getElementById('cg-detail').style.display = 'block';
}

// ══════════════════════════════════════════════════════════════════════════════
// View 3 – Impact Explorer
// ══════════════════════════════════════════════════════════════════════════════
const IMP_MODES = {
  file:   { label:'File path fragment',   placeholder:'e.g. WeatherController.cs',  hint: null },
  fn:     { label:'Function name fragment', placeholder:'e.g. GetWeatherForecast',    hint: null },
  commit: { label:'Commit SHA',            placeholder:'e.g. a1b2c3d',              hint: 'commit' },
  range:  { label:'Commit range',          placeholder:'e.g. abc123..def456',        hint: 'range' },
};

function updateImpactMode(mode) {
  const cfg = IMP_MODES[mode];
  document.getElementById('imp-input-label').textContent = cfg.label;
  document.getElementById('imp-input').placeholder = cfg.placeholder;
  const hint = document.getElementById('imp-hint');
  if (cfg.hint === 'commit') {
    hint.style.display = '';
    hint.innerHTML = '<strong>Commit resolution requires the CLI.</strong> Run:<br/>' +
      '<code>dotnet-graph-scanner impact --commit &lt;sha&gt; --repo &lt;path&gt;</code><br/>' +
      'Or enter a file path fragment manually using the <em>File path</em> mode.';
  } else if (cfg.hint === 'range') {
    hint.style.display = '';
    hint.innerHTML = '<strong>Commit range resolution requires the CLI.</strong> Run:<br/>' +
      '<code>dotnet-graph-scanner impact --commit-range &lt;from&gt;..&lt;to&gt; --repo &lt;path&gt;</code><br/>' +
      'Or enter a file path fragment manually using the <em>File path</em> mode.';
  } else {
    hint.style.display = 'none';
  }
}

async function runImpactQuery() {
  if (!_driver) { alert('Not connected. Connect to the database first.'); return; }
  const mode      = document.getElementById('imp-mode').value;
  const input     = document.getElementById('imp-input').value.trim();
  // '' means All APIs – pass null so the WHERE clause is skipped entirely
  const apiFilter = document.getElementById('imp-api-select').value || null;

  if (mode === 'commit' || mode === 'range') {
    updateImpactMode(mode);
    return;
  }
  if (!input) { alert('Please enter a search term.'); return; }

  const files = mode === 'file' ? [input] : [];
  const fns   = mode === 'fn'   ? [input] : [];

  document.getElementById('imp-empty').textContent = 'Searching…';
  document.getElementById('imp-empty').style.display = 'block';
  document.getElementById('imp-results').style.display = 'none';

  try {
    // Uses the same CodeNode dataset as the Code Graph view.
    // Entry-point methods carry httpMethod + routeTemplate in their properties
    // (written by WriteCodeNodesAsync from EntryPointDetector's Meta values).
    //
    // Traversal follows CALLS, ACCESSES, and USES_ATTRIBUTE so that changes to
    // model/DTO files surface the controllers that use them.
    // filePath is stored on all owned nodes (Class, Method, Property, Field),
    // so direct filtering by filePath is sufficient — no ancestor traversal needed.
    // All file comparisons are case-insensitive via toLower().
    const recs = await runQuery(_driver, `
      MATCH (ep:CodeNode {isEntryPoint: 'True'})
      WHERE $apiName IS NULL OR ep.apiName = $apiName
      MATCH (ep)-[:CALLS|ACCESSES|USES_ATTRIBUTE*0..20]->(reached:CodeNode)
      WHERE (size($files) > 0
             AND reached.filePath IS NOT NULL
             AND any(fp IN $files WHERE toLower(reached.filePath) CONTAINS toLower(fp)))
         OR (size($fns) > 0
             AND reached.kind = 'Method'
             AND any(fn IN $fns WHERE toLower(reached.label) CONTAINS toLower(fn)))
      RETURN DISTINCT ep.apiName       AS apiName,
                      ep.httpMethod    AS httpMethod,
                      ep.routeTemplate AS route,
                      ep.label         AS lbl
      ORDER BY ep.apiName, ep.routeTemplate`,
      { files, fns, apiName: apiFilter });

    renderImpactResults(recs);
  } catch (err) {
    console.error('[Impact] query failed:', err);
    const el = document.getElementById('imp-empty');
    el.innerHTML = '<span style="color:#f85149;font-weight:600">✕ Query failed:</span> ' + escHtml(err.message);
    el.style.display = 'block';
    document.getElementById('imp-results').style.display = 'none';
  }
}

function renderImpactResults(recs) {
  const tbody = document.getElementById('imp-tbody');
  tbody.innerHTML = '';
  if (!recs.length) {
    document.getElementById('imp-empty').textContent = 'No affected endpoints found.';
    document.getElementById('imp-empty').style.display = 'block';
    document.getElementById('imp-results').style.display = 'none';
    return;
  }
  recs.forEach(r => {
    const tr = document.createElement('tr');
    tr.innerHTML =
      '<td>'+escHtml(r.get('apiName'))+'</td>' +
      '<td><span class="imp-method">'+escHtml(r.get('httpMethod'))+'</span></td>' +
      '<td>'+escHtml(r.get('route'))+'</td>' +
      '<td style="color:#8b949e">'+escHtml(r.get('lbl'))+'</td>';
    tbody.appendChild(tr);
  });
  document.getElementById('imp-empty').style.display = 'none';
  document.getElementById('imp-results').style.display = 'table';
}

// Initialise hint visibility
updateImpactMode('file');
</script>
</body>
</html>
""";
}
