'use strict';

// DeskPanel web search. Plain JavaScript on purpose: no build step, nothing to load from outside, and the page's
// Content-Security-Policy forbids inline script, so everything lives here.

const $ = (id) => document.getElementById(id);
const HIGHLIGHT_START = '\u0001';
const HIGHLIGHT_END = '\u0002';
const STORE_KEY = 'dp.indexes';

const FIRST_PAGE = 50;   // hits asked for first: the cost of a search is mostly building the highlighted snippets

const state = {
  allowDownloads: true,
  actions: false,       // whether the server lets the web start audio processing and PDF conversion
  maxResults: 200,     // the server's ceiling, reported by /api/session
  lastQuery: '',
  indexes: [],          // [{name, documents, updatedUtc}]
  selected: new Set(),  // names ticked in the index panel
  terms: [],            // the highlight terms of the last search (what the server matched)
  hits: [],
  activeId: null,
  marks: [],            // <mark> elements of the open preview
  cur: -1,
  previewOpen: false,
  searchSeq: 0,         // guards against a slow earlier search overwriting a newer one
  previewSeq: 0,
};

class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}

async function api(path, { method = 'GET', json } = {}) {
  const init = { method, credentials: 'same-origin', headers: { 'X-DeskPanel': '1' } };
  if (json !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(json);
  }

  const response = await fetch(path, init);
  let data = null;
  try { data = await response.json(); } catch { /* an empty or non-JSON body */ }

  if (response.status === 401 && !path.startsWith('/api/login')) {
    showLogin();
    throw new ApiError('La sesión ha caducado.', 401);
  }

  if (!response.ok) {
    throw new ApiError((data && data.error) || `Error ${response.status}`, response.status);
  }

  return data;
}

// ---------- screens ----------
function showLogin() {
  $('app').hidden = true;
  $('login').hidden = false;
  $('password').value = '';
  $('password').focus();
}

async function showApp() {
  $('login').hidden = true;
  $('app').hidden = false;
  setupNav(state.actions);   // actions.js: Audio and Feeds appear only when the server allows actions
  await loadIndexes();
  $('q').focus();
}

function applySession(session) {
  state.allowDownloads = session.allowDownloads;
  state.maxResults = session.maxResults || state.maxResults;
  state.actions = !!session.actions;
}

async function start() {
  try {
    const session = await api('/api/session');
    applySession(session);
    if (session.authenticated) { await showApp(); } else { showLogin(); }
  } catch {
    showLogin();
  }
}

$('loginForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('loginError').textContent = '';
  try {
    await api('/api/login', { method: 'POST', json: { password: $('password').value } });
    applySession(await api('/api/session'));
    await showApp();
  } catch (error) {
    $('loginError').textContent = error.message;
    $('password').select();
  }
});

$('btnLogout').addEventListener('click', async () => {
  try { await api('/api/logout', { method: 'POST' }); } catch { /* signing out of a dead session is fine */ }
  closePreview();
  stopAudioPolling();
  closeFeedPreview();
  $('results').replaceChildren();
  $('status').textContent = '';
  showLogin();
});

// ---------- indexes ----------
async function loadIndexes() {
  state.indexes = await api('/api/indexes');
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(STORE_KEY)); } catch { /* storage may be blocked */ }

  // First visit (or an index renamed since): everything published is ticked.
  const names = state.indexes.map((i) => i.name);
  const keep = Array.isArray(saved) ? saved.filter((n) => names.includes(n)) : [];
  state.selected = new Set(keep.length ? keep : names);
  renderIndexes();
}

function renderIndexes() {
  const list = $('indexList');
  list.replaceChildren();
  $('indexEmpty').hidden = state.indexes.length > 0;

  for (const index of state.indexes) {
    const label = document.createElement('label');
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = state.selected.has(index.name);
    box.addEventListener('change', () => {
      if (box.checked) { state.selected.add(index.name); } else { state.selected.delete(index.name); }
      try { localStorage.setItem(STORE_KEY, JSON.stringify([...state.selected])); } catch { /* optional convenience */ }
      updateIndexCount();
    });

    const name = document.createElement('span');
    name.textContent = index.name;
    const meta = document.createElement('span');
    meta.className = 'meta';
    meta.textContent = `${index.documents.toLocaleString('es')} docs`;
    label.append(box, name, meta);
    list.append(label);
  }

  updateIndexCount();
}

function updateIndexCount() {
  $('indexCount').textContent = state.indexes.length ? `(${state.selected.size}/${state.indexes.length})` : '';
}

// ---------- panels (history / indexes) ----------
function togglePanel(panelId, buttonId, onOpen) {
  const panel = $(panelId);
  const open = panel.hidden;
  $('historyPanel').hidden = true;
  $('indexPanel').hidden = true;
  $('btnHistory').setAttribute('aria-expanded', 'false');
  $('btnIndexes').setAttribute('aria-expanded', 'false');
  if (open) {
    panel.hidden = false;
    $(buttonId).setAttribute('aria-expanded', 'true');
    if (onOpen) { onOpen(); }
  }
}

$('btnHistory').addEventListener('click', () => togglePanel('historyPanel', 'btnHistory', loadHistory));
$('btnIndexes').addEventListener('click', () => togglePanel('indexPanel', 'btnIndexes'));

function closePanels() {
  $('historyPanel').hidden = true;
  $('indexPanel').hidden = true;
  $('btnHistory').setAttribute('aria-expanded', 'false');
  $('btnIndexes').setAttribute('aria-expanded', 'false');
}

// ---------- history ----------
async function loadHistory() {
  const list = $('historyList');
  try {
    const entries = await api('/api/history');
    list.replaceChildren();
    $('historyEmpty').hidden = entries.length > 0;
    $('btnClearHistory').hidden = entries.length === 0;

    for (const entry of entries) {
      const row = document.createElement('li');
      const run = document.createElement('button');
      run.type = 'button';
      run.className = 'q';
      run.textContent = entry.query;
      run.addEventListener('click', () => { $('q').value = entry.query; closePanels(); search(); });

      const when = document.createElement('span');
      when.className = 'when';
      when.textContent = new Date(entry.usedUtc).toLocaleDateString('es', { day: '2-digit', month: '2-digit' });

      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'x';
      remove.textContent = '✕';
      remove.setAttribute('aria-label', `Quitar «${entry.query}» del histórico`);
      remove.addEventListener('click', async () => {
        await api(`/api/history?q=${encodeURIComponent(entry.query)}`, { method: 'DELETE' });
        loadHistory();
      });

      row.append(run, when, remove);
      list.append(row);
    }
  } catch (error) {
    $('status').textContent = error.message;
  }
}

$('btnClearHistory').addEventListener('click', async () => {
  if (!confirm('¿Borrar todo el histórico de búsquedas?')) { return; }
  await api('/api/history/clear', { method: 'POST' });
  loadHistory();
});

// ---------- search ----------
$('searchForm').addEventListener('submit', (event) => { event.preventDefault(); search(); });

async function search(max = FIRST_PAGE, queryOverride = null) {
  const query = (queryOverride ?? $('q').value).trim();
  if (!query) { return; }
  if (state.indexes.length && state.selected.size === 0) {
    $('status').textContent = 'Marca al menos un índice (botón «Índices»).';
    return;
  }

  closePanels();
  closePreview();
  const seq = ++state.searchSeq;
  $('status').textContent = 'Buscando…';
  $('results').replaceChildren();

  const params = new URLSearchParams({ q: query, max: String(max) });
  for (const name of state.selected) { params.append('idx', name); }

  try {
    const data = await api(`/api/search?${params}`);
    if (seq !== state.searchSeq) { return; }  // a newer search already took over
    state.terms = data.terms || [];
    state.hits = data.hits;
    state.lastQuery = query;
    renderResults(data);
  } catch (error) {
    if (seq === state.searchSeq) { $('status').textContent = error.message; }
  }
}

function renderResults(data) {
  const results = $('results');
  results.replaceChildren();

  if (data.hits.length === 0) {
    $('status').textContent = 'Sin resultados.';
    return;
  }

  $('status').textContent = data.total > data.hits.length
    ? `${data.hits.length} de ${data.total} resultados`
    : `${data.total} resultado${data.total === 1 ? '' : 's'}`;

  for (const hit of data.hits) {
    const card = document.createElement('article');
    card.className = 'hit';
    card.tabIndex = 0;
    card.setAttribute('role', 'button');
    card.dataset.id = hit.id;

    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = hit.name;
    const path = document.createElement('div');
    path.className = 'path';
    path.textContent = `${hit.index} / ${hit.path}`;
    const snippet = document.createElement('div');
    snippet.className = 'snippet';
    renderSnippet(snippet, hit.snippet);

    card.append(name, path, snippet);
    card.addEventListener('click', () => openPreview(hit));
    card.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openPreview(hit); } });
    results.append(card);
  }

  // More exist than were built: offer to ask again for up to the server's ceiling.
  if (data.total > data.hits.length && data.hits.length < state.maxResults) {
    const more = document.createElement('button');
    more.type = 'button';
    more.className = 'chip more';
    more.textContent = `Mostrar más (hasta ${Math.min(data.total, state.maxResults)})`;
    more.addEventListener('click', () => search(state.maxResults, state.lastQuery));
    results.append(more);
  }
}

// The server marks matches with control characters instead of HTML, so the page never has to trust markup.
function renderSnippet(element, text) {
  let inMark = false;
  let buffer = '';
  const flush = () => {
    if (!buffer) { return; }
    if (inMark) {
      const mark = document.createElement('mark');
      mark.textContent = buffer;
      element.append(mark);
    } else {
      element.append(document.createTextNode(buffer));
    }
    buffer = '';
  };

  for (const ch of text || '') {
    if (ch === HIGHLIGHT_START) { flush(); inMark = true; }
    else if (ch === HIGHLIGHT_END) { flush(); inMark = false; }
    else { buffer += ch; }
  }
  flush();
}

// ---------- preview ----------
async function openPreview(hit) {
  const seq = ++state.previewSeq;
  state.activeId = hit.id;
  for (const card of $('results').children) { card.classList.toggle('active', card.dataset.id === hit.id); }

  const wasOpen = state.previewOpen;
  state.previewOpen = true;
  $('preview').hidden = false;
  $('previewEmpty').hidden = true;
  $('previewTitle').textContent = hit.name;
  $('previewText').textContent = 'Cargando…';
  $('previewNote').hidden = true;
  $('matchInfo').textContent = '';
  state.marks = [];
  state.cur = -1;
  updateNavButtons();

  const download = $('btnDownload');
  download.hidden = !state.allowDownloads;
  download.href = `/api/file?id=${encodeURIComponent(hit.id)}`;

  // So the phone's Back button closes the preview instead of leaving the page.
  if (!wasOpen) { history.pushState({ preview: true }, ''); }

  const params = new URLSearchParams({ id: hit.id });
  for (const term of state.terms) { params.append('t', term); }

  try {
    const data = await api(`/api/preview?${params}`);
    if (seq !== state.previewSeq) { return; }
    renderPreviewText(data.text, data.matches);

    if (data.truncated) {
      $('previewNote').textContent = 'El documento es muy largo: se muestra solo el principio.';
      $('previewNote').hidden = false;
    }

    if (state.marks.length) { goTo(0); } else { $('matchInfo').textContent = 'Sin coincidencias en el texto'; }
  } catch (error) {
    if (seq === state.previewSeq) { $('previewText').textContent = error.message; }
  }
}

function renderPreviewText(text, flat) {
  const fragment = document.createDocumentFragment();
  state.marks = [];
  let position = 0;
  for (let i = 0; i < flat.length; i += 2) {
    const start = flat[i];
    const length = flat[i + 1];
    if (start < position) { continue; }
    if (start > position) { fragment.append(text.slice(position, start)); }
    const mark = document.createElement('mark');
    mark.textContent = text.slice(start, start + length);
    fragment.append(mark);
    state.marks.push(mark);
    position = start + length;
  }
  if (position < text.length) { fragment.append(text.slice(position)); }

  $('previewText').replaceChildren(fragment);
  $('previewText').scrollTop = 0;
  updateNavButtons();
}

function updateNavButtons() {
  const none = state.marks.length === 0;
  $('btnPrev').disabled = none;
  $('btnNext').disabled = none;
}

function goTo(index) {
  const count = state.marks.length;
  if (!count) { return; }
  if (state.cur >= 0) { state.marks[state.cur].classList.remove('cur'); }
  state.cur = ((index % count) + count) % count;
  const mark = state.marks[state.cur];
  mark.classList.add('cur');
  mark.scrollIntoView({ block: 'center' });
  $('matchInfo').textContent = `${state.cur + 1} de ${count}`;
}

$('btnNext').addEventListener('click', () => goTo(state.cur + 1));
$('btnPrev').addEventListener('click', () => goTo(state.cur - 1));

function closePreview() {
  if (!state.previewOpen) { return; }
  state.previewOpen = false;
  state.previewSeq++;
  $('preview').hidden = true;
  $('previewEmpty').hidden = false;
  for (const card of $('results').children) { card.classList.remove('active'); }
}

$('btnBack').addEventListener('click', () => {
  // Going back through the history entry keeps the browser's own Back in step.
  if (history.state && history.state.preview) { history.back(); } else { closePreview(); }
});
window.addEventListener('popstate', closePreview);

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') {
    if (!$('historyPanel').hidden || !$('indexPanel').hidden) { closePanels(); }
    else if (state.previewOpen && history.state && history.state.preview) { history.back(); }
    else { closePreview(); }
  } else if (event.key === 'F3' && state.previewOpen) {
    event.preventDefault();
    goTo(state.cur + (event.shiftKey ? -1 : 1));
  }
});

start();
