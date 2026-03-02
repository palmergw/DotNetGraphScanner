using System.Text;

namespace DotNetGraphScanner.Export;

/// <summary>
/// Generates a self-contained HTML page that connects to a Neo4j-compatible
/// database at runtime and renders the cross-API dependency map live.
/// No data is embedded in the page — every load/refresh fetches the latest
/// state from the database.
/// </summary>
public sealed class CrossApiLiveHtmlExporter
{
    private readonly string _boltUrl;
    private readonly string _defaultUser;

    public CrossApiLiveHtmlExporter(
        string boltUrl   = "bolt://127.0.0.1:7687",
        string defaultUser = "neo4j")
    {
        _boltUrl     = boltUrl;
        _defaultUser = defaultUser;
    }

    public Task ExportAsync(string outputPath, CancellationToken ct = default)
    {
        var title = Path.GetFileNameWithoutExtension(outputPath);
        var html  = BuildHtml(title, _boltUrl, _defaultUser);
        File.WriteAllText(outputPath, html, Encoding.UTF8);
        Console.WriteLine($"  Cross-API Live → {outputPath}");
        return Task.CompletedTask;
    }

    private static string BuildHtml(string title, string boltUrl, string defaultUser) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<title>{{title}} – Cross-API Live Map</title>
<script src="https://cdn.jsdelivr.net/npm/neo4j-driver@5/lib/browser/neo4j-web.min.js"></script>
<style>
*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0d1117; color: #c9d1d9; height: 100vh; display: flex; flex-direction: column; overflow: hidden; }

/* ── Connection bar ───────────────────────────────────────────── */
#conn-bar { background: #161b22; border-bottom: 1px solid #21262d; padding: 10px 20px; display: flex; align-items: center; gap: 10px; flex-shrink: 0; flex-wrap: wrap; }
#conn-bar label { font-size: 12px; color: #8b949e; white-space: nowrap; }
#conn-bar input { background: #0d1117; border: 1px solid #30363d; border-radius: 6px; color: #c9d1d9; font-size: 12px; padding: 5px 9px; width: 220px; }
#conn-bar input:focus { outline: none; border-color: #388bfd; }
#conn-bar input[type=password] { width: 120px; letter-spacing: 2px; }
.conn-btn { padding: 5px 14px; border-radius: 6px; border: none; font-size: 12px; font-weight: 600; cursor: pointer; }
#btn-connect  { background: #238636; color: #fff; }
#btn-connect:hover  { background: #2ea043; }
#btn-refresh  { background: #1f6feb; color: #fff; display: none; }
#btn-refresh:hover  { background: #388bfd; }
#btn-clear    { background: #21262d; color: #8b949e; border: 1px solid #30363d; display: none; }
#btn-clear:hover { background: #30363d; color: #c9d1d9; }
#conn-status { font-size: 12px; margin-left: auto; white-space: nowrap; }
#conn-status.ok  { color: #3fb950; }
#conn-status.err { color: #f85149; }
#conn-status.loading { color: #e3b341; }

/* ── Header ──────────────────────────────────────────────────── */
#header { padding: 11px 24px; border-bottom: 1px solid #21262d; display: flex; align-items: center; gap: 16px; flex-shrink: 0; }
#header h1 { font-size: 15px; font-weight: 700; color: #f0f6fc; }
#header .subtitle { font-size: 11px; color: #8b949e; }
#header .legend { display: flex; align-items: center; gap: 16px; margin-left: auto; font-size: 12px; }
#header .legend-item { display: flex; align-items: center; gap: 6px; }
.pill-ep-sample  { width: 12px; height: 12px; border-radius: 3px; background: #0f291e; border: 1px solid #238636; }
.pill-out-sample { width: 12px; height: 12px; border-radius: 3px; background: #2d1c00; border: 1px solid #d29922; }

/* ── Empty / loading state ───────────────────────────────────── */
#empty-state { flex: 1; display: flex; align-items: center; justify-content: center; }
#empty-card  { text-align: center; max-width: 400px; }
#empty-card h2 { font-size: 16px; font-weight: 600; color: #8b949e; margin-bottom: 8px; }
#empty-card p  { font-size: 13px; color: #484f58; }
@keyframes spin { to { transform: rotate(360deg); } }
#loading-spinner { width: 32px; height: 32px; border: 3px solid #21262d; border-top-color: #1f6feb; border-radius: 50%; animation: spin .7s linear infinite; margin: 0 auto 14px; display: none; }
#loading-spinner.visible { display: block; }

/* ── Canvas ──────────────────────────────────────────────────── */
#canvas-wrap { flex: 1; overflow: hidden; position: relative; padding: 40px; display: none; cursor: grab; user-select: none; }
#canvas-wrap.dragging { cursor: grabbing; }
#canvas { display: block; pointer-events: none; }
#canvas * { pointer-events: auto; }

/* ── API card ────────────────────────────────────────────────── */
.api-card { fill: #161b22; stroke: #30363d; stroke-width: 1; rx: 10; ry: 10; }
.api-card.dim { opacity: 0.35; }
.api-name { fill: #f0f6fc; font-size: 14px; font-weight: 700; user-select: none; }
.api-name.dim { opacity: 0.35; }
.col-label { fill: #8b949e; font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: .06em; user-select: none; }
.api-scanned { fill: #484f58; font-size: 9px; user-select: none; }

/* ── Pills ───────────────────────────────────────────────────── */
.pill-ep { fill: #0f291e; stroke: #238636; stroke-width: 1; rx: 5; cursor: pointer; }
.pill-ep:hover { fill: #1a4731; }
.pill-ep.active  { fill: #1a4731; stroke: #3fb950; stroke-width: 2; }
.pill-ep.dim     { opacity: 0.25; }
.pill-ep-text { fill: #3fb950; font-size: 11px; font-weight: 600; pointer-events: none; user-select: none; }
.pill-ep-text.dim { opacity: 0.25; }
.pill-ep-badge { fill: #238636; rx: 3; pointer-events: none; }
.pill-ep-badge.dim { opacity: 0.25; }
.pill-ep-badge-text { fill: #f0f6fc; font-size: 9px; font-weight: 700; pointer-events: none; user-select: none; }
.pill-ep-badge-text.dim { opacity: 0.25; }

.pill-out { fill: #2d1c00; stroke: #d29922; stroke-width: 1; rx: 5; cursor: pointer; }
.pill-out:hover { fill: #3d2600; }
.pill-out.active { fill: #3d2600; stroke: #f97316; stroke-width: 2; }
.pill-out.dim    { opacity: 0.25; }
.pill-out-text { fill: #e3b341; font-size: 11px; font-weight: 600; pointer-events: none; user-select: none; }
.pill-out-text.dim { opacity: 0.25; }
.pill-out-arrow { fill: #d29922; pointer-events: none; }
.pill-out-arrow.dim { opacity: 0.25; }

/* ── Connectors ──────────────────────────────────────────────── */
.connector { fill: none; stroke: #30363d; stroke-width: 1.5; opacity: 0.9; stroke-dasharray: 5 3; }
.connector.active { stroke: #58a6ff; stroke-width: 2; opacity: 1; stroke-dasharray: none; }
.connector.dim    { stroke: #30363d; opacity: 0.15; stroke-dasharray: 5 3; }
.connector-dot { fill: #30363d; }
.connector-dot.active { fill: #58a6ff; }
.connector-dot.dim { opacity: 0.15; }
.card-divider { stroke: #21262d; stroke-width: 1; }

/* ── Impact panel ────────────────────────────────────────────── */
#impact-panel { position: fixed; bottom: 0; left: 0; right: 0; background: #161b22; border-top: 1px solid #30363d; padding: 14px 24px; transition: transform .2s; transform: translateY(100%); max-height: 220px; overflow-y: auto; z-index: 100; }
#impact-panel.visible { transform: translateY(0); }
#impact-panel h2 { font-size: 13px; font-weight: 700; margin-bottom: 10px; color: #f0f6fc; }
#impact-panel .close-btn { position: absolute; top: 14px; right: 24px; background: none; border: none; color: #8b949e; cursor: pointer; font-size: 18px; line-height: 1; }
#impact-panel .close-btn:hover { color: #f0f6fc; }
.impact-section { margin-bottom: 10px; }
.impact-section h3 { font-size: 11px; font-weight: 600; color: #8b949e; text-transform: uppercase; letter-spacing: .06em; margin-bottom: 6px; }
.impact-tag { display: inline-block; margin: 2px 4px; padding: 3px 10px; border-radius: 12px; font-size: 11px; font-weight: 600; }
.impact-tag.ep  { background: #0f291e; border: 1px solid #238636; color: #3fb950; }
.impact-tag.out { background: #2d1c00; border: 1px solid #d29922; color: #e3b341; }
.impact-empty { color: #8b949e; font-size: 12px; font-style: italic; }
</style>
</head>
<body>

<!-- Connection bar -->
<div id="conn-bar">
  <label>Bolt URL</label>
  <input type="text"     id="inp-url"  value="{{boltUrl}}"/>
  <label>User</label>
  <input type="text"     id="inp-user" value="{{defaultUser}}" style="width:90px"/>
  <label>Password</label>
  <input type="password" id="inp-pass" value="" placeholder="(none)"/>
  <button class="conn-btn" id="btn-connect" onclick="doConnect()">Connect</button>
  <button class="conn-btn" id="btn-refresh" onclick="doRefresh()">Refresh</button>
  <button class="conn-btn" id="btn-clear"   onclick="doClear()">Disconnect</button>
  <span id="conn-status"></span>
</div>

<div id="header">
  <h1>{{title}} – Cross-API Live Map</h1>
  <span class="subtitle" id="api-summary">Not connected</span>
  <div class="legend">
    <div class="legend-item"><div class="pill-ep-sample"></div> Entry point (inbound)</div>
    <div class="legend-item"><div class="pill-out-sample"></div> Outbound API call</div>
  </div>
</div>

<div id="empty-state">
  <div id="empty-card">
    <div id="loading-spinner"></div>
    <h2 id="empty-title">Enter connection details above</h2>
    <p id="empty-sub">Connect to a Neo4j-compatible database that contains scan results pushed with <code>scan --push</code>.</p>
  </div>
</div>

<div id="canvas-wrap">
  <svg id="canvas" xmlns="http://www.w3.org/2000/svg"></svg>
</div>

<div id="impact-panel">
  <button class="close-btn" onclick="closePanel()">✕</button>
  <h2 id="panel-title"></h2>
  <div id="panel-body"></div>
</div>

<script>
'use strict';

// ── Driver state ────────────────────────────────────────────────────────────
let _driver = null;

function setStatus(msg, cls = '') {
  const el = document.getElementById('conn-status');
  el.textContent = msg;
  el.className = cls;
}

async function doConnect() {
  const url  = document.getElementById('inp-url').value.trim();
  const user = document.getElementById('inp-user').value.trim();
  const pass = document.getElementById('inp-pass').value;

  if (_driver) { try { await _driver.close(); } catch {} _driver = null; }

  setStatus('Connecting…', 'loading');
  showSpinner(true);
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
    showSpinner(false);
    showEmpty('Connection failed', err.message);
  }
}

async function doClear() {
  if (_driver) { try { await _driver.close(); } catch {} _driver = null; }
  setStatus('', '');
  document.getElementById('btn-refresh').style.display = 'none';
  document.getElementById('btn-clear').style.display   = 'none';
  document.getElementById('btn-connect').textContent   = 'Connect';
  clearCanvas();
  showEmpty('Disconnected', 'Enter connection details and click Connect.');
  document.getElementById('api-summary').textContent = 'Not connected';
  closePanel();
}

async function doRefresh() {
  if (!_driver) return;
  setStatus('Refreshing…', 'loading');
  showSpinner(true);
  closePanel();
  try {
    const data = await loadData(_driver);
    setStatus('● Connected', 'ok');
    if (data.apis.length === 0) {
      showSpinner(false);
      document.getElementById('api-summary').textContent = 'No data';
      showEmpty('No API data found', 'Run: dotnet run -- scan <project.csproj> --output <dir> --push');
      return;
    }
    document.getElementById('api-summary').textContent =
      data.apis.length + ' API' + (data.apis.length !== 1 ? 's' : '') +
      ' · ' + data.apis.reduce((s, a) => s + a.entryPoints.length, 0) + ' entry points' +
      ' · ' + data.connections.filter(c => c.matchedEntrypointNodeId).length + ' connections resolved';
    initRenderer(data);
  } catch (err) {
    setStatus('✕ ' + err.message, 'err');
    showEmpty('Query failed', err.message);
    showSpinner(false);
  }
}

// ── Neo4j data loading ──────────────────────────────────────────────────────

// Memgraph keeps an implicit transaction open for the lifetime of a session,
// so each query must run in its own session to avoid the "open transaction" error.
// Do NOT specify a database name — Memgraph has no named-database concept.
async function runQuery(driver, cypher) {
  const session = driver.session();
  try   { return (await session.run(cypher)).records; }
  finally { await session.close(); }
}

async function loadData(driver) {
  const apisRecs  = await runQuery(driver, 'MATCH (a:Api) RETURN a.name AS name, a.scannedAt AS scannedAt ORDER BY a.name');
  const epsRecs   = await runQuery(driver, 'MATCH (a:Api)-[:HAS_ENTRY_POINT]->(e:EntryPoint) RETURN a.name AS apiName, e.nodeId AS nodeId, e.httpMethod AS httpMethod, e.route AS route, e.label AS label');
  const outsRecs  = await runQuery(driver, 'MATCH (a:Api)-[:HAS_OUTBOUND_CALL]->(o:OutboundCall) RETURN a.name AS apiName, o.nodeId AS nodeId, o.targetApi AS targetApi, o.targetRoute AS targetRoute, o.label AS label');
  const impRecs   = await runQuery(driver, 'MATCH (e:EntryPoint)-[:CAN_REACH]->(o:OutboundCall) RETURN e.nodeId AS epId, o.nodeId AS callId');
  const connRecs  = await runQuery(driver, 'MATCH (o:OutboundCall)-[:RESOLVES_TO]->(e:EntryPoint) RETURN o.nodeId AS outId, e.nodeId AS epId');

  const apiNames = apisRecs.map(r => r.get('name'));

  // Group EPs and outbound calls by API
  const epsByApi  = {}, outsByApi = {};
  apiNames.forEach(n => { epsByApi[n] = []; outsByApi[n] = []; });
  epsRecs.forEach(r => {
    const api = r.get('apiName');
    if (!epsByApi[api]) epsByApi[api] = [];
    epsByApi[api].push({ nodeId: r.get('nodeId'), httpMethod: r.get('httpMethod'), route: r.get('route'), label: r.get('label') });
  });
  outsRecs.forEach(r => {
    const api = r.get('apiName');
    if (!outsByApi[api]) outsByApi[api] = [];
    outsByApi[api].push({ nodeId: r.get('nodeId'), targetApi: r.get('targetApi'), targetRoute: r.get('targetRoute'), label: r.get('label') });
  });

  // Impact map: group callIds by epId
  const impMap = {};
  impRecs.forEach(r => {
    const epId = r.get('epId'), callId = r.get('callId');
    (impMap[epId] = impMap[epId] || []).push(callId);
  });
  const impacts = Object.entries(impMap).map(([epId, callIds]) => ({ entrypointNodeId: epId, callNodeIds: callIds }));

  // Connections
  const connections = connRecs.map(r => ({
    outboundCallNodeId:      r.get('outId'),
    matchedEntrypointNodeId: r.get('epId')
  }));

  return {
    title: 'Cross-API Live Map',
    apis: apiNames.map(name => ({
      name,
      scannedAt: (apisRecs.find(r => r.get('name') === name) || null)?.get('scannedAt') ?? null,
      entryPoints:   epsByApi[name]  || [],
      outboundCalls: outsByApi[name] || [],
    })),
    impacts,
    connections,
  };
}

// ── UI helpers ──────────────────────────────────────────────────────────────
function showSpinner(on) {
  document.getElementById('loading-spinner').classList.toggle('visible', on);
}

function showEmpty(title, sub) {
  document.getElementById('empty-state').style.display = '';
  document.getElementById('canvas-wrap').style.display = 'none';
  document.getElementById('empty-title').textContent = title;
  document.getElementById('empty-sub').textContent   = sub;
  showSpinner(false);
}

function clearCanvas() {
  const svg = document.getElementById('canvas');
  while (svg.firstChild) svg.removeChild(svg.firstChild);
}

// ── Renderer (identical logic to static exporter) ───────────────────────────
const BOX_W = 360, BOX_H_HEADER = 64, PILL_H = 28, PILL_ROW = 38;
const BOX_PAD = 18, COL_GAP = 12, API_GAP = 180, MARGIN_X = 60, MARGIN_Y = 50;
const CARD_RADIUS = 10, BADGE_W = 38;
const SVG_NS = 'http://www.w3.org/2000/svg';

let epById = {}, outById = {}, epToCallNodeIds = {}, callToEpNodeIds = {}, outConnTo = {}, epConnFrom = {};
let activeNodeId = null;

function el(tag, attrs, text) {
  const e = document.createElementNS(SVG_NS, tag);
  Object.entries(attrs || {}).forEach(([k, v]) => e.setAttribute(k, v));
  if (text !== undefined) e.textContent = text;
  return e;
}
function truncate(str, maxW, charPx = 6.5) {
  const maxChars = Math.floor(maxW / charPx);
  return str.length > maxChars ? str.slice(0, maxChars - 1) + '…' : str;
}

function initRenderer(DATA) {
  clearCanvas();
  showSpinner(false);

  // Reset lookup maps
  epById = {}; outById = {}; epToCallNodeIds = {}; callToEpNodeIds = {}; outConnTo = {}; epConnFrom = {};
  activeNodeId = null;

  DATA.apis.forEach(api => {
    api.entryPoints.forEach(ep   => { epById[ep.nodeId]  = { ...ep,  api: api.name }; });
    api.outboundCalls.forEach(o  => { outById[o.nodeId]  = { ...o,   api: api.name }; });
  });
  DATA.impacts.forEach(imp => {
    epToCallNodeIds[imp.entrypointNodeId] = imp.callNodeIds;
    imp.callNodeIds.forEach(cn => {
      (callToEpNodeIds[cn] = callToEpNodeIds[cn] || []).push(imp.entrypointNodeId);
    });
  });
  DATA.connections.forEach(c => {
    outConnTo[c.outboundCallNodeId] = c.matchedEntrypointNodeId || null;
    if (c.matchedEntrypointNodeId)
      (epConnFrom[c.matchedEntrypointNodeId] = epConnFrom[c.matchedEntrypointNodeId] || []).push(c.outboundCallNodeId);
  });

  // Layout
  const colW = (BOX_W - COL_GAP * 3) / 2;
  function boxHeight(api) {
    return BOX_H_HEADER + BOX_PAD + Math.max(api.entryPoints.length, api.outboundCalls.length, 1) * PILL_ROW + BOX_PAD;
  }
  const layout = {};
  let curX = MARGIN_X, maxH = 0;
  DATA.apis.forEach(api => {
    const h = boxHeight(api), bx = curX, by = MARGIN_Y;
    maxH = Math.max(maxH, h);
    layout[api.name] = {
      x: bx, y: by, w: BOX_W, h,
      scannedAt: api.scannedAt || '',
      epPills:  api.entryPoints.map((ep, i) => ({ nodeId: ep.nodeId, x: bx + BOX_PAD,              y: by + BOX_H_HEADER + BOX_PAD + i * PILL_ROW, w: colW,   h: PILL_H })),
      outPills: api.outboundCalls.map((o,  i) => ({ nodeId: o.nodeId,  x: bx + BOX_PAD + colW + COL_GAP, y: by + BOX_H_HEADER + BOX_PAD + i * PILL_ROW, w: colW,   h: PILL_H })),
    };
    curX += BOX_W + API_GAP;
  });

  const pillPos = {};
  DATA.apis.forEach(api => {
    const lay = layout[api.name];
    lay.epPills.forEach(p  => { pillPos[p.nodeId] = { ...p, cx: p.x,        cy: p.y + p.h / 2, side: 'left'  }; });
    lay.outPills.forEach(p => { pillPos[p.nodeId] = { ...p, cx: p.x + p.w,  cy: p.y + p.h / 2, side: 'right' }; });
  });

  const svgW = curX - API_GAP + MARGIN_X;
  const svgH = maxH + MARGIN_Y * 2;
  // Extra buffer so bezier control points arcing between cards are never clipped
  const BUF = 160;

  const svg = document.getElementById('canvas');
  svg.setAttribute('width',   svgW + BUF * 2);
  svg.setAttribute('height',  svgH + BUF * 2);
  svg.setAttribute('viewBox', (-BUF) + ' ' + (-BUF) + ' ' + (svgW + BUF * 2) + ' ' + (svgH + BUF * 2));

  const connLayer = el('g', { id: 'conn-layer' });
  const cardLayer = el('g', { id: 'card-layer' });
  const pillLayer = el('g', { id: 'pill-layer' });
  svg.appendChild(connLayer);
  svg.appendChild(cardLayer);
  svg.appendChild(pillLayer);

  DATA.apis.forEach(api => {
    const lay = layout[api.name];
    const g   = el('g', { 'data-api': api.name });
    g.appendChild(el('rect', { x: lay.x, y: lay.y, width: lay.w, height: lay.h, rx: CARD_RADIUS, class: 'api-card', 'data-api': api.name }));
    g.appendChild(el('text', { x: lay.x + BOX_W / 2, y: lay.y + 28, class: 'api-name', 'text-anchor': 'middle', 'data-api': api.name }, api.name));
    if (lay.scannedAt) {
      const ts = lay.scannedAt.length > 19 ? lay.scannedAt.slice(0, 19).replace('T', ' ') + ' UTC' : lay.scannedAt;
      g.appendChild(el('text', { x: lay.x + BOX_W / 2, y: lay.y + 48, class: 'api-scanned', 'text-anchor': 'middle' }, 'scanned ' + ts));
    }
    const divX = lay.x + BOX_PAD + colW + COL_GAP / 2;
    g.appendChild(el('line', { x1: divX, y1: lay.y + BOX_H_HEADER, x2: divX, y2: lay.y + lay.h, class: 'card-divider' }));
    const colLabelY = lay.y + BOX_H_HEADER - 6;
    g.appendChild(el('text', { x: lay.x + BOX_PAD + colW / 2,                    y: colLabelY, class: 'col-label', 'text-anchor': 'middle' }, 'Entry Points'));
    g.appendChild(el('text', { x: lay.x + BOX_PAD + colW + COL_GAP + colW / 2,   y: colLabelY, class: 'col-label', 'text-anchor': 'middle' }, 'Outbound Calls'));
    cardLayer.appendChild(g);

    lay.epPills.forEach((p, i) => {
      const ep = api.entryPoints[i];
      const pg = el('g', { 'data-ep': ep.nodeId, class: 'pill-ep-group', style: 'cursor:pointer' });
      pg.appendChild(el('rect', { x: p.x, y: p.y, width: p.w, height: p.h, class: 'pill-ep', 'data-ep': ep.nodeId }));
      pg.appendChild(el('rect', { x: p.x + 4, y: p.y + 4, width: BADGE_W, height: p.h - 8, class: 'pill-ep-badge', 'data-ep': ep.nodeId }));
      pg.appendChild(el('text', { x: p.x + 4 + BADGE_W / 2, y: p.y + p.h / 2 + 1, class: 'pill-ep-badge-text', 'text-anchor': 'middle', 'dominant-baseline': 'middle', 'data-ep': ep.nodeId }, ep.httpMethod));
      pg.appendChild(el('text', { x: p.x + BADGE_W + 10, y: p.y + p.h / 2 + 1, class: 'pill-ep-text', 'dominant-baseline': 'middle', 'data-ep': ep.nodeId }, truncate(ep.route, p.w - BADGE_W - 20)));
      pg.addEventListener('click', () => onEpClick(ep.nodeId));
      pillLayer.appendChild(pg);
    });

    lay.outPills.forEach((p, i) => {
      const out = api.outboundCalls[i];
      const pg  = el('g', { 'data-out': out.nodeId, class: 'pill-out-group', style: 'cursor:pointer' });
      pg.appendChild(el('rect', { x: p.x, y: p.y, width: p.w, height: p.h, class: 'pill-out', 'data-out': out.nodeId }));
      const ax = p.x + p.w - 10, ay = p.y + p.h / 2;
      pg.appendChild(el('polygon', { points: (ax-5) + ',' + (ay-5) + ' ' + (ax+3) + ',' + ay + ' ' + (ax-5) + ',' + (ay+5), class: 'pill-out-arrow', 'data-out': out.nodeId }));
      pg.appendChild(el('text', { x: p.x + 8, y: p.y + p.h / 2 + 1, class: 'pill-out-text', 'dominant-baseline': 'middle', 'data-out': out.nodeId }, truncate(out.targetApi + ': ' + out.targetRoute, p.w - 24)));
      pg.addEventListener('click', () => onOutClick(out.nodeId));
      pillLayer.appendChild(pg);
    });
  });

  // Connector lines
  DATA.connections.forEach(conn => {
    if (!conn.matchedEntrypointNodeId) return;
    const src = pillPos[conn.outboundCallNodeId], tgt = pillPos[conn.matchedEntrypointNodeId];
    if (!src || !tgt) return;
    const x1 = src.x + src.w, y1 = src.cy, x2 = tgt.x, y2 = tgt.cy;
    let d;
    if (x2 >= x1) {
      const cp = Math.max(60, (x2 - x1) * 0.45);
      d = 'M ' + x1 + ' ' + y1 + ' C ' + (x1+cp) + ' ' + y1 + ', ' + (x2-cp) + ' ' + y2 + ', ' + x2 + ' ' + y2;
    } else {
      const arcY = Math.max(...Object.values(layout).map(l => l.y + l.h)) + 40, cp = 80;
      d = 'M ' + x1 + ' ' + y1 + ' C ' + (x1+cp) + ' ' + y1 + ', ' + (x1+cp) + ' ' + arcY + ', ' + ((x1+x2)/2) + ' ' + arcY + ' S ' + (x2-cp) + ' ' + arcY + ', ' + (x2-cp) + ' ' + y2 + ' S ' + x2 + ' ' + y2 + ', ' + x2 + ' ' + y2;
    }
    connLayer.appendChild(el('path', { d, class: 'connector', 'data-conn-out': conn.outboundCallNodeId, 'data-conn-ep': conn.matchedEntrypointNodeId }));
    connLayer.appendChild(el('circle', { cx: x1, cy: y1, r: 3.5, class: 'connector-dot', 'data-conn-out': conn.outboundCallNodeId, 'data-conn-ep': conn.matchedEntrypointNodeId }));
    connLayer.appendChild(el('circle', { cx: x2, cy: y2, r: 3.5, class: 'connector-dot', 'data-conn-out': conn.outboundCallNodeId, 'data-conn-ep': conn.matchedEntrypointNodeId }));
  });

  document.getElementById('empty-state').style.display = 'none';
  document.getElementById('canvas-wrap').style.display = 'flex';
  svg.addEventListener('click', e => { if (!e.target.closest('[data-ep],[data-out]')) { clearHighlights(); closePanel(); } });

  // ── Pan + zoom via CSS transform ──────────────────────────────────────────
  let panX = 0, panY = 0, scale = 1;
  svg.style.transformOrigin = '0 0';

  function applyTransform() {
    svg.style.transform = 'translate(' + panX + 'px,' + panY + 'px) scale(' + scale + ')';
  }
  applyTransform();

  // Clone wrap to drop old listeners between refreshes
  const wrap = document.getElementById('canvas-wrap');
  const newWrap = wrap.cloneNode(false);
  while (wrap.firstChild) newWrap.appendChild(wrap.firstChild);
  wrap.parentNode.replaceChild(newWrap, wrap);
  newWrap.appendChild(svg);

  // Drag to pan
  newWrap.addEventListener('mousedown', e => {
    if (e.button !== 0) return;
    if (e.target.closest('[data-ep],[data-out]')) return;
    e.preventDefault();
    newWrap.classList.add('dragging');
    const startX = e.clientX - panX;
    const startY = e.clientY - panY;
    function onMove(ev) {
      panX = ev.clientX - startX;
      panY = ev.clientY - startY;
      applyTransform();
    }
    function onUp() {
      newWrap.classList.remove('dragging');
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    }
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  });

  // Scroll to zoom (centered on cursor)
  newWrap.addEventListener('wheel', e => {
    e.preventDefault();
    const rect    = newWrap.getBoundingClientRect();
    const mouseX  = e.clientX - rect.left;
    const mouseY  = e.clientY - rect.top;
    const oldScale = scale;
    scale = Math.min(3, Math.max(0.15, scale * (e.deltaY < 0 ? 1.1 : 0.9)));
    panX  = mouseX - (mouseX - panX) * (scale / oldScale);
    panY  = mouseY - (mouseY - panY) * (scale / oldScale);
    applyTransform();
  }, { passive: false });
}

// ── Interaction ──────────────────────────────────────────────────────────────
function clearHighlights() {
  activeNodeId = null;
  document.querySelectorAll('.active').forEach(e => e.classList.remove('active'));
  document.querySelectorAll('.dim').forEach(e => e.classList.remove('dim'));
}
function dimAll() {
  document.querySelectorAll('.api-card, .api-name, .col-label, .api-scanned').forEach(e => e.classList.add('dim'));
  document.querySelectorAll('.pill-ep, .pill-ep-text, .pill-ep-badge, .pill-ep-badge-text').forEach(e => e.classList.add('dim'));
  document.querySelectorAll('.pill-out, .pill-out-text, .pill-out-arrow').forEach(e => e.classList.add('dim'));
  document.querySelectorAll('.connector, .connector-dot').forEach(e => e.classList.add('dim'));
}
function activatePill(attr, id) {
  document.querySelectorAll('[' + attr + '="' + id + '"]').forEach(e => { e.classList.remove('dim'); e.classList.add('active'); });
}
function activateConnector(outId, epId) {
  document.querySelectorAll('[data-conn-out="' + outId + '"][data-conn-ep="' + epId + '"]').forEach(e => { e.classList.remove('dim'); e.classList.add('active'); });
}
function onEpClick(nodeId) {
  if (activeNodeId === nodeId) { clearHighlights(); closePanel(); return; }
  clearHighlights(); activeNodeId = nodeId;
  dimAll();
  activatePill('data-ep', nodeId);
  const calls = epToCallNodeIds[nodeId] || [];
  calls.forEach(callId => { activatePill('data-out', callId); const t = outConnTo[callId]; if (t) activateConnector(callId, t); });
  const ep = epById[nodeId];
  showEpPanel(ep, calls.map(id => outById[id]).filter(Boolean));
}
function onOutClick(nodeId) {
  if (activeNodeId === nodeId) { clearHighlights(); closePanel(); return; }
  clearHighlights(); activeNodeId = nodeId;
  dimAll();
  activatePill('data-out', nodeId);
  const ep = outConnTo[nodeId];
  if (ep) { activatePill('data-ep', ep); activateConnector(nodeId, ep); }
  const triggerers = callToEpNodeIds[nodeId] || [];
  triggerers.forEach(epId => activatePill('data-ep', epId));
  showOutPanel(outById[nodeId], triggerers.map(id => epById[id]).filter(Boolean), ep ? epById[ep] : null);
}
function closePanel() { document.getElementById('impact-panel').classList.remove('visible'); }
function escHtml(s) { return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
function renderTags(items, cls) { return items.map(t => '<span class="impact-tag ' + cls + '">' + escHtml(t) + '</span>').join(''); }

function showEpPanel(ep, calls) {
  document.getElementById('panel-title').textContent = 'Impact: ' + ep.api + ' · ' + ep.httpMethod + ' ' + ep.route;
  let html = '';
  if (!calls.length) { html = '<div class="impact-section"><p class="impact-empty">No outbound API calls reachable from this entry point.</p></div>'; }
  else {
    const byApi = {};
    calls.forEach(c => { (byApi[c.targetApi] = byApi[c.targetApi] || []).push(c.targetRoute); });
    html = Object.entries(byApi).map(([api, routes]) => '<div class="impact-section"><h3>Calls to ' + escHtml(api) + '</h3>' + renderTags(routes, 'out') + '</div>').join('');
  }
  document.getElementById('panel-body').innerHTML = html;
  document.getElementById('impact-panel').classList.add('visible');
}
function showOutPanel(out, eps, resolved) {
  document.getElementById('panel-title').textContent = 'Outbound: ' + out.targetApi + ' · ' + out.targetRoute;
  let html = '';
  if (resolved) html += '<div class="impact-section"><h3>Resolves to</h3>' + renderTags([resolved.api + ': ' + resolved.httpMethod + ' ' + resolved.route], 'ep') + '</div>';
  if (eps.length) html += '<div class="impact-section"><h3>Triggered by entry points on ' + escHtml(out.api) + '</h3>' + renderTags(eps.map(e => e.httpMethod + ' ' + e.route), 'ep') + '</div>';
  else html += '<div class="impact-section"><p class="impact-empty">Not reachable from any tracked entry point on ' + escHtml(out.api) + '.</p></div>';
  document.getElementById('panel-body').innerHTML = html;
  document.getElementById('impact-panel').classList.add('visible');
}
</script>
</body>
</html>
""";
}
